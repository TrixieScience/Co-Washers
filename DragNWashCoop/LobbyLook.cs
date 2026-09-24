using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace DragNWashCoop;

/// <summary>Colours, font and shapes of the co-op menu, in the style of the game's menu stickers.</summary>
internal static class LobbyLook
{
    // the stickers' own colours: yellow Multiplayer, green New Game, blue Options, pink Continue, ...
    internal static readonly Color Ink = Hex(0x2A2522), Cream = Hex(0xFFF3DA), Yellow = Hex(0xF5C633),
        Green = Hex(0x63C06A), Blue = Hex(0x5B9BD8), Pink = Hex(0xE07E8C), Orange = Hex(0xEFA962),
        Teal = Hex(0x5CCDB0), RowBack = new(.06f, .045f, .04f, .74f);

    private static TMP_FontAsset? font;
    private static bool fontLoaded;
    private static Material? titleMaterial, labelMaterial;
    private static Sprite? rounded, circle;
    private static Texture2D? shade;

    internal static Color Hex(int rgb, float alpha = 1f) =>
        new((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f, alpha);

    internal static Color Fade(Color colour, float alpha) => new(colour.r, colour.g, colour.b, alpha);

    /// <summary>
    /// Chewy (Sideshow, Apache 2.0): rounded hand-drawn letters like the lettering on the game's stickers.
    /// Falls back to the game's own font, which also covers names Chewy has no letters for.
    /// </summary>
    internal static TMP_FontAsset? Font
    {
        get
        {
            if (!fontLoaded) { fontLoaded = true; font = LoadFont(); }
            return font;
        }
    }

    /// <summary>Big titles: thick ink outline and a soft shadow.</summary>
    internal static Material? TitleMaterial => titleMaterial ??= Styled(.26f, "title");

    /// <summary>Labels over the game's scenery: thin ink outline and a soft shadow.</summary>
    internal static Material? LabelMaterial => labelMaterial ??= Styled(.14f, "label");

    private static TMP_FontAsset? LoadFont()
    {
        TMP_FontAsset? fallback = null;
        try { fallback = TMP_Settings.defaultFontAsset; } catch { }
        if (fallback == null) fallback = Resources.Load<TMP_FontAsset>("fonts & materials/LiberationSans SDF");
        try
        {
            // TextMeshPro reads a font from a file, so the embedded one is copied out once
            string path = Path.Combine(Paths.CachePath, "DragNWashCoop-Chewy-Regular.ttf");
            using (var stream = typeof(LobbyLook).Assembly.GetManifestResourceStream("DragNWashCoop.Assets.Chewy-Regular.ttf"))
            {
                if (stream == null) return fallback;
                if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
                {
                    try
                    {
                        Directory.CreateDirectory(Paths.CachePath);
                        string temp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id;
                        using (var file = File.Create(temp)) stream.CopyTo(file);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(temp, path);
                    }
                    catch (IOException) { if (!File.Exists(path)) throw; }   // another copy of the game wrote it
                }
            }
            var chewy = TMP_FontAsset.CreateFontAsset(path, 0, 90, 12, GlyphRenderMode.SDFAA, 1024, 1024);
            if (chewy == null) return fallback;
            chewy.name = "Chewy (co-op menu)";
            if (fallback != null)
            {
                chewy.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
                if (chewy.material.shader == null || !chewy.material.shader.isSupported) chewy.material.shader = fallback.material.shader;
            }
            chewy.hideFlags = HideFlags.DontUnloadUnusedAsset;
            chewy.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return chewy;
        }
        catch (Exception e)
        {
            CoopPlugin.Instance?.Log.LogWarning($"Co-op menu font could not be loaded, using the game's: {e.Message}");
            return fallback;
        }
    }

    private static Material? Styled(float outline, string name)
    {
        if (Font == null) return null;
        var material = new Material(Font.material) { name = "Co-op menu " + name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        ShaderUtilities.GetShaderPropertyIDs();
        material.EnableKeyword(ShaderUtilities.Keyword_Outline);
        material.SetFloat(ShaderUtilities.ID_OutlineWidth, outline);
        material.SetColor(ShaderUtilities.ID_OutlineColor, Ink);
        material.EnableKeyword(ShaderUtilities.Keyword_Underlay);
        material.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, .55f));
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, .45f);
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -.6f);
        material.SetFloat(ShaderUtilities.ID_UnderlayDilate, outline * 2f);
        material.SetFloat(ShaderUtilities.ID_UnderlaySoftness, .35f);
        ShaderUtilities.UpdateShaderRatios(material);
        return material;
    }

    private static readonly Dictionary<string, Sprite?> art = new();

    /// <summary>Art embedded in this DLL (Assets/*.png), so installing stays one file. Loaded once, kept across scenes.</summary>
    internal static Sprite? Art(string file)
    {
        if (!art.TryGetValue(file, out var sprite) || sprite == null) art[file] = sprite = LoadSprite(file);
        return sprite;
    }

    private static Sprite? LoadSprite(string file)
    {
        using var stream = typeof(LobbyLook).Assembly.GetManifestResourceStream("DragNWashCoop.Assets." + file);
        if (stream == null) return null;
        var bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = file };
        if (!texture.LoadImage(bytes)) return null;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;   // survives scene loads, reused on every menu visit
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f);
        sprite.name = file;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    /// <summary>A rounded rectangle for sliced images; corners are 24 units at a pixels-per-unit multiplier of 1.</summary>
    internal static Sprite Rounded => rounded ??= MakeRounded();

    internal static Sprite Circle => circle ??= MakeCircle();

    /// <summary>Darkens the left of the screen for the menu and fades out towards the right, over the scenery.</summary>
    internal static Texture2D Shade => shade ??= MakeShade();

    private static Sprite MakeRounded()
    {
        const int size = 64, radius = 24;
        var texture = NewTexture(size, size);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float px = x + .5f, py = y + .5f;
            float qx = Mathf.Max(radius - px, px - (size - radius), 0f), qy = Mathf.Max(radius - py, py - (size - radius), 0f);
            float alpha = Mathf.Clamp01(.5f - (Mathf.Sqrt(qx * qx + qy * qy) - radius));
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
        }
        texture.SetPixels32(pixels);
        texture.Apply();
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0,
                                   SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static Sprite MakeCircle()
    {
        const int size = 128;
        var texture = NewTexture(size, size);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x + .5f - size / 2f, dy = y + .5f - size / 2f;
            float alpha = Mathf.Clamp01(.5f - (Mathf.Sqrt(dx * dx + dy * dy) - (size / 2f - 1f)));
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
        }
        texture.SetPixels32(pixels);
        texture.Apply();
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f);
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static Texture2D MakeShade()
    {
        const int width = 256;
        var texture = NewTexture(width, 1);
        for (int x = 0; x < width; x++)
        {
            float t = x / (width - 1f);
            float alpha = t < .38f ? Mathf.Lerp(.9f, .7f, t / .38f) : Mathf.Lerp(.7f, 0f, Mathf.SmoothStep(0f, 1f, (t - .38f) / .42f));
            texture.SetPixel(x, 0, new Color(.05f, .035f, .03f, alpha));
        }
        texture.Apply();
        return texture;
    }

    private static Texture2D NewTexture(int width, int height) =>
        new(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
}

