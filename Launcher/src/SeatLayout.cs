namespace SplitScreenLauncher;

/// <summary>One monitor, as the launcher shows it.</summary>
internal sealed record Display(int Number, string DeviceName, Rectangle Bounds, bool Primary)
{
    internal string Label => $"Display {Number}  ·  {Bounds.Width}×{Bounds.Height}" + (Primary ? "  ·  main" : string.Empty);
}

/// <summary>
/// Where every player's window goes, across however many monitors there are.
///
/// Each seat names a display and a position on it. A display is shared out among the seats placed
/// on it, so two players on one monitor split it while a third has another monitor to themselves,
/// and moving one seat never needs anyone else rearranged by hand.
/// </summary>
internal static class SeatLayout
{
    /// <summary>
    /// Every monitor, numbered left to right.
    ///
    /// Windows' own display numbers follow the order the adapters were detected, which rarely
    /// matches where the monitors sit on the desk; numbering by position matches the picture.
    /// </summary>
    internal static List<Display> Displays()
    {
        return Screen.AllScreens
            .OrderBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .Select((s, i) => new Display(i + 1, s.DeviceName, s.Bounds, s.Primary))
            .ToList();
    }

    internal static Display DisplayOf(Seat seat, List<Display> displays)
    {
        // A monitor that has been unplugged since the last session falls back to the main one.
        return displays.FirstOrDefault(d => d.DeviceName == seat.Display)
               ?? displays.FirstOrDefault(d => d.Primary)
               ?? displays[0];
    }

    /// <summary>The tile rectangle, in screen pixels, for every seat.</summary>
    internal static Dictionary<Seat, Rectangle> Compute(SessionOptions options, List<Display>? displays = null)
    {
        displays ??= Displays();
        var result = new Dictionary<Seat, Rectangle>();

        foreach (var group in options.Seats.GroupBy(s => DisplayOf(s, displays)))
        {
            var ordered = group.OrderBy(s => s.Tile).ThenBy(s => s.Index).ToList();
            var tiles = TileMath.Compute(ordered.Count, LayoutFor(options.Layout, ordered.Count, group.Key.Bounds),
                group.Key.Bounds);

            for (int i = 0; i < ordered.Count; i++)
                result[ordered[i]] = tiles[i];
        }

        return result;
    }

    /// <summary>
    /// Automatic picks per display. Two players side by side on a landscape monitor but stacked on
    /// a portrait one, since halving the short side of a screen leaves two slivers.
    /// </summary>
    internal static TileLayout LayoutFor(TileLayout chosen, int count, Rectangle bounds)
    {
        if (chosen != TileLayout.Auto)
            return chosen;

        if (count == 2)
            return bounds.Width >= bounds.Height ? TileLayout.SideBySide : TileLayout.Stacked;

        return TileLayout.Grid;
    }

    /// <summary>
    /// Points every seat at a display that exists and numbers each display's seats 0, 1, 2…
    /// without gaps, keeping their order.
    /// </summary>
    internal static void Normalize(SessionOptions options)
    {
        var displays = Displays();

        foreach (var seat in options.Seats)
            seat.Display = DisplayOf(seat, displays).DeviceName;

        foreach (var group in options.Seats.GroupBy(s => s.Display))
        {
            int slot = 0;
            foreach (var seat in group.OrderBy(s => s.Tile).ThenBy(s => s.Index).ToList())
                seat.Tile = slot++;
        }
    }

    /// <summary>Two players trade places, wherever each of them was.</summary>
    internal static void Swap(Seat a, Seat b)
    {
        (a.Display, b.Display) = (b.Display, a.Display);
        (a.Tile, b.Tile) = (b.Tile, a.Tile);
    }

    /// <summary>Puts a seat on a display at a position among the seats already there.</summary>
    internal static void MoveTo(SessionOptions options, Seat seat, string display, int slot)
    {
        var others = options.Seats
            .Where(s => s != seat && s.Display == display)
            .OrderBy(s => s.Tile)
            .ToList();

        others.Insert(Math.Clamp(slot, 0, others.Count), seat);
        seat.Display = display;

        for (int i = 0; i < others.Count; i++)
            others[i].Tile = i;

        // Closes the gap on the display the seat came from.
        Normalize(options);
    }

    /// <summary>Puts a seat next to another one, sharing that player's display.</summary>
    internal static void MoveBeside(SessionOptions options, Seat seat, Seat target, bool after)
    {
        var others = options.Seats
            .Where(s => s != seat && s.Display == target.Display)
            .OrderBy(s => s.Tile)
            .ToList();

        int index = others.IndexOf(target);
        MoveTo(options, seat, target.Display, after ? index + 1 : index);
    }

    /// <summary>One player per display, for as far as the displays go; the rest share from the first.</summary>
    internal static void OneDisplayEach(SessionOptions options)
    {
        var displays = Displays();

        foreach (var seat in options.Seats)
        {
            seat.Display = displays[seat.Index % displays.Count].DeviceName;
            seat.Tile = seat.Index;
        }

        Normalize(options);
    }

    /// <summary>Everyone on one display, in player order.</summary>
    internal static void AllOn(SessionOptions options, string display)
    {
        foreach (var seat in options.Seats)
        {
            seat.Display = display;
            seat.Tile = seat.Index;
        }

        Normalize(options);
    }
}
