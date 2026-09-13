using System.Globalization;

namespace SplitScreenLauncher;

/// <summary>
/// Remembers the last setup, so the second evening of play is one click rather than five.
///
/// Controller claims are deliberately not remembered: which pad Rewired calls which can change
/// between sessions, and a remembered claim that silently points at the wrong pad is worse than
/// asking everyone to press a button again.
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

            if (map.TryGetValue("keyboardseat", out string? keyboard)
                && int.TryParse(keyboard, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seat))
            {
                foreach (var s in options.Seats)
                    s.Input = s.Index == seat ? SeatInput.KeyboardAndMouse : SeatInput.Controller;
            }
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

            var keyboard = options.Seats.FirstOrDefault(s => s.Input == SeatInput.KeyboardAndMouse);

            File.WriteAllLines(FilePath,
            [
                "gamedir=" + options.GameDir,
                "mode=" + options.Mode,
                "layout=" + options.Layout,
                "players=" + options.Seats.Count.ToString(CultureInfo.InvariantCulture),
                "keyboardseat=" + (keyboard?.Index ?? -1).ToString(CultureInfo.InvariantCulture)
            ]);
        }
        catch
        {
            // Not being able to remember settings is not worth interrupting anyone over.
        }
    }
}
