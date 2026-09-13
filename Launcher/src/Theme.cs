namespace SplitScreenLauncher;

internal static class Theme
{
    internal static readonly Color Back = Color.FromArgb(22, 20, 26);
    internal static readonly Color Bezel = Color.FromArgb(12, 11, 15);
    internal static readonly Color Surface = Color.FromArgb(36, 33, 42);
    internal static readonly Color Hover = Color.FromArgb(52, 48, 60);
    internal static readonly Color Border = Color.FromArgb(66, 61, 76);
    internal static readonly Color Text = Color.FromArgb(234, 230, 238);
    internal static readonly Color Muted = Color.FromArgb(152, 146, 162);
    internal static readonly Color Accent = Color.FromArgb(216, 164, 76);
    internal static readonly Color AccentHover = Color.FromArgb(232, 184, 100);

    private static readonly Color[] Players =
    [
        Color.FromArgb(92, 160, 255),
        Color.FromArgb(240, 100, 100),
        Color.FromArgb(100, 206, 124),
        Color.FromArgb(236, 198, 76)
    ];

    internal static Color Player(int seat) => Players[seat % Players.Length];

    internal static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }
}
