using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenConfiguration;

/// <summary>
/// A click-only icon button that uses the same visual style as GuiElementTextButton
/// (ButtonBackColor background, edge highlights, proper normal/hover/active states).
/// GuiElementToggleButton has hover disabled when an icon is set, so we use our own element.
/// </summary>
internal sealed class GuiElementIconClickButton : GuiElementControl
{
    private readonly string iconKey;
    private readonly ActionConsumable onClick;
    private LoadedTexture normalTex;
    private LoadedTexture hoverTex;
    private LoadedTexture activeTex;
    private bool isOver;
    private bool isDown;

    public override bool Focusable => enabled;

    public GuiElementIconClickButton(ICoreClientAPI api, string iconKey, ActionConsumable onClick, ElementBounds bounds)
        : base(api, bounds)
    {
        this.iconKey = iconKey;
        this.onClick = onClick;
        normalTex = new LoadedTexture(api);
        hoverTex = new LoadedTexture(api);
        activeTex = new LoadedTexture(api);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        int w = (int)Bounds.OuterWidth;
        int h = (int)Bounds.OuterHeight;

        ImageSurface normSurf = new ImageSurface(Format.Argb32, w, h);
        Context normCtx = genContext(normSurf);
        DrawButton(normCtx, w, h);
        generateTexture(normSurf, ref normalTex);
        normCtx.Dispose();
        ((Surface)normSurf).Dispose();

        // Hover overlay: semi-transparent white rectangle rendered on top of normalTex
        ImageSurface hoverSurf = new ImageSurface(Format.Argb32, w, h);
        Context hoverCtx = genContext(hoverSurf);
        hoverCtx.SetSourceRGBA(1.0, 1.0, 1.0, 0.1);
        hoverCtx.Rectangle(0, 0, w, h);
        hoverCtx.Fill();
        generateTexture(hoverSurf, ref hoverTex);
        hoverCtx.Dispose();
        ((Surface)hoverSurf).Dispose();

        // Active overlay: semi-transparent dark rectangle rendered on top of normalTex
        ImageSurface activeSurf = new ImageSurface(Format.Argb32, w, h);
        Context activeCtx = genContext(activeSurf);
        activeCtx.SetSourceRGBA(0.0, 0.0, 0.0, 0.4);
        activeCtx.Rectangle(0, 0, w, h);
        activeCtx.Fill();
        generateTexture(activeSurf, ref activeTex);
        activeCtx.Dispose();
        ((Surface)activeSurf).Dispose();
    }

    private void DrawButton(Context ctx, int w, int h)
    {
        // Background — matches EnumButtonStyle.Normal from GuiElementTextButton
        GuiElement.Rectangle(ctx, 0, 0, w, h);
        ctx.SetSourceRGBA(GuiStyle.ButtonBackColor);
        ctx.Fill();

        // Top-left edge highlight (Normal style)
        double edge = scaled(1.5);
        GuiElement.Rectangle(ctx, 0, 0, w - edge, edge);
        ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.15);
        ctx.Fill();
        GuiElement.Rectangle(ctx, 0, edge, edge, h - edge);
        ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.15);
        ctx.Fill();

        // Icon
        double pad = scaled(4.0);
        api.Gui.Icons.DrawIcon(ctx, iconKey,
            Bounds.absPaddingX + pad,
            Bounds.absPaddingY + pad,
            Bounds.InnerWidth - scaled(9.0),
            Bounds.InnerHeight - scaled(9.0),
            GuiStyle.DialogDefaultTextColor);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        bool over = Bounds.PointInside(api.Input.MouseX, api.Input.MouseY);
        api.Render.Render2DTexturePremultipliedAlpha(normalTex.TextureId, Bounds);
        if (isDown && over)
            api.Render.Render2DTexturePremultipliedAlpha(activeTex.TextureId, Bounds);
        else if (over)
            api.Render.Render2DTexturePremultipliedAlpha(hoverTex.TextureId, Bounds);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        bool wasOver = isOver;
        isOver = Bounds.PointInside(api.Input.MouseX, api.Input.MouseY);
        if (!wasOver && isOver)
            api.Gui.PlaySound("menubutton");
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        isDown = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        isDown = false;
        if (enabled && Bounds.PointInside(args.X, args.Y))
        {
            if (onClick())
                args.Handled = true;
            api.Gui.PlaySound("menubutton_press");
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        isDown = false;
        base.OnMouseUp(api, args);
    }

    public override void Dispose()
    {
        normalTex?.Dispose();
        hoverTex?.Dispose();
        activeTex?.Dispose();
        base.Dispose();
    }
}

internal static class GuiComposerIconClickButtonExtension
{
    internal static GuiComposer AddIconClickButton(this GuiComposer composer, string iconKey, ActionConsumable onClick, ElementBounds bounds, string? key = null)
    {
        if (!composer.Composed)
            composer.AddInteractiveElement(new GuiElementIconClickButton(composer.Api, iconKey, onClick, bounds), key);
        return composer;
    }
}
