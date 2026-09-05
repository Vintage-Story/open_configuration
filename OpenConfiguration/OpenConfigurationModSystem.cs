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
        api.Network.RegisterChannel(ConfigSync.ChannelId)
            .RegisterMessageType<ConfigSyncPacket>()
            .RegisterMessageType<ModConfigSavePacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        ConfigSync.InitServer(api);
        ModConfigEditorSync.RegisterServer(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        ConfigSync.InitClient(api);
        ModConfigEditorSync.RegisterClient(api);

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
