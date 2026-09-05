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

    private record FileEntry(string Name, ConfigSource Source);

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
                    () => { OnModFolderSelected(instance, captured, capi); return true; },
                    ElementBounds.Fixed(0.0, y, 280.0, 30.0)
                );
                y += 40.0;
            }
        }

        composer = composer.Compose();

        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);
    }

    private static void OnModFolderSelected(GuiCompositeSettings instance, ModEntry entry, ICoreClientAPI capi)
    {
        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();

        Dictionary<string, string>? serverMod = null;
        if (entry.Source is ConfigSource.Server or ConfigSource.Both)
            ModConfigEditorSync.ServerIndex?.Mods.TryGetValue(entry.Folder, out serverMod);
        HashSet<string> serverFiles = serverMod?.Keys.ToHashSet() ?? [];

        Dictionary<string, string>? clientMod = null;
        if (entry.Source is ConfigSource.Client or ConfigSource.Both)
            ModConfigEditorSync.BuildClientIndex(capi).Mods.TryGetValue(entry.Folder, out clientMod);
        HashSet<string> clientFiles = clientMod?.Keys.ToHashSet() ?? [];

        List<FileEntry> files = serverFiles.Union(clientFiles)
            .OrderBy(name => name)
            .Select(name =>
            {
                bool inServer = serverFiles.Contains(name);
                bool inClient = clientFiles.Contains(name);
                ConfigSource source = (inServer && inClient) ? ConfigSource.Both
                    : inServer ? ConfigSource.Server
                    : ConfigSource.Client;
                return new FileEntry(name, source);
            })
            .ToList();

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        composer = composer
            .AddButton("Back", () => { OnModsTabToggled(instance, true); return true; },
                ElementBounds.Fixed(0.0, 90.0, 70.0, 25.0))
            .AddStaticText(entry.Folder, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(80.0, 90.0, 310.0, 30.0));

        if (files.Count == 0)
        {
            composer = composer.AddStaticText("No configuration files found.", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0.0, 130.0, 400.0, 30.0));
        }
        else
        {
            double y = 130.0;
            foreach (FileEntry file in files)
            {
                FileEntry captured = file;
                string label = file.Source switch
                {
                    ConfigSource.Server => $"{file.Name} [Server]",
                    ConfigSource.Client => $"{file.Name} [Client]",
                    _ => file.Name
                };
                composer = composer.AddButton(
                    label,
                    () => { OnFileSelected(instance, entry, captured, capi); return true; },
                    ElementBounds.Fixed(0.0, y, 280.0, 30.0)
                );
                y += 40.0;
            }
        }

        composer = composer.Compose();
        traverse.Field("composer").SetValue(composer);
        handler.LoadComposer(composer);
    }

    private static void OnFileSelected(GuiCompositeSettings instance, ModEntry mod, FileEntry file, ICoreClientAPI capi)
    {
        JObject? jObj = null;
        try { jObj = JObject.Parse(GetFileContent(mod, file, capi)); } catch { }

        List<FieldRow> fields = jObj != null ? BuildFields(jObj) : [];
        Dictionary<string, JToken> values = fields.ToDictionary(f => f.Key, f => f.OriginalValue.DeepClone());

        ShowEditor(instance, mod, file, capi, jObj, fields, values);
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
        GuiCompositeSettings instance, ModEntry mod, FileEntry file, ICoreClientAPI capi,
        JObject? original, List<FieldRow> fields, Dictionary<string, JToken> values,
        int scrollTop = 0)
    {
        Traverse traverse = Traverse.Create(instance);
        IGameSettingsHandler handler = traverse.Field("handler").GetValue<IGameSettingsHandler>();

        const double rowH = 30.0, rowGap = 6.0, rowStep = rowH + rowGap;
        const double startY = 125.0;
        const double labelW = 200.0, inputX = 210.0, inputW = 160.0;
        const int visibleRows = 8;

        // Virtual scrolling: only add elements for the visible window of rows.
        // BeginClip is intentionally avoided — number input +/- buttons ignore clip bounds
        // and render outside the panel when clipping is used.
        scrollTop = Math.Clamp(scrollTop, 0, Math.Max(0, fields.Count - visibleRows));
        int endRow = Math.Min(scrollTop + visibleRows, fields.Count);

        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        composer = composer
            .AddButton("Back", () => { OnModFolderSelected(instance, mod, capi); return true; },
                ElementBounds.Fixed(0, 90, 70, 25))
            .AddStaticText($"{mod.Folder} / {file.Name}", CairoFont.WhiteDetailText(),
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
                    () => { ShowEditor(instance, mod, file, capi, original, fields, values, capturedScrollTop - visibleRows); return true; },
                    ElementBounds.Fixed(0, navY, 70, 25));
            }

            string pageInfo = $"{scrollTop + 1}-{endRow} / {fields.Count}";
            composer = composer.AddStaticText(pageInfo, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(80, navY + 3, 190, 22));

            if (hasNext)
            {
                composer = composer.AddButton("Next",
                    () => { ShowEditor(instance, mod, file, capi, original, fields, values, capturedScrollTop + visibleRows); return true; },
                    ElementBounds.Fixed(290, navY, 70, 25));
            }
        }
        else
        {
            btnY = afterRowsY + 10;
        }

        composer = composer
            .AddButton("Save",
                () => { SaveFromForm(instance, mod, file, original, fields, values, capi); return true; },
                ElementBounds.Fixed(0, btnY, 80, 30))
            .AddButton("Cancel",
                () => { OnModFolderSelected(instance, mod, capi); return true; },
                ElementBounds.Fixed(90, btnY, 90, 30));

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

    private static void SaveFromForm(
        GuiCompositeSettings instance, ModEntry mod, FileEntry file,
        JObject? original, List<FieldRow> fields, Dictionary<string, JToken> values,
        ICoreClientAPI capi)
    {
        JObject result = (JObject?)original?.DeepClone() ?? new JObject();
        foreach (FieldRow field in fields)
        {
            if (values.TryGetValue(field.Key, out JToken? raw))
                result[field.Key] = ConvertValue(raw, field.OriginalValue.Type);
        }
        SaveContent(instance, mod, file, result.ToString(Formatting.Indented), capi);
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

        string localPath = Path.Combine(capi.DataBasePath, "ModConfig", mod.Folder, $"{file.Name}.json");
        return File.Exists(localPath) ? File.ReadAllText(localPath) : "";
    }

    private static void SaveContent(GuiCompositeSettings instance, ModEntry mod, FileEntry file, string content, ICoreClientAPI capi)
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
                string localPath = Path.Combine(capi.DataBasePath, "ModConfig", mod.Folder, $"{file.Name}.json");
                File.WriteAllText(localPath, content);
            }
            catch (Exception ex)
            {
                capi.Logger.Error("[OpenConfiguration] Failed to write local config: {0}", ex.Message);
            }
        }

        OnModFolderSelected(instance, mod, capi);
    }
}
