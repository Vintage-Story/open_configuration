using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

namespace OpenConfiguration;

[HarmonyPatch(typeof(GuiCompositeSettings))]
internal static class ModsSettingsTabPatch
{
    private const string TabKey = "mods";

    // Bridges the two patched methods: updateButtonBounds() computes this tab's slot
    // (and makes room for it) before ComposerHeader() builds the composer that consumes it.
    private static readonly ConditionalWeakTable<GuiCompositeSettings, ElementBounds> ReservedBoundsByInstance = new();

    // GuiScreenSettings (main menu, before joining a world) uses a stub API where World/IsSinglePlayer
    // throw NotImplementedException, so the tab is only offered from the in-game pause menu.
    private static bool ShouldShowModsTab(GuiCompositeSettings instance)
    {
        IGameSettingsHandler? handler = Traverse.Create(instance).Field("handler").GetValue<IGameSettingsHandler>();
        if (handler == null || !handler.IsIngame) return false;

        ICoreClientAPI capi = handler.Api;
        return capi.IsSinglePlayer || (capi.World.Player?.HasPrivilege(Privilege.controlserver) ?? false);
    }

    [HarmonyPatch("updateButtonBounds")]
    [HarmonyPostfix]
    private static void UpdateButtonBoundsPostfix(GuiCompositeSettings __instance)
    {
        ReservedBoundsByInstance.Remove(__instance);
        if (!ShouldShowModsTab(__instance)) return;

        Traverse traverse = Traverse.Create(__instance);
        ElementBounds interfaceBounds = traverse.Field<ElementBounds>("iButtonBounds").Value;
        ElementBounds developerBounds = traverse.Field<ElementBounds>("dButtonBounds").Value;
        ElementBounds backBounds = traverse.Field<ElementBounds>("backButtonBounds").Value;

        ElementBounds lastVisibleTabBounds = ClientSettings.DeveloperMode ? developerBounds : interfaceBounds;

        CairoFont font = CairoFont.ButtonText();
        double width = font.GetTextExtents("Mods").Width / (double)ClientSettings.GUIScale + 15.0;

        ElementBounds modsBounds = ElementBounds.Fixed(0.0, 0.0, 0.0, 40.0)
            .WithFixedPadding(0.0, 3.0)
            .WithFixedWidth(width)
            .FixedRightOf(lastVisibleTabBounds, 15.0);

        // Only used in the in-game (pause menu) layout, but harmless to shift it either way -
        // this keeps the "Back" button (and the panel width computed from its position) in sync.
        backBounds.FixedRightOf(modsBounds, 25.0);

        ReservedBoundsByInstance.Add(__instance, modsBounds);
    }

    [HarmonyPatch("ComposerHeader")]
    [HarmonyPostfix]
    private static void ComposerHeaderPostfix(GuiCompositeSettings __instance, string currentTab, GuiComposer __result)
    {
        if (!ReservedBoundsByInstance.TryGetValue(__instance, out ElementBounds? modsBounds) || modsBounds == null) return;

        GuiElementToggleButton anchorTab = __result.GetToggleButton("developer") ?? __result.GetToggleButton("interface");
        if (anchorTab == null) return;

        modsBounds.ParentBounds = anchorTab.Bounds.ParentBounds;

        __result.AddToggleButton("Mods", CairoFont.ButtonText(), on => OnModsTabToggled(__instance, on), modsBounds, TabKey);
        __result.GetToggleButton(TabKey)?.SetValue(currentTab == TabKey);
    }

    private enum ConfigSource { Server, Client, Both }

    private record ModEntry(string Folder, ConfigSource Source);

    private record FileEntry(string Name, ConfigSource Source, bool IsFolder = false);

    private record FieldRow(string Key, JToken OriginalValue, bool IsReadOnly);

    private static void OnModsTabToggled(GuiCompositeSettings instance, bool on)
    {
        if (!on) return;

        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();
        ICoreClientAPI capi = handler.Api;

        // Server-side mod configs received when the player joined
        HashSet<string> serverFolders = ModConfigEditorSync.ServerIndex?.Mods.Keys.ToHashSet()
            ?? [];

        // Client-side mod configs (client-only mods)
        ModConfigIndex clientIndex = ModConfigEditorSync.BuildClientIndex(capi);
        HashSet<string> clientFolders = clientIndex.Mods.Keys.ToHashSet();

        List<ModEntry> entries = serverFolders.Union(clientFolders)
            .OrderBy(name => name)
            .Select(name =>
            {
                bool inServer = serverFolders.Contains(name);
                bool inClient = clientFolders.Contains(name);
                ConfigSource source = (inServer && inClient) ? ConfigSource.Both
                    : inServer ? ConfigSource.Server
                    : ConfigSource.Client;
                return new ModEntry(name, source);
            })
            .ToList();

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        composer = composer.AddStaticText("Mods", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 90.0, 400.0, 30.0));

