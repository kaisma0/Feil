#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
using Serilog;

namespace Feil.Core;

class Steam3Session
{
    public bool IsLoggedOn { get; private set; }

    public Dictionary<uint, ulong> AppTokens { get; } = [];
    public Dictionary<uint, ulong> PackageTokens { get; } = [];
    public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
    public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
    public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
    public Dictionary<string, byte[]> AppBetaPasswords { get; } = [];

    public SteamClient steamClient;
    public SteamUser steamUser;
    public SteamContent steamContent;
    readonly SteamApps steamApps;

    readonly CallbackManager callbacks;
    bool bConnecting;
    bool bAborted;
    bool bExpectingDisconnectRemote;
    bool bDidDisconnect;
    bool bIsConnectionRecovery;
    int connectionBackoff;
    int seq; // more hack fixes
    readonly CancellationTokenSource abortedToken = new();

    public Steam3Session()
    {
        var clientConfiguration = SteamConfiguration.Create(config =>
            config
                .WithHttpClientFactory(static purpose => HttpClientFactory.CreateHttpClient())
        );

        this.steamClient = new SteamClient(clientConfiguration);

        this.steamUser = this.steamClient.GetHandler<SteamUser>();
        this.steamApps = this.steamClient.GetHandler<SteamApps>();
        this.steamContent = this.steamClient.GetHandler<SteamContent>();

        this.callbacks = new CallbackManager(this.steamClient);

        this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
        this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
        this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);

