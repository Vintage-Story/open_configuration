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

    private static readonly ConcurrentDictionary<string, Func<string>> ServerProviders = new();
    private static readonly ConcurrentDictionary<string, Action<string>> ClientHandlers = new();

    internal static void InitServer(ICoreServerAPI api)
    {
        IServerNetworkChannel channel = api.Network.GetChannel(ChannelId);
        api.Event.PlayerNowPlaying += player =>
        {
            foreach ((string key, Func<string> serialize) in ServerProviders)
            {
                channel.SendPacket(new ConfigSyncPacket { Key = key, Json = serialize() }, player);
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

    internal static void RegisterServerProvider(string key, Func<string> serialize) => ServerProviders[key] = serialize;

    internal static void RegisterClientHandler(string key, Action<string> apply) => ClientHandlers[key] = apply;
}
