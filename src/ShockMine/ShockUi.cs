using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShockMine;

/// <summary>
/// The countdown a seized player sees, so being unable to move reads as a timer running down
/// rather than as the game having locked up.
///
/// Drawn with TextMeshPro on our own canvas rather than IMGUI so it can carry the game's own
/// font and outline. The font is read off a live TMP element in the game's UI, which keeps the
/// countdown matching whatever the game ships even if that changes.
/// </summary>
internal static class ShockUi
{
    /// <summary>The warm off-white the text fades into, matching the game's own headings.</summary>
    private static readonly Color32 GradientBottom = new(0xFF, 0xFC, 0xD4, 0xFF);

    /// <summary>How long a refusal notice stays on screen.</summary>
    private const float DenialSeconds = 1.6f;

    /// <summary>Flashes per second while the refusal notice is showing.</summary>
    private const float DenialFlashHz = 4f;

    private static TextMeshProUGUI? label;
    private static CanvasGroup? group;
    private static float endTime;
    private static float denialEndTime;
    private static string denialText = string.Empty;

    /// <summary>Shown only to the player actually held.</summary>
    public static void BeginCountdown(float seconds) => endTime = Time.time + seconds;

    /// <summary>Shown when a use is refused, explaining which rule stopped it.</summary>
    public static void ShowDenial(string message)
    {
        denialText = message;
        denialEndTime = Time.time + DenialSeconds;
    }

    public static void Clear() => endTime = 0f;

    /// <summary>Driven every frame by <see cref="ModRunner"/>.</summary>
    public static void Tick()
    {
        float remaining = endTime - Time.time;
        float denialRemaining = denialEndTime - Time.time;

        if (remaining <= 0f && denialRemaining <= 0f)
        {
            if (group != null && group.alpha != 0f)
            {
                group.alpha = 0f;
            }

            return;
        }

        if (!EnsureLabel())
        {
            return;
        }

        group!.alpha = 1f;

        // A refusal takes the space: it is the more urgent of the two, and the two cannot
        // sensibly happen together anyway — a held player is not using items.
        if (denialRemaining > 0f)
        {
            label!.text = denialText;
            // Square wave rather than a fade, so it reads as a flash rather than a pulse.
            bool on = Mathf.Repeat(denialRemaining * DenialFlashHz, 1f) > 0.5f;
            label.alpha = on ? 1f : 0.15f;
            return;
        }

        label!.alpha = 1f;
        label.text = $"SEIZED  {Mathf.CeilToInt(remaining)}";
    }

    private static bool EnsureLabel()
    {
        if (label != null)
        {
            return true;
        }

        TMP_FontAsset? font = FindGameFont();
        if (font == null)
        {
            // The UI may simply not be loaded yet; try again next frame rather than give up.
            return false;
        }

        GameObject root = new("ShockMineCountdown");
        Object.DontDestroyOnLoad(root);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the game's HUD, below anything modal.
        canvas.sortingOrder = 500;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        // Match on height so the text keeps its size on ultrawide displays.
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        group = root.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        GameObject textObject = new("Label");
        textObject.transform.SetParent(root.transform, worldPositionStays: false);

        label = textObject.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = 68f;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;

        label.enableVertexGradient = true;
        label.colorGradient = new VertexGradient(
            Color.white,
            Color.white,
            GradientBottom,
            GradientBottom);

        // outlineWidth/outlineColor act on this label's own material instance, so the game's
        // shared font material is left untouched.
        label.outlineWidth = 0.2f;
        label.outlineColor = new Color32(0, 0, 0, 255);

        RectTransform rect = label.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -200f);
        rect.sizeDelta = new Vector2(1400f, 120f);

        return true;
    }

    private static TMP_FontAsset? FindGameFont()
    {
        TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
        if (texts.Length == 0)
        {
            return null;
        }

        return texts
            .Select(text => text.font)
            .Where(font => font != null)
            .GroupBy(font => font)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
    }
}
