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
        ConfigManager.LoadSyncedModConfig<OpenConfigurationServerConfig>(api, "OpenConfiguration", "server");
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        ConfigSync.InitClient(api);
        ModConfigEditorSync.RegisterClient(api);

        OpenConfigurationConfig localConfig = ConfigManager.Load<OpenConfigurationConfig>(api, "ModConfig", "OpenConfiguration");
        OpenConfigurationServerConfig serverConfig = new();
        serverConfig = ConfigManager.LoadSyncedModConfig<OpenConfigurationServerConfig>(
            api, "OpenConfiguration", "server", _ => Apply());

        void Apply()
        {
            if (serverConfig.EnableGui && localConfig.EnableGui)
            {
                if (!Harmony.HasAnyPatches(HarmonyId))
                    new Harmony(HarmonyId).PatchAll();
            }
            else
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            }
        }

        Apply();

        ConfigManager.WatchConfig<OpenConfigurationConfig>(api, "ModConfig", "OpenConfiguration", updated =>
        {
            localConfig.EnableGui = updated.EnableGui;
            Apply();
        });
    }

    public override void Dispose()
    {
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
    }
}
