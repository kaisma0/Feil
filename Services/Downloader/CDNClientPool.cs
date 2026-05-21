#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2.CDN;
using Serilog;

namespace Feil.Core;

/// <summary>
/// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed.
/// Servers are selected via atomic round-robin so all parallel chunk tasks fan out across
/// the full server list rather than hammering a single host.
/// </summary>
class CDNClientPool
{
    private readonly Steam3Session steamSession;
    private readonly uint appId;
    public Client CDNClient { get; }
    public Server ProxyServer { get; private set; }

    private volatile Server[] servers = [];
    private long _nextServer;

    public CDNClientPool(Steam3Session steamSession, uint appId)
    {
        this.steamSession = steamSession;
        this.appId = appId;
        CDNClient = new Client(steamSession.steamClient);
    }

    public async Task UpdateServerList()
    {
        Log.Information("Updating CDN server list for app {AppId}", appId);
        var rawServers = await this.steamSession.steamContent.GetServersForSteamPipe((uint?)ContentDownloader.Config.CellID);

        ProxyServer = rawServers.Where(x => x.UseAsProxy).FirstOrDefault();

        var weightedCdnServers = rawServers
            .Where(server =>
            {
                var isEligibleForApp = server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId);
                return isEligibleForApp && (server.Type == "SteamCache" || server.Type == "CDN");
            })
            .Select(server =>
            {
                AccountSettingsStore.Instance.ContentServerPenalty.TryGetValue(server.Host, out var penalty);
                return (server, penalty);
            })
            .OrderBy(pair => pair.penalty)
            .ThenBy(pair => pair.server.WeightedLoad);

        var list = new List<Server>();
        foreach (var (server, _) in weightedCdnServers)
        {
            for (var i = 0; i < server.NumEntries; i++)
                list.Add(server);
        }

        if (list.Count == 0)
        {
            var ex = new Exception("Failed to retrieve any download servers.");
            Log.Error(ex, "Could not get any valid servers from SteamPipe for app {AppId}", appId);
            throw ex;
        }

        Interlocked.Exchange(ref _nextServer, 0);
        servers = list.ToArray();
    }

    public Server GetConnection()
    {
        Log.Debug("Requesting CDN connection");
        var snap = servers;

        if (snap.Length == 0)
            throw new InvalidOperationException("No CDN servers are available. Call UpdateServerList() before requesting a connection.");

        var idx = (int)((Interlocked.Increment(ref _nextServer) & long.MaxValue) % snap.Length);
        return snap[idx];
    }

    public void ReturnConnection(Server server)
    {
        // Nothing to track on a successful return.
        // ContentServerPenalty reduction could go here if desired.
    }

    public void ReturnBrokenConnection(Server server)
    {

        Log.Warning("Returning broken connection for server {Host}", server.Host);
        if (server == null) return;

        // Increment penalty for this host so it sorts lower on the next server list refresh.
        AccountSettingsStore.Instance.ContentServerPenalty.AddOrUpdate(
            server.Host,
            addValue: 1,
            updateValueFactory: (_, existing) => existing + 1);
    }
}
