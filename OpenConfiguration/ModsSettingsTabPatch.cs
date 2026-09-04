using System.Runtime.CompilerServices;
using HarmonyLib;
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

    private static void OnModsTabToggled(GuiCompositeSettings instance, bool on)
    {
        if (!on) return;

        Traverse traverse = Traverse.Create(instance);
        GuiComposer composer = traverse.Method("ComposerHeader", "gamesettings-mods", TabKey).GetValue<GuiComposer>();

        composer = composer
            .AddStaticText("Mods", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0.0, 90.0, 400.0, 30.0))
            .AddStaticText("Coming soon.", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0.0, 130.0, 400.0, 30.0))
            .Compose();

        traverse.Field("composer").SetValue(composer);
        traverse.Field("handler").GetValue<IGuiCompositeHandler>().LoadComposer(composer);
    }
}