        if (entries.Count == 0)
        {
            composer = composer.AddStaticText("No mod configurations found.", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0.0, 130.0, 400.0, 30.0));
        }
        else
        {
            double y = 130.0;
            foreach (ModEntry entry in entries)
            {
                ModEntry captured = entry;
                string label = entry.Source switch
                {
                    ConfigSource.Server => $"{entry.Folder} [Server]",
                    ConfigSource.Client => $"{entry.Folder} [Client]",
                    _ => entry.Folder
                };
                composer = composer.AddButton(
                    label,
                    () => { OnFolderContentsShown(instance, captured, "", capi); return true; },
                    ElementBounds.Fixed(0.0, y, 280.0, 30.0)
                );
                y += 40.0;
            }
        }

        composer = composer.Compose();

        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);
    }

    // subpath is the relative path within the mod folder ("" = root, "levelstats" = subfolder).
    // FileEntry.Name for files is the full relative key ("levelstats/axe"); for folders it is the subpath ("levelstats").
    private static void OnFolderContentsShown(GuiCompositeSettings instance, ModEntry mod, string subpath, ICoreClientAPI capi, int scrollTop = 0)
    {
        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();

        Dictionary<string, string>? serverMod = null;
        if (mod.Source is ConfigSource.Server or ConfigSource.Both)
            ModConfigEditorSync.ServerIndex?.Mods.TryGetValue(mod.Folder, out serverMod);
        HashSet<string> serverFiles = serverMod?.Keys.ToHashSet() ?? [];

        Dictionary<string, string>? clientMod = null;
        if (mod.Source is ConfigSource.Client or ConfigSource.Both)
            ModConfigEditorSync.BuildClientIndex(capi).Mods.TryGetValue(mod.Folder, out clientMod);
        HashSet<string> clientFiles = clientMod?.Keys.ToHashSet() ?? [];

        string prefix = subpath.Length > 0 ? subpath + "/" : "";
        HashSet<string> seenDirs = [];
        List<FileEntry> entries = [];

        foreach (string key in serverFiles.Union(clientFiles).OrderBy(k => k))
        {
            if (!key.StartsWith(prefix)) continue;
            string relative = key[prefix.Length..];
            int slash = relative.IndexOf('/');
            if (slash < 0)
            {
                bool inServer = serverFiles.Contains(key);
                bool inClient = clientFiles.Contains(key);
                ConfigSource src = (inServer && inClient) ? ConfigSource.Both
                    : inServer ? ConfigSource.Server : ConfigSource.Client;
                entries.Add(new FileEntry(key, src));
            }
            else
            {
                string dirKey = prefix + relative[..slash];
                if (!seenDirs.Add(dirKey)) continue;
                string dirPrefix = dirKey + "/";
                bool inServer = serverFiles.Any(k => k.StartsWith(dirPrefix));
                bool inClient = clientFiles.Any(k => k.StartsWith(dirPrefix));
                ConfigSource src = (inServer && inClient) ? ConfigSource.Both
                    : inServer ? ConfigSource.Server : ConfigSource.Client;
                entries.Add(new FileEntry(dirKey, src, IsFolder: true));
            }
        }

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        string headerText = subpath.Length > 0
            ? mod.Folder + " / " + subpath.Replace("/", " / ")
            : mod.Folder;
        string parentPath = subpath.Contains('/') ? subpath[..subpath.LastIndexOf('/')] : "";

        composer = composer
            .AddButton("Back",
                () => {
                    if (subpath.Length == 0) OnModsTabToggled(instance, true);
                    else OnFolderContentsShown(instance, mod, parentPath, capi);
                    return true;
                },
                ElementBounds.Fixed(0, 90, 70, 25))
            .AddStaticText(headerText, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(80, 93, 310, 25));

        const int visibleItems = 7;
        const double itemStep = 40;

        scrollTop = Math.Clamp(scrollTop, 0, Math.Max(0, entries.Count - visibleItems));
        int endIdx = Math.Min(scrollTop + visibleItems, entries.Count);

        if (entries.Count == 0)
        {
            composer = composer.AddStaticText("No configuration files found.", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, 130, 400, 30));
        }
        else
        {
            double y = 130;
            for (int i = scrollTop; i < endIdx; i++)
            {
                FileEntry entry = entries[i];
                FileEntry captured = entry;
                string displayName = entry.Name[prefix.Length..];
                if (entry.IsFolder) displayName += "/";
                string label = entry.Source switch
                {
                    ConfigSource.Server => $"{displayName} [Server]",
                    ConfigSource.Client => $"{displayName} [Client]",
                    _ => displayName
                };
                composer = composer.AddButton(
                    label,
                    () => {
                        if (captured.IsFolder) OnFolderContentsShown(instance, mod, captured.Name, capi);
                        else OnFileSelected(instance, mod, captured, subpath, capi);
                        return true;
                    },
                    ElementBounds.Fixed(0, y, 300, 30));
                y += itemStep;
            }

            bool hasPrev = scrollTop > 0;
            bool hasNext = endIdx < entries.Count;
            if (hasPrev || hasNext)
            {
                double navY = 130 + visibleItems * itemStep + 5;
                int capturedScrollTop = scrollTop;

                if (hasPrev)
                    composer = composer.AddButton("Prev",
                        () => { OnFolderContentsShown(instance, mod, subpath, capi, capturedScrollTop - visibleItems); return true; },
                        ElementBounds.Fixed(0, navY, 70, 25));

                string pageInfo = $"{scrollTop + 1}-{endIdx} / {entries.Count}";
                composer = composer.AddStaticText(pageInfo, CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(80, navY + 3, 190, 22));

                if (hasNext)
                    composer = composer.AddButton("Next",
                        () => { OnFolderContentsShown(instance, mod, subpath, capi, capturedScrollTop + visibleItems); return true; },
                        ElementBounds.Fixed(290, navY, 70, 25));
            }
        }

        composer = composer.Compose();
        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);
    }

    private static void OnFileSelected(GuiCompositeSettings instance, ModEntry mod, FileEntry file, string subpath, ICoreClientAPI capi)
    {
        JObject? jObj = null;
        try { jObj = JObject.Parse(GetFileContent(mod, file, capi)); } catch { }

        List<FieldRow> fields = jObj != null ? BuildFields(jObj) : [];
        Dictionary<string, JToken> values = fields.ToDictionary(f => f.Key, f => f.OriginalValue.DeepClone());

        ShowEditor(instance, mod, file, subpath, capi, jObj, fields, values);
    }

    private static List<FieldRow> BuildFields(JObject obj) =>
        obj.Properties()
            .Select(p =>
            {
                bool complex = p.Value.Type == JTokenType.Object
                    || (p.Value.Type == JTokenType.Array
                        && ((JArray)p.Value).Any(t => t.Type is JTokenType.Object or JTokenType.Array));
                return new FieldRow(p.Name, p.Value, complex);
            })
            .ToList();

    private static void ShowEditor(
        GuiCompositeSettings instance, ModEntry mod, FileEntry file, string subpath, ICoreClientAPI capi,
        JObject? original, List<FieldRow> fields, Dictionary<string, JToken> values,
        int scrollTop = 0)
    {
        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();

        const double rowH = 30.0, rowGap = 6.0, rowStep = rowH + rowGap;
        const double startY = 125.0;
        const double labelW = 200.0, inputX = 210.0, inputW = 130.0, removeX = 345.0, removeW = 22.0;
        const int visibleRows = 8;

        // Virtual scrolling: only add elements for the visible window of rows.
        // BeginClip is intentionally avoided — number input +/- buttons ignore clip bounds
        // and render outside the panel when clipping is used.
        scrollTop = Math.Clamp(scrollTop, 0, Math.Max(0, fields.Count - visibleRows));
        int endRow = Math.Min(scrollTop + visibleRows, fields.Count);

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        string editorHeader = subpath.Length > 0
            ? $"{mod.Folder} / {subpath} / {file.Name[(subpath.Length + 1)..]}"
            : $"{mod.Folder} / {file.Name}";
        composer = composer
            .AddButton("Back", () => { OnFolderContentsShown(instance, mod, subpath, capi); return true; },
                ElementBounds.Fixed(0, 90, 70, 25))
            .AddStaticText(editorHeader, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(80, 93, 320, 25));

        for (int i = scrollTop; i < endRow; i++)
        {
            FieldRow field = fields[i];
            string fieldKey = $"f{i}";
            double rowY = startY + (i - scrollTop) * rowStep;

            composer = composer.AddStaticText(field.Key, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(5, rowY, labelW, rowH));

            string capturedKey = field.Key;
            ElementBounds inputB = ElementBounds.Fixed(inputX, rowY, inputW, rowH);

            if (field.IsReadOnly)
            {
                composer = composer.AddStaticText("[complex]", CairoFont.WhiteDetailText(), inputB);
            }
            else if (field.OriginalValue.Type == JTokenType.Boolean)
            {
                // Fixed small bounds — switch renders at a fixed internal size, rowH padding centers it
                ElementBounds switchB = ElementBounds.Fixed(inputX, rowY + 5, 50, rowH - 10);
                composer = composer.AddSwitch(
                    on => values[capturedKey] = new JValue(on),
                    switchB, fieldKey);
            }
            else if (field.OriginalValue.Type is JTokenType.Integer or JTokenType.Float)
            {
                composer = composer.AddNumberInput(
                    inputB,
                    text => values[capturedKey] = new JValue(text),
                    CairoFont.WhiteDetailText(), fieldKey);
            }
            else
            {
                composer = composer.AddTextInput(
                    inputB,
                    text => values[capturedKey] = new JValue(text),
                    CairoFont.WhiteDetailText(), fieldKey);
            }

            composer = composer.AddButton("X",
                () => {
                    fields.RemoveAll(f => f.Key == capturedKey);
                    values.Remove(capturedKey);
                    int newScroll = Math.Clamp(scrollTop, 0, Math.Max(0, fields.Count - visibleRows));
                    ShowEditor(instance, mod, file, subpath, capi, original, fields, values, newScroll);
                    return true;
                },
                ElementBounds.Fixed(removeX, rowY, removeW, rowH));
        }

        bool hasPrev = scrollTop > 0;
        bool hasNext = endRow < fields.Count;
        double afterRowsY = startY + visibleRows * rowStep;
        double btnY;

        if (hasPrev || hasNext)
        {
            double navY = afterRowsY + 5;
            btnY = navY + 30;
            int capturedScrollTop = scrollTop;

            if (hasPrev)
            {
                composer = composer.AddButton("Prev",
                    () => { ShowEditor(instance, mod, file, subpath, capi, original, fields, values, capturedScrollTop - visibleRows); return true; },
                    ElementBounds.Fixed(0, navY, 70, 25));
            }

            string pageInfo = $"{scrollTop + 1}-{endRow} / {fields.Count}";
            composer = composer.AddStaticText(pageInfo, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(80, navY + 3, 190, 22));

            if (hasNext)
            {
                composer = composer.AddButton("Next",
                    () => { ShowEditor(instance, mod, file, subpath, capi, original, fields, values, capturedScrollTop + visibleRows); return true; },
                    ElementBounds.Fixed(290, navY, 70, 25));
            }
        }
        else
        {
            btnY = afterRowsY + 10;
        }

        composer = composer
            .AddButton("Save",
                () => { SaveFromForm(instance, mod, file, subpath, original, fields, values, capi); return true; },
                ElementBounds.Fixed(0, btnY, 80, 30))
            .AddButton("Add",
                () => { ShowAddEntryDialog(instance, mod, file, subpath, capi, original, fields, values, scrollTop); return true; },
                ElementBounds.Fixed(90, btnY, 80, 30));

        composer = composer.Compose();
        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);

        for (int i = scrollTop; i < endRow; i++)
        {
            FieldRow field = fields[i];
            if (field.IsReadOnly || !values.TryGetValue(field.Key, out JToken? val)) continue;
            string fieldKey = $"f{i}";
            if (field.OriginalValue.Type == JTokenType.Boolean)
                composer.GetSwitch(fieldKey)?.SetValue(val.Value<bool>());
            else if (field.OriginalValue.Type is JTokenType.Integer or JTokenType.Float)
            {
                GuiElementNumberInput? num = composer.GetNumberInput(fieldKey);
                if (num != null)
                {
                    num.IntMode = field.OriginalValue.Type == JTokenType.Integer;
                    num.SetValue((float)val.Value<double>());
                }
            }
            else
            {
                // Arrays are stored as JArray initially; display as compact JSON (no \n)
                string display = val.Type == JTokenType.Array
                    ? val.ToString(Formatting.None)
                    : val.ToString();
                composer.GetTextInput(fieldKey)?.SetValue(display);
            }
        }
    }

    private static void ShowAddEntryDialog(
        GuiCompositeSettings instance, ModEntry mod, FileEntry file, string subpath, ICoreClientAPI capi,
        JObject? original, List<FieldRow> fields, Dictionary<string, JToken> values,
        int returnScrollTop = 0)
    {
        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();

        string newKey = "";
        string newValue = "";

        string editorHeader = subpath.Length > 0
            ? $"{mod.Folder} / {subpath} / {file.Name[(subpath.Length + 1)..]}"
            : $"{mod.Folder} / {file.Name}";

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();
        composer = composer
            .AddButton("Back",
                () => { ShowEditor(instance, mod, file, subpath, capi, original, fields, values, returnScrollTop); return true; },
                ElementBounds.Fixed(0, 90, 70, 25))
            .AddStaticText(editorHeader, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(80, 93, 320, 25))
            .AddStaticText("Key:", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(5, 145, 100, 28))
            .AddTextInput(
                ElementBounds.Fixed(110, 143, 250, 30),
                text => newKey = text,
                CairoFont.WhiteDetailText(), "newEntryKey")
            .AddStaticText("Value:", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(5, 190, 100, 28))
            .AddTextInput(
                ElementBounds.Fixed(110, 188, 250, 30),
                text => newValue = text,
                CairoFont.WhiteDetailText(), "newEntryValue")
            .AddButton("Add",
                () => {
                    string key = newKey.Trim();
                    if (string.IsNullOrEmpty(key)) return true;
                    if (!values.ContainsKey(key))
                    {
                        JValue jval = new JValue(newValue);
                        fields.Add(new FieldRow(key, jval, false));
                        values[key] = jval.DeepClone();
                    }
                    ShowEditor(instance, mod, file, subpath, capi, original, fields, values, returnScrollTop);
                    return true;
                },
                ElementBounds.Fixed(0, 235, 80, 30));

        composer = composer.Compose();
        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);
    }

    private static void SaveFromForm(
        GuiCompositeSettings instance, ModEntry mod, FileEntry file, string subpath,
        JObject? original, List<FieldRow> fields, Dictionary<string, JToken> values,
        ICoreClientAPI capi)
    {
        JObject result = (JObject?)original?.DeepClone() ?? new JObject();

        HashSet<string> activeKeys = fields.Select(f => f.Key).ToHashSet();
        foreach (string key in result.Properties().Select(p => p.Name).ToList())
            if (!activeKeys.Contains(key)) result.Remove(key);

        foreach (FieldRow field in fields)
        {
            if (values.TryGetValue(field.Key, out JToken? raw))
                result[field.Key] = ConvertValue(raw, field.OriginalValue.Type);
        }
        SaveContent(instance, mod, file, subpath, result.ToString(Formatting.Indented), capi);
    }

    private static JToken ConvertValue(JToken raw, JTokenType target)
    {
        if (raw.Type == target) return raw;
        string s = raw.ToString();
        if (target == JTokenType.Array)
        {
            try { return JArray.Parse(s.Trim()); }
            catch { return raw; }
        }
        return target switch
        {
            JTokenType.Integer when long.TryParse(s, out long lv)   => new JValue(lv),
            JTokenType.Float   when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double dv) => new JValue(dv),
            JTokenType.Boolean when bool.TryParse(s, out bool bv)   => new JValue(bv),
            _ => raw
        };
    }

    private static string GetFileContent(ModEntry mod, FileEntry file, ICoreClientAPI capi)
    {
        if (file.Source is ConfigSource.Server or ConfigSource.Both)
        {
            if (ModConfigEditorSync.ServerIndex?.Mods.TryGetValue(mod.Folder, out var serverMod) == true
                && serverMod.TryGetValue(file.Name, out string? content))
                return content;
        }

        string localPath = LocalPath(capi.DataBasePath, mod.Folder, file.Name);
        return File.Exists(localPath) ? File.ReadAllText(localPath) : "";
    }

    private static void SaveContent(GuiCompositeSettings instance, ModEntry mod, FileEntry file, string subpath, string content, ICoreClientAPI capi)
    {
        if (file.Source is ConfigSource.Server or ConfigSource.Both)
        {
            capi.Network.GetChannel(ConfigSync.ChannelId).SendPacket(new ModConfigSavePacket
            {
                Folder = mod.Folder,
                FileName = file.Name,
                Content = content
            });
        }

        if (file.Source is ConfigSource.Client or ConfigSource.Both)
        {
            try
            {
                string localPath = LocalPath(capi.DataBasePath, mod.Folder, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                File.WriteAllText(localPath, content);
            }
            catch (Exception ex)
            {
                capi.Logger.Error("[OpenConfiguration] Failed to write local config: {0}", ex.Message);
            }
        }

        OnFolderContentsShown(instance, mod, subpath, capi);
    }

    // Builds an absolute path for a config file whose key may contain "/" for nested paths.
    private static string LocalPath(string dataBasePath, string modFolder, string fileKey)
    {
        string relativePart = fileKey.Replace('/', Path.DirectorySeparatorChar) + ".json";
        return Path.Combine(dataBasePath, "ModConfig", modFolder, relativePart);
    }
}
