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
        ConfigSync.RegisterServerProvider(IndexKey, () => BuildServerIndexJson(api),
            player => player.HasPrivilege(Privilege.controlserver));

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

        // Reject names that look like path traversal.
        // FileName may contain "/" for nested paths (e.g. "levelstats/axe") — validate each component.
        string[] fileNameParts = packet.FileName.Split('/');
        if (string.IsNullOrWhiteSpace(packet.Folder)
            || packet.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileNameParts.Length == 0
            || fileNameParts.Any(p => string.IsNullOrWhiteSpace(p) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            api.Logger.Warning("[OpenConfiguration] {0} sent invalid folder/file name", player.PlayerName);
            return;
        }

        string modConfigPath = Path.GetFullPath(Path.Combine(api.DataBasePath, "ModConfig"));
        string relativeFile = string.Join(Path.DirectorySeparatorChar, fileNameParts) + ".json";
        string targetPath = Path.GetFullPath(Path.Combine(modConfigPath, packet.Folder, relativeFile));
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

        ConfigManager.TriggerReload(targetPath);

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
            IndexDirectory(dir, dir, files);
            if (files.Count > 0)
                index.Mods[folderName] = files;
        }

        return index;
    }

    private static void IndexDirectory(string rootDir, string currentDir, Dictionary<string, string> files)
    {
        foreach (string file in Directory.GetFiles(currentDir, "*.json").OrderBy(f => f))
        {
            // Key is relative path with forward slashes and no .json extension (e.g. "levelstats/axe")
            string relative = Path.GetRelativePath(rootDir, file);
            string key = relative[..^5].Replace(Path.DirectorySeparatorChar, '/');
            files[key] = File.ReadAllText(file);
        }

        foreach (string subDir in Directory.GetDirectories(currentDir).OrderBy(d => d))
            IndexDirectory(rootDir, subDir, files);
    }
}
