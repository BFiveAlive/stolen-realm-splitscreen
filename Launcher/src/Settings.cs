using System.Globalization;

namespace SplitScreenLauncher;

/// <summary>
/// Remembers the last setup, so the second evening of play is one click rather than five.
///
/// Controller claims are deliberately not remembered: which pad Rewired calls which can change
/// between sessions, and a remembered claim that silently points at the wrong pad is worse than
/// asking everyone to press a button again. Which monitor each player sits at is remembered, and
/// falls back to the main monitor if that one is not connected.
/// </summary>
internal static class Settings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StolenRealmSplitScreen", "launcher.txt");

    internal static SessionOptions Load()
    {
        var options = new SessionOptions();

        try
        {
            if (!File.Exists(FilePath))
                return options;

            var map = File.ReadAllLines(FilePath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

            if (map.TryGetValue("gamedir", out string? dir) && File.Exists(Path.Combine(dir, "Stolen Realm.exe")))
                options.GameDir = dir;

            if (map.TryGetValue("mode", out string? mode) && Enum.TryParse(mode, true, out GameMode m))
                options.Mode = m;

            if (map.TryGetValue("layout", out string? layout) && Enum.TryParse(layout, true, out TileLayout l))
                options.Layout = l;

            if (map.TryGetValue("players", out string? players)
                && int.TryParse(players, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                options.SetPlayerCount(Math.Clamp(count, 1, 4));

            foreach (var seat in options.Seats)
            {
                // seat1=KeyboardAndMouse|\\.\DISPLAY2|0
                if (!map.TryGetValue("seat" + (seat.Index + 1), out string? value))
                    continue;

                string[] parts = value.Split('|');

                if (parts.Length > 0 && Enum.TryParse(parts[0], true, out SeatInput input))
                    seat.Input = input;

                if (parts.Length > 1)
                    seat.Display = parts[1];

                if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tile))
                    seat.Tile = tile;
            }

            // Only one seat may keep the keyboard, whatever the file says.
            bool keyboardTaken = false;
            foreach (var seat in options.Seats.Where(s => s.Input == SeatInput.KeyboardAndMouse))
            {
                if (keyboardTaken)
                    seat.Input = SeatInput.Controller;
                keyboardTaken = true;
            }

            SeatLayout.Normalize(options);
        }
        catch
        {
            // A damaged settings file costs the defaults, never the launcher.
        }

        return options;
    }

    internal static void Save(SessionOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var lines = new List<string>
            {
                "gamedir=" + options.GameDir,
                "mode=" + options.Mode,
                "layout=" + options.Layout,
                "players=" + options.Seats.Count.ToString(CultureInfo.InvariantCulture)
            };

            foreach (var seat in options.Seats)
            {
                lines.Add("seat" + (seat.Index + 1) + "=" + seat.Input + "|" + seat.Display + "|"
                          + seat.Tile.ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllLines(FilePath, lines);
        }
        catch
        {
            // Not being able to remember settings is not worth interrupting anyone over.
        }
    }
}