        Log.Information("Connecting to Steam3...");
        Connect();
    }

    private readonly Lock steamLock = new();

    public bool WaitUntilCallback(Action submitter, Func<bool> waiter)
    {
        while (!bAborted && !waiter())
        {
            lock (steamLock)
            {
                submitter();
            }

            var seq = this.seq;
            do
            {
                lock (steamLock)
                {
                    callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
            } while (!bAborted && this.seq == seq && !waiter());
        }

        return bAborted;
    }

    public bool WaitForLogon()
    {
        if (IsLoggedOn || bAborted)
            return IsLoggedOn;

        WaitUntilCallback(() => { }, () => IsLoggedOn);

        return IsLoggedOn;
    }

    public async Task TickCallbacks()
    {
        var token = abortedToken.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await callbacks.RunWaitCallbackAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            //
        }
    }

    public async Task RequestAppInfo(uint appId, bool bForce = false)
    {
        if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
            return;

        var appTokens = await steamApps.PICSGetAccessTokens([appId], []);

        if (appTokens.AppTokensDenied.Contains(appId))
        {
            Log.Warning("Insufficient privileges to get access token for app {AppId}", appId);
        }

        foreach (var token_dict in appTokens.AppTokens)
        {
            this.AppTokens[token_dict.Key] = token_dict.Value;
        }

        var request = new SteamApps.PICSRequest(appId);

        if (AppTokens.TryGetValue(appId, out var token))
        {
            request.AccessToken = token;
        }

        if (ContentDownloader.Config.UseAppToken)
        {
            request.AccessToken = ContentDownloader.Config.AppToken;
        }

        var appInfoMultiple = await steamApps.PICSGetProductInfo([request], []);

        foreach (var appInfo in appInfoMultiple.Results)
        {
            foreach (var app_value in appInfo.Apps)
            {
                var app = app_value.Value;

                Log.Information("Got AppInfo for {AppId}", app.ID);
                AppInfo[app.ID] = app;
            }

            foreach (var app in appInfo.UnknownApps)
            {
                AppInfo[app] = null;
            }
        }
    }

    public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
    {
        var packages = packageIds.ToList();
        packages.RemoveAll(PackageInfo.ContainsKey);

        if (packages.Count == 0 || bAborted)
            return;

        var packageRequests = new List<SteamApps.PICSRequest>();

        foreach (var package in packages)
        {
            var request = new SteamApps.PICSRequest(package);

            if (PackageTokens.TryGetValue(package, out var token))
            {
                request.AccessToken = token;
            }

            if (ContentDownloader.Config.UsePackageToken)
            {
                request.AccessToken = ContentDownloader.Config.PackageToken;
            }

            packageRequests.Add(request);
        }

        var packageInfoMultiple = await steamApps.PICSGetProductInfo([], packageRequests);

        foreach (var packageInfo in packageInfoMultiple.Results)
        {
            foreach (var package_value in packageInfo.Packages)
            {
                var package = package_value.Value;
                PackageInfo[package.ID] = package;
            }

            foreach (var package in packageInfo.UnknownPackages)
            {
                PackageInfo[package] = null;
            }
        }
    }

    public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
    {
        if (bAborted)
            return 0;

        var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

        if (requestCode == 0)
        {
            Log.Warning("No manifest request code was returned for depot {DepotId} from app {AppId}, manifest {ManifestId}", depotId, appId, manifestId);
        }
        else
        {
            Log.Information("Got manifest request code for depot {DepotId} from app {AppId}, manifest {ManifestId}, result: {RequestCode}", depotId, appId, manifestId, requestCode);
        }

        return requestCode;
    }

    public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
    {
        var cdnKey = (depotid, server.Host);
        var completion = new TaskCompletionSource<SteamContent.CDNAuthToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
        {
            return;
        }

        try
        {
            Log.Debug("Requesting CDN auth token for {Host}", server.Host);

            var cdnAuth = await steamContent.GetCDNAuthToken(appid, depotid, server.Host);

            Log.Information("Got CDN auth token for {Host} result: {Result} (expires {Expiration})", server.Host, cdnAuth.Result, cdnAuth.Expiration);

            if (cdnAuth.Result != EResult.OK)
            {
                CDNAuthTokens.TryRemove(cdnKey, out _);
                completion.TrySetException(new InvalidOperationException(
                    $"Steam returned {cdnAuth.Result} for CDN auth token on {server.Host}."));
                return;
            }

            completion.TrySetResult(cdnAuth);
        }
        catch (Exception ex)
        {
            CDNAuthTokens.TryRemove(cdnKey, out _);
            completion.TrySetException(ex);
        }
    }

    public async Task CheckAppBetaPassword(uint appid, string password)
    {
        try
        {
            var appPassword = await steamApps.CheckAppBetaPassword(appid, password);

            Log.Information("Retrieved {Count} beta keys with result: {Result}", appPassword.BetaPasswords.Count, appPassword.Result);

            foreach (var entry in appPassword.BetaPasswords)
            {
                AppBetaPasswords[entry.Key] = entry.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check beta password for app {AppId}", appid);
        }
    }

    public async Task<KeyValue> GetPrivateBetaDepotSection(uint appid, string branch)
    {
        if (!AppBetaPasswords.TryGetValue(branch, out var branchPassword)) // Should be filled by CheckAppBetaPassword
        {
            return new KeyValue();
        }

        AppTokens.TryGetValue(appid, out var accessToken); // Should be filled by RequestAppInfo

        if (ContentDownloader.Config.UseAppToken)
        {
            accessToken = ContentDownloader.Config.AppToken;
        }

        try
        {
            var privateBeta = await steamApps.PICSGetPrivateBeta(appid, accessToken, branch, branchPassword);

            Log.Information("Retrieved private beta depot section for {AppId} with result: {Result}", appid, privateBeta.Result);

            return privateBeta.DepotSection;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get private beta depot section for app {AppId}", appid);
            return new KeyValue();
        }
    }

    private void ResetConnectionFlags()
    {
        bExpectingDisconnectRemote = false;
        bDidDisconnect = false;
        bIsConnectionRecovery = false;
    }

    void Connect()
    {
        bAborted = false;
        bConnecting = true;
        connectionBackoff = 0;

        ResetConnectionFlags();
        this.steamClient.Connect();
    }

    private void Abort(bool sendLogOff = true)
    {
        Disconnect(sendLogOff);
    }

    public void Disconnect(bool sendLogOff = true)
    {
        if (sendLogOff)
        {
            steamUser.LogOff();
        }

        bAborted = true;
        bConnecting = false;
        bIsConnectionRecovery = false;
        abortedToken.Cancel();
        steamClient.Disconnect();


        // flush callbacks until our disconnected event
        while (!bDidDisconnect)
        {
            callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
        }
    }

    private void Reconnect()
    {
        bIsConnectionRecovery = true;
        steamClient.Disconnect();
    }

    private void ConnectedCallback(SteamClient.ConnectedCallback connected)
    {
        Log.Information("Connected to Steam3!");
        bConnecting = false;

        // Update our tracking so that we don't time out, even if we need to reconnect multiple times.
        connectionBackoff = 0;

        Log.Information("Logging anonymously into Steam3...");
        steamUser.LogOnAnonymous();
    }

    private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
    {
        bDidDisconnect = true;

        Log.Debug("Disconnected: bIsConnectionRecovery = {IsConnectionRecovery}, UserInitiated = {UserInitiated}, bExpectingDisconnectRemote = {ExpectingDisconnectRemote}", bIsConnectionRecovery, disconnected.UserInitiated, bExpectingDisconnectRemote);

        // When recovering the connection, we want to reconnect even if the remote disconnects us
        if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
        {
            Log.Information("Disconnected from Steam");

            // Any operations outstanding need to be aborted
            bAborted = true;
        }
        else if (connectionBackoff >= 10)
        {
            Log.Error("Could not connect to Steam after 10 tries");
            Abort(false);
        }
        else if (!bAborted)
        {
            connectionBackoff += 1;

            if (bConnecting)
            {
                Log.Warning("Connection to Steam failed. Trying again (#{ConnectionBackoff})...", connectionBackoff);
            }
            else
            {
                Log.Warning("Lost connection to Steam. Reconnecting...");
            }

            Thread.Sleep(1000 * connectionBackoff);

            // Any connection related flags need to be reset here to match the state after Connect
            ResetConnectionFlags();
            steamClient.Connect();
        }
    }

    private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
    {
        if (loggedOn.Result == EResult.TryAnotherCM)
        {
            Log.Information("Retrying Steam3 connection (TryAnotherCM)...");

            Reconnect();

            return;
        }

        if (loggedOn.Result == EResult.ServiceUnavailable)
        {
            Log.Error("Unable to connect to Steam3: {Result}", loggedOn.Result);
            Abort(false);

            return;
        }

        if (loggedOn.Result != EResult.OK)
        {
            Log.Error("Unable to connect to Steam3: {Result}", loggedOn.Result);
            Abort();

            return;
        }

        Log.Information("Logged onto Steam3 successfully!");

        this.seq++;
        IsLoggedOn = true;

        if (ContentDownloader.Config.CellID == 0)
        {
            Log.Information("Using Steam3 suggested CellID: {CellID}", loggedOn.CellID);
            ContentDownloader.Config.CellID = (int)loggedOn.CellID;
        }
    }
}
