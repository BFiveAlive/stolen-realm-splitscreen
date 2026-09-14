namespace SplitScreenLauncher;

internal enum GameMode { Campaign, Roguelike }

internal enum TileLayout { Auto, SideBySide, Stacked, Grid }

/// <summary>How a seat gets its input.</summary>
internal enum SeatInput
{
    /// <summary>An Xbox-style pad that joined in the launcher, known by its XInput slot.</summary>
    Pad,

    /// <summary>Mouse and keyboard. There can only be one of these.</summary>
    KeyboardAndMouse,

    /// <summary>
    /// Any other controller. The launcher cannot tell those apart from the game's side, so the
    /// player presses a button on it once the game has loaded.
    /// </summary>
    Claim
}

/// <summary>One player: one window, one tile of a screen, one input device.</summary>
internal sealed class Seat
{
    /// <summary>Join order. Seat 0 hosts; it is renumbered when someone leaves.</summary>
    internal int Index { get; set; }

    internal SeatInput Input { get; set; } = SeatInput.Claim;

    /// <summary>For <see cref="SeatInput.Pad"/>: the Windows XInput slot, 0-3.</summary>
    internal int XInputSlot { get; set; } = -1;

    /// <summary>Ready in the lobby. Everyone ready starts the countdown.</summary>
    internal bool LobbyReady { get; set; }

    /// <summary>The monitor this player's window is on, by Windows device name.</summary>
    internal string Display { get; set; } = string.Empty;

    /// <summary>This player's position among the players sharing their display.</summary>
    internal int Tile { get; set; }

    /// <summary>For <see cref="SeatInput.Claim"/>: what the player claimed in game. Null until then.</summary>
    internal string? ControllerName { get; set; }

    /// <summary>For <see cref="SeatInput.Claim"/>: this window's game is loaded and listening.</summary>
    internal bool Ready { get; set; }

    internal string Label => "p" + (Index + 1);

    internal bool NeedsClaim => Input == SeatInput.Claim && ControllerName is null;

    internal string InputDescription => Input switch
    {
        SeatInput.Pad => $"Controller {XInputSlot + 1}",
        SeatInput.KeyboardAndMouse => "Keyboard & mouse",
        _ => ControllerName ?? "Other controller"
    };
}

internal sealed class SessionOptions
{
    /// <summary>Stolen Realm's party holds six characters, one per window here.</summary>
    internal const int MaxPlayers = 6;

    internal string GameDir { get; set; } = GameLocator.Find() ?? string.Empty;
    internal GameMode Mode { get; set; } = GameMode.Campaign;
    internal TileLayout Layout { get; set; } = TileLayout.Auto;
    internal List<Seat> Seats { get; } = [];

    /// <summary>
    /// Adds a player, beside the last one who joined. Null if the party is full or, for keyboard
    /// and mouse, if somebody already has it.
    /// </summary>
    internal Seat? AddSeat(SeatInput input, int xinputSlot = -1)
    {
        if (Seats.Count >= MaxPlayers)
            return null;

        if (input == SeatInput.KeyboardAndMouse && Seats.Any(s => s.Input == SeatInput.KeyboardAndMouse))
            return null;

        if (input == SeatInput.Pad && Seats.Any(s => s.Input == SeatInput.Pad && s.XInputSlot == xinputSlot))
            return null;

        var seat = new Seat
        {
            Index = Seats.Count,
            Input = input,
            XInputSlot = input == SeatInput.Pad ? xinputSlot : -1,
            Display = Seats.LastOrDefault()?.Display ?? string.Empty,
            Tile = int.MaxValue,

            // Nobody is holding an "other controller" in the launcher to press ready with, so that
            // seat is ready as soon as it is added.
            LobbyReady = input == SeatInput.Claim
        };

        Seats.Add(seat);
        SeatLayout.Normalize(this);
        return seat;
    }

    internal void RemoveSeat(Seat seat)
    {
        if (!Seats.Remove(seat))
            return;

        for (int i = 0; i < Seats.Count; i++)
            Seats[i].Index = i;

        SeatLayout.Normalize(this);
    }

    internal void ClearSeats() => Seats.Clear();

    internal bool AllReady => Seats.Count > 0 && Seats.All(s => s.LobbyReady);

    internal string ExePath => Path.Combine(GameDir, "Stolen Realm.exe");

    internal string PluginPath =>
        Path.Combine(GameDir, "BepInEx", "plugins", "SplitCoopMod", "SplitCoopMod.dll");

    /// <summary>Why this configuration cannot be launched, or null if it can.</summary>
    internal string? Problem()
    {
        if (string.IsNullOrWhiteSpace(GameDir) || !File.Exists(ExePath))
            return "Stolen Realm.exe was not found. Point the launcher at the game folder.";

        // A missing BepInEx is not a problem any more: Launch installs it (BepInExInstaller).

        // A launcher carrying the mod installs it on Launch, so a missing plugin is only a problem
        // for a build that was made without one.
        if (!File.Exists(PluginPath) && !ModInstaller.HasBundledMod)
            return "SplitCoopMod is not installed in the game's BepInEx\\plugins folder. "
                 + "Without it the windows open but never join each other.";

        if (Seats.Count == 0)
            return "Nobody has joined yet. Press any button on a controller, or add keyboard & mouse.";

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

    /// <summary>Steam's own list of library folders, for games kept on a second drive.</summary>
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
