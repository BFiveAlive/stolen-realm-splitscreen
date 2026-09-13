namespace SplitScreenLauncher;

/// <summary>
/// Cuts the screen into one rectangle per player.
///
/// The same arithmetic as tools\Start-SplitScreen.ps1, so a session laid out by the launcher and
/// one laid out by the script put the windows in the same places.
/// </summary>
internal static class TileMath
{
    internal static List<Rectangle> Compute(int count, TileLayout layout, Rectangle bounds)
    {
        count = Math.Max(1, count);
        var tiles = new List<Rectangle>(count);

        switch (layout)
        {
            case TileLayout.SideBySide:
            {
                int w = bounds.Width / count;
                for (int i = 0; i < count; i++)
                    tiles.Add(new Rectangle(bounds.X + i * w, bounds.Y, w, bounds.Height));
                break;
            }

            case TileLayout.Stacked:
            {
                int h = bounds.Height / count;
                for (int i = 0; i < count; i++)
                    tiles.Add(new Rectangle(bounds.X, bounds.Y + i * h, bounds.Width, h));
                break;
            }

            default:
            {
                int cols = count == 1 ? 1 : 2;
                int rows = (int)Math.Ceiling(count / (double)cols);
                int w = bounds.Width / cols;
                int h = bounds.Height / rows;

                for (int i = 0; i < count; i++)
                    tiles.Add(new Rectangle(bounds.X + (i % cols) * w, bounds.Y + (i / cols) * h, w, h));
                break;
            }
        }

        return tiles;
    }
}
