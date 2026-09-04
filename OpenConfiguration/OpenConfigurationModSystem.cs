using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenConfiguration;

public class OpenConfigurationModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ConfigSync.ChannelId).RegisterMessageType<ConfigSyncPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api) => ConfigSync.InitServer(api);

    public override void StartClientSide(ICoreClientAPI api) => ConfigSync.InitClient(api);
}
