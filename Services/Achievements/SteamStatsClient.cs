using System;
using Serilog;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Feil.Services.Achievements;

// Manages a SteamKit2 session for authenticated stats schema queries.
public sealed class SteamStatsClient : IDisposable
{
    private readonly SteamClient            _steamClient;
    private readonly CallbackManager        _callbackManager;
    private readonly SteamUser              _steamUser;
    private readonly CustomUserStatsHandler _statsHandler;

    private string  _username = string.Empty;
    private string? _password;
    private string? _refreshToken;

    private TaskCompletionSource<bool>?                           _logonTcs;
    private TaskCompletionSource<CMsgClientGetUserStatsResponse>? _statsTcs;
    private JobID?                                                _currentStatsJobId;

    private volatile bool _isRunning;
    private bool          _isRetryingAuth;
    private IAuthenticator? _authenticator;

    // Set after a fresh credential login yields a new refresh token.
    public string? ReceivedRefreshToken { get; private set; }

    public SteamID? CurrentUser => _steamClient.SteamID;

    public SteamStatsClient()
    {
        _steamClient     = new SteamClient();
        _statsHandler    = new CustomUserStatsHandler();
        _steamClient.AddHandler(_statsHandler);
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser       = _steamClient.GetHandler<SteamUser>()!;

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        _statsHandler.OnStatsResponse = (packet, response) =>
        {
            if (_statsTcs is { Task.IsCompleted: false } &&
                packet is PacketClientMsgProtobuf pkt &&
                pkt.TargetJobID == _currentStatsJobId)
            {
                _statsTcs.TrySetResult(response);
            }
        };
    }

    // Connect and authenticate using the given credentials.
    public async Task<bool> ConnectAndLogOnAsync(
        string username,
        string? password,
        string? refreshToken,
        IAuthenticator? authenticator = null)
    {
        Log.Debug("Starting ConnectAndLogOnAsync for user {Username}", username);
        _username       = username;
        _password       = password;
        _refreshToken   = refreshToken;
        _authenticator  = authenticator;
        _logonTcs       = new TaskCompletionSource<bool>();
        _isRunning      = true;

        // Start callback pump
        _ = Task.Run(CallbackLoop);

        _steamClient.Connect();
        return await _logonTcs.Task;
    }

    // Fetch the stats schema for a game from a specific owner.
    public async Task<CMsgClientGetUserStatsResponse?> GetStatsSchemaAsync(
        uint gameId, ulong ownerId)
    {
        Log.Debug("Sending ClientGetUserStats request for GameId {GameId} to owner {OwnerId}", gameId, ownerId);
        _statsTcs = new TaskCompletionSource<CMsgClientGetUserStatsResponse>();

        var request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats);
        request.Body.game_id              = gameId;
        request.Body.schema_local_version = -1;
        request.Body.crc_stats            = 0;
        request.Body.steam_id_for_user    = ownerId;
        request.SourceJobID               = _steamClient.GetNextJobID();
        _currentStatsJobId                = request.SourceJobID;

        _steamClient.Send(request);

        var timeoutTask   = Task.Delay(TimeSpan.FromSeconds(5));
        var completedTask = await Task.WhenAny(_statsTcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            Log.Warning("Timed out waiting for stats schema response for GameId {GameId} from owner {OwnerId}", gameId, ownerId);
            return null;
        }

        return await _statsTcs.Task;
    }

    public void Disconnect()
    {
        _isRunning      = false;
        _isRetryingAuth = false;
        _steamClient.Disconnect();
    }

    public void Dispose()
    {
        Disconnect();
    }

    // ── Callbacks ────────────────────────────────────────────────

    private async void OnConnected(SteamClient.ConnectedCallback _)
    {
        Log.Information("Connected to Steam. Proceeding with logon.");
        if (_refreshToken != null)
        {
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username               = _username,
                AccessToken            = _refreshToken,
                ShouldRememberPassword = true,
            });
            return;
        }

        // Fresh credential login
        try
        {
            var authSession = await _steamClient.Authentication
                .BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username            = _username,
                    Password            = _password,
                    IsPersistentSession = true,
                    Authenticator       = _authenticator ?? new UserConsoleAuthenticator(),
                });

            var pollResponse    = await authSession.PollingWaitForResultAsync();
            _refreshToken       = pollResponse.RefreshToken;
            ReceivedRefreshToken = _refreshToken;

            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username               = _username,
                AccessToken            = _refreshToken,
                ShouldRememberPassword = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Steam authentication failed");
            _logonTcs?.TrySetResult(false);
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback _)
    {
        Log.Information("Disconnected from Steam. (Retrying: {IsRetrying})", _isRetryingAuth);
        if (_isRetryingAuth)
        {
            _steamClient.Connect();
            return;
        }

        if (_logonTcs is { Task.IsCompleted: false })
            _logonTcs.TrySetResult(false);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            Log.Information("Successfully logged on to Steam.");
            _isRetryingAuth = false;
            _logonTcs?.TrySetResult(true);
        }
        else if ((callback.Result is EResult.InvalidPassword or EResult.AccessDenied)
                 && _refreshToken != null)
        {
            // Saved refresh token expired — signal failure so caller can re-prompt
            SteamCredentialStore.Delete();
            _refreshToken   = null;
            _isRetryingAuth = false;
            _logonTcs?.TrySetResult(false);
        }
        else
        {
            Log.Error("Steam logon failed: {Result} / {ExtendedResult}", callback.Result, callback.ExtendedResult);
            _isRetryingAuth = false;
            _logonTcs?.TrySetResult(false);
        }
    }

    private void CallbackLoop()
    {
        while (_isRunning)
        {
            _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
    }
}
