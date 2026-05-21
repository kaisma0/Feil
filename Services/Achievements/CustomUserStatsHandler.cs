using System;
using SteamKit2;
using SteamKit2.Internal;

namespace Feil.Services.Achievements;

// Custom SteamKit2 handler that intercepts ClientGetUserStatsResponse messages.
public sealed class CustomUserStatsHandler : ClientMsgHandler
{
    public Action<IPacketMsg, CMsgClientGetUserStatsResponse>? OnStatsResponse { get; set; }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType == EMsg.ClientGetUserStatsResponse)
        {
            var msg = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
            Serilog.Log.Debug("Intercepted ClientGetUserStatsResponse for GameId {GameId}, EResult {Result}", msg.Body.game_id, msg.Body.eresult);
            OnStatsResponse?.Invoke(packetMsg, msg.Body);
        }
    }
}
