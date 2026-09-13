namespace SplitScreenLauncher;

internal enum GameMode { Campaign, Roguelike }

internal enum TileLayout { Auto, SideBySide, Stacked, Grid }

/// <summary>How a seat gets its input.</summary>
internal enum SeatInput
{
    /// <summary>The player will press a button on the pad they want for this window.</summary>
    Controller,

    /// <summary>Mouse and keyboard. There can only be one of these, and it is nobody's to claim.</summary>
    KeyboardAndMouse
}

/// <summary>One player: one window, one tile of a screen, one input device.</summary>
internal sealed class Seat
{
    internal required int Index { get; init; }

    internal SeatInput Input { get; set; } = SeatInput.Controller;

    /// <summary>The monitor this player's window is on, by Windows device name.</summary>
    internal string Display { get; set; } = string.Empty;

    /// <summary>
    /// This player's position among the players sharing their display.
    ///
    /// Separate from <see cref="Index"/> because the index is also who hosts and which window a
    /// claim belongs to, and neither of those should change just because two people swapped seats.
    /// </summary>
    internal int Tile { get; set; }

    /// <summary>What the player claimed, once they have pressed a button. Null until then.</summary>
    internal string? ControllerName { get; set; }

    /// <summary>True once this window has told us it is loaded and listening.</summary>
    internal bool Ready { get; set; }

    internal string Label => "p" + (Index + 1);

    internal bool NeedsClaim => Input == SeatInput.Controller && ControllerName is null;
}

internal sealed class SessionOptions
{
    internal string GameDir { get; set; } = GameLocator.Find() ?? string.Empty;
    internal GameMode Mode { get; set; } = GameMode.Campaign;
    internal TileLayout Layout { get; set; } = TileLayout.Auto;
    internal List<Seat> Seats { get; } = [];

    internal SessionOptions() => SetPlayerCount(2);

    /// <summary>
    /// Grows or shrinks the seat list, keeping what the player already set up.
    ///
    /// Changing the player count is the first thing anyone touches and it should not throw away
    /// which monitor player 1 was put on.
    /// </summary>
    internal void SetPlayerCount(int count)
    {
        while (Seats.Count > count)
            Seats.RemoveAt(Seats.Count - 1);

        while (Seats.Count < count)
        {
            // Player 1 defaults to mouse and keyboard: it is the seat that drives menus most
            // comfortably, and it means this works with no controllers plugged in at all.
            // A new player joins whichever display the previous player is on, after them.
            var previous = Seats.LastOrDefault();

            Seats.Add(new Seat
            {
                Index = Seats.Count,
                Input = Seats.Count == 0 ? SeatInput.KeyboardAndMouse : SeatInput.Controller,
                Display = previous?.Display ?? string.Empty,
                Tile = int.MaxValue
            });
        }

        SeatLayout.Normalize(this);
    }

    internal string ExePath => Path.Combine(GameDir, "Stolen Realm.exe");

    internal string PluginPath =>
        Path.Combine(GameDir, "BepInEx", "plugins", "SplitCoopMod", "SplitCoopMod.dll");

    /// <summary>Why this configuration cannot be launched, or null if it can.</summary>
    internal string? Problem()
    {
        if (string.IsNullOrWhiteSpace(GameDir) || !File.Exists(ExePath))
            return "Stolen Realm.exe was not found. Point the launcher at the game folder.";

        if (!ModInstaller.BepInExInstalled(GameDir))
            return "BepInEx is not installed in the game folder. Install it first - the Stolen Realm "
                 + "mod installer does this - and then launch again.";

        // A launcher carrying the mod installs it on Launch, so a missing plugin is only a problem
        // for a build that was made without one.
        if (!File.Exists(PluginPath) && !ModInstaller.HasBundledMod)
            return "SplitCoopMod is not installed in the game's BepInEx\\plugins folder. "
                 + "Without it the windows open but never join each other.";

        if (Seats.Count(s => s.Input == SeatInput.KeyboardAndMouse) > 1)
            return "Only one seat can use the keyboard and mouse.";

        return null;
    }
}

/// <summary>Finds the game without asking, in the places Steam actually puts it.</summary>
internal static class GameLocator
{
    internal static string? Find()
    {
        foreach (string candidate in Candidates())
        {
            if (File.Exists(Path.Combine(candidate, "Stolen Realm.exe")))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        const string relative = @"steamapps\common\Stolen Realm";

        foreach (string library in SteamLibraries())
            yield return Path.Combine(library, relative);

        // Last resort, for a Steam install that never registered itself.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.RootDirectory.FullName, @"Program Files (x86)\Steam", relative);
            yield return Path.Combine(drive.RootDirectory.FullName, @"SteamLibrary", relative);
        }
    }

    /// <summary>
    /// Steam's own list of library folders.
    ///
    /// Plenty of people keep games on a second drive, and guessing C:\Program Files (x86) would
    /// send them to the folder picker for no reason.
    /// </summary>
    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = SteamPath();
        if (steam is null)
            yield break;

        yield return steam;

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        // "path"		"D:\\SteamLibrary"
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    private static string? SteamPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string is { } path && path.Length > 0
                ? path.Replace('/', '\\')
                : null;
        }
        catch
        {
            return null;
        }
    }
}
