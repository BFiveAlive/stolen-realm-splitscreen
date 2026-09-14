using System.Globalization;

namespace SplitScreenLauncher;

/// <summary>
/// Remembers the game folder, mode and layout between runs.
///
/// Seats are not remembered. Players join by pressing a button each time, and the XInput slot a
/// pad gets can change between sessions, so a remembered seat could point at the wrong pad.
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

            File.WriteAllLines(FilePath,
            [
                "gamedir=" + options.GameDir,
                "mode=" + options.Mode.ToString(),
                "layout=" + options.Layout.ToString()
            ]);
        }
        catch
        {
            // Not being able to remember settings is not worth interrupting anyone over.
        }
    }
}