/// <summary>
/// Hover and keyboard/controller focus in the co-op menu: stickers grow and get a white edge like the
/// game's own, text buttons change colour, slide right and show an arrow.
/// </summary>
internal sealed class LobbyHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    internal Graphic? Tint;
    internal Color Normal, Hot;
    internal Graphic? Show;             // faded in: the arrow, or the sticker's white edge (made invisible)
    internal float ShowAlpha = 1f;
    internal RectTransform? Slide;
    internal float SlideBy;
    internal float Grow = 1f;
    private Selectable? selectable;
    private bool pointer, focus, captured;
    private float amount;
    private Vector2 slideFrom;

    public void OnPointerEnter(PointerEventData eventData) => pointer = true;
    public void OnPointerExit(PointerEventData eventData) => pointer = false;
    public void OnSelect(BaseEventData eventData) => focus = true;
    public void OnDeselect(BaseEventData eventData) => focus = false;

    private void Update()
    {
        if (!captured)
        {
            captured = true;
            selectable = GetComponent<Selectable>();
            if (Slide != null) slideFrom = Slide.anchoredPosition;
        }
        bool on = (pointer || focus) && (selectable == null || selectable.IsInteractable());
        amount = Mathf.MoveTowards(amount, on ? 1f : 0f, Time.unscaledDeltaTime * 9f);
        float e = amount * amount * (3f - 2f * amount);
        if (Tint != null) Tint.color = Color.Lerp(Normal, Hot, e);
        if (Show != null) Show.color = LobbyLook.Fade(Show.color, ShowAlpha * e);
        if (Slide != null) Slide.anchoredPosition = slideFrom + new Vector2(SlideBy * e, 0f);
        if (Grow != 1f) transform.localScale = Vector3.one * Mathf.Lerp(1f, Grow, e);
    }

    private void OnDisable() => pointer = focus = false;
}
