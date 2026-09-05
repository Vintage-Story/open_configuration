using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenConfiguration;

internal static class ModConfigEditorSync
{
    private const string IndexKey = "openconfiguration:editor_index";

    internal static ModConfigIndex? ServerIndex { get; private set; }

    internal static void RegisterServer(ICoreServerAPI api)
    {
        ConfigSync.RegisterServerProvider(IndexKey, () => BuildServerIndexJson(api));

        api.Network.GetChannel(ConfigSync.ChannelId)
            .SetMessageHandler<ModConfigSavePacket>((player, packet) => HandleSave(api, player, packet));
    }

    internal static void RegisterClient(ICoreClientAPI api)
    {
        ConfigSync.RegisterClientHandler(IndexKey, json =>
        {
            ServerIndex = JsonConvert.DeserializeObject<ModConfigIndex>(json) ?? new ModConfigIndex();
        });
    }

    internal static string BuildServerIndexJson(ICoreServerAPI api)
        => JsonConvert.SerializeObject(BuildIndexFromPath(Path.Combine(api.DataBasePath, "ModConfig")));

    internal static ModConfigIndex BuildClientIndex(ICoreClientAPI api)
        => BuildIndexFromPath(Path.Combine(api.DataBasePath, "ModConfig"));

    private static void HandleSave(ICoreServerAPI api, IServerPlayer player, ModConfigSavePacket packet)
    {
        if (!player.HasPrivilege(Privilege.controlserver))
        {
            api.Logger.Warning("[OpenConfiguration] {0} tried to save config without privileges", player.PlayerName);
            return;
        }

        // Reject names that look like path traversal
        if (string.IsNullOrWhiteSpace(packet.Folder) || string.IsNullOrWhiteSpace(packet.FileName)
            || packet.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || packet.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            api.Logger.Warning("[OpenConfiguration] {0} sent invalid folder/file name", player.PlayerName);
            return;
        }

        string modConfigPath = Path.GetFullPath(Path.Combine(api.DataBasePath, "ModConfig"));
        string targetPath = Path.GetFullPath(Path.Combine(modConfigPath, packet.Folder, $"{packet.FileName}.json"));
        if (!targetPath.StartsWith(modConfigPath + Path.DirectorySeparatorChar))
        {
            api.Logger.Warning("[OpenConfiguration] {0} attempted path traversal", player.PlayerName);
            return;
        }

        try { JToken.Parse(packet.Content); }
        catch (JsonException ex)
        {
            api.Logger.Warning("[OpenConfiguration] Save rejected for {0}/{1}: invalid JSON ({2})", packet.Folder, packet.FileName, ex.Message);
            return;
        }

        try
        {
            File.WriteAllText(targetPath, packet.Content);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[OpenConfiguration] Failed to write {0}: {1}", targetPath, ex.Message);
            return;
        }

        // Push refreshed index back to the player who saved
        api.Network.GetChannel(ConfigSync.ChannelId).SendPacket(
            new ConfigSyncPacket { Key = IndexKey, Json = BuildServerIndexJson(api) }, player);
    }

    private static ModConfigIndex BuildIndexFromPath(string modConfigPath)
    {
        ModConfigIndex index = new();
        if (!Directory.Exists(modConfigPath)) return index;

        foreach (string dir in Directory.GetDirectories(modConfigPath).OrderBy(d => d))
        {
            string folderName = new DirectoryInfo(dir).Name;
            Dictionary<string, string> files = new();

            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                files[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);

            index.Mods[folderName] = files;
        }

        return index;
    }
}
