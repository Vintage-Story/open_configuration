using System;
using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenConfiguration;

/// <summary>
/// Backs <see cref="ConfigManager.RegisterSync"/>/<see cref="ConfigManager.LoadSynced{T}"/> over a single
/// network channel shared by every mod depending on OpenConfiguration. A mod registers a serializer (server)
/// or a handler (client) under a key of its own choosing; the server pushes every registered config to a
/// player once they finish joining, and the client dispatches incoming packets back to the matching handler.
/// </summary>
internal static class ConfigSync
{
    internal const string ChannelId = "openconfiguration";

    private record ServerProvider(Func<string> Serialize, Func<IServerPlayer, bool>? CanSend);

    private static readonly ConcurrentDictionary<string, ServerProvider> ServerProviders = new();
    private static readonly ConcurrentDictionary<string, Action<string>> ClientHandlers = new();

    internal static void InitServer(ICoreServerAPI api)
    {
        IServerNetworkChannel channel = api.Network.GetChannel(ChannelId);
        api.Event.PlayerNowPlaying += player =>
        {
            foreach ((string key, ServerProvider provider) in ServerProviders)
            {
                if (provider.CanSend != null && !provider.CanSend(player)) continue;
                channel.SendPacket(new ConfigSyncPacket { Key = key, Json = provider.Serialize() }, player);
            }
        };
    }

    internal static void InitClient(ICoreClientAPI api)
    {
        api.Network.GetChannel(ChannelId).SetMessageHandler<ConfigSyncPacket>(packet =>
        {
            if (ClientHandlers.TryGetValue(packet.Key, out Action<string>? apply)) apply(packet.Json);
        });
    }

    internal static void RegisterServerProvider(string key, Func<string> serialize, Func<IServerPlayer, bool>? canSend = null)
        => ServerProviders[key] = new ServerProvider(serialize, canSend);

    internal static void RegisterClientHandler(string key, Action<string> apply) => ClientHandlers[key] = apply;
}
