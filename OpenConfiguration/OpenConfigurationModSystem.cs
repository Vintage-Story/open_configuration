using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenConfiguration;

public class OpenConfigurationModSystem : ModSystem
{
    private const string HarmonyId = "openconfiguration.modssettingstab";

    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ConfigSync.ChannelId).RegisterMessageType<ConfigSyncPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api) => ConfigSync.InitServer(api);

    public override void StartClientSide(ICoreClientAPI api)
    {
        ConfigSync.InitClient(api);

        if (!Harmony.HasAnyPatches(HarmonyId))
        {
            new Harmony(HarmonyId).PatchAll();
        }
    }

    public override void Dispose()
    {
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
    }
}
