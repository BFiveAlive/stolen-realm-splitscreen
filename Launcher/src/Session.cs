using System.Diagnostics;
using System.Globalization;

namespace SplitScreenLauncher;

/// <summary>
/// Starts one game per player, joins them together and puts each window in its tile.
///
/// Rather than waiting a fixed time for the host, this watches the host's own trace for the moment
/// its session is actually open, and starts everyone else then.
/// </summary>
internal sealed class Session
{
    internal const string ProcessName = "Stolen Realm";

    internal const string Hosting = "Hosting";
    internal const string Connected = "Connected";
    internal const string Connecting = "Connecting…";
    internal const string Failed = "Could not connect";

    private const int HostTimeoutSeconds = 120;
    private const int JoinSpacingSeconds = 6;
    private const int SettleSeconds = 20;

    private readonly SessionOptions options;
    private readonly Action<string> log;
    private readonly List<(Seat Seat, Process Process)> started = [];

    internal Session(SessionOptions options, Action<string> log)
    {
        this.options = options;
        this.log = log;
        Claims = new ClaimDirectory(Path.Combine(WorkDir, "claims"));
    }

    internal ClaimDirectory Claims { get; }

    internal static string WorkDir => Path.Combine(Path.GetTempPath(), "stolen-realm-splitscreen");

    private string TraceDir => Path.Combine(options.GameDir, "BepInEx", "splitcoop-logs");

    internal bool AnyRunning => started.Any(s => !HasExited(s.Process));

    internal int StartedCount => started.Count;

    internal static bool GameIsRunning => Process.GetProcessesByName(ProcessName).Length > 0;

    internal static void StopGame()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            try { process.Kill(); }
            catch { /* Already on its way out. */ }
            finally { process.Dispose(); }
        }
    }

    internal async Task LaunchAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (GameIsRunning)
        {
            progress.Report("Closing the copy of Stolen Realm that is already running…");
            StopGame();
            await Task.Delay(3000, ct);
        }

        Reset(WorkDir);
        Reset(TraceDir);
        Claims.Reset();
        Claims.ReserveXInputSlots(options.Seats.Where(s => s.Input == SeatInput.Pad).Select(s => s.XInputSlot));

        SeatLayout.Normalize(options);
        var displays = SeatLayout.Displays();
        log($"{displays.Count} display(s): "
            + string.Join("; ", displays.Select(d => $"{d.Number} {d.DeviceName} {d.Bounds}"))
            + $". {options.Seats.Count} player(s), {options.Mode}.");

        foreach (var seat in options.Seats.OrderBy(s => s.Index))
        {
            ct.ThrowIfCancellationRequested();

            progress.Report($"Starting player {seat.Index + 1}'s game…");
            Start(seat);

            bool last = seat.Index == options.Seats.Count - 1;

            if (seat.Index == 0 && !last)
                await WaitForHostAsync(progress, ct);
            else if (!last)
                await WaitAsync(JoinSpacingSeconds, $"Started player {seat.Index + 1}; starting the next…", progress, ct);
        }

        // Unity settles its window a few seconds after it appears and can put it back where it
        // started, so the placement is kept up for a little while rather than done once.
        await WaitAsync(SettleSeconds, "Arranging the windows…", progress, ct);
        progress.Report("All games started.");
    }

    /// <summary>
    /// Moves each window to its seat's tile, if it is not there already. The layout is worked out
    /// afresh each time, so a rearrangement in the launcher moves the game windows too.
    /// </summary>
    internal void PlaceWindows()
    {
        var layout = SeatLayout.Compute(options);

        foreach (var (seat, process) in started)
        {
            if (HasExited(process) || !layout.TryGetValue(seat, out Rectangle tile))
                continue;

            nint handle;
            try
            {
                process.Refresh();
                handle = process.MainWindowHandle;
            }
            catch
            {
                continue;
            }

            if (handle == 0)
                continue;

            if (Native.GetWindowRect(handle, out var r)
                && r.Left == tile.X && r.Top == tile.Y
                && r.Right - r.Left == tile.Width && r.Bottom - r.Top == tile.Height)
                continue;

            Native.ShowWindow(handle, Native.SwShowNormal);
            Native.SetWindowPos(handle, 0, tile.X, tile.Y, tile.Width, tile.Height, Native.SwpShowWindow);
        }
    }

    /// <summary>What each player's mod trace says about its connection, keyed by "p1", "p2"…</summary>
    internal Dictionary<string, string> Verdicts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(TraceDir))
            return result;

        foreach (string file in Directory.GetFiles(TraceDir, "instance-p*-*.log"))
        {
            // instance-p2-12345
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length < 3)
                continue;

            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }

            string label = parts[1];
            result[label] =
                text.Contains("SESSION OK", StringComparison.Ordinal) ? (label == "p1" ? Hosting : Connected)
                : text.Contains("FAILED", StringComparison.Ordinal) ? Failed
                : Connecting;
        }

        return result;
    }

    private void Start(Seat seat)
    {
        Rectangle tile = SeatLayout.Compute(options)[seat];
        var psi = new ProcessStartInfo(options.ExePath)
        {
            WorkingDirectory = options.GameDir,
            UseShellExecute = false
        };

        void Add(params string[] args)
        {
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
        }

        string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        // No -monitor: Unity's monitor numbering is its own, and the window is moved to exactly
        // the right place by SetWindowPos as soon as it appears, on whichever display that is.
        Add("-screen-fullscreen", "0",
            "-screen-width", Int(tile.Width),
            "-screen-height", Int(tile.Height),
            "-popupwindow",                    // borderless, so the tiles meet without chrome
            "-logFile", Path.Combine(WorkDir, seat.Label + ".log"),
            "-srplayer", seat.Label,
            "-srmode", options.Mode == GameMode.Roguelike ? "roguelike" : "campaign",
            "-srseat", Int(seat.Index));

        switch (seat.Input)
        {
            case SeatInput.KeyboardAndMouse:
                Add("-srcontroller", "keyboard");
                break;

            case SeatInput.Pad:
                // The pad chosen in the lobby, by the XInput slot the game's input library also uses.
                Add("-srcontroller", "xinput:" + Int(seat.XInputSlot));
                break;

            default:
                Add("-srcontroller", "claim", "-srclaimdir", Claims.Root);
                break;
        }

        if (seat.Index == 0)
            Add("-srhost");
        else
            Add("-srjoin", "127.0.0.1");

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Windows would not start Stolen Realm.exe.");

        started.Add((seat, process));
        log($"Started player {seat.Index + 1} ({(seat.Index == 0 ? "host" : "joining")}, "
            + $"{seat.InputDescription}, {tile.Width}x{tile.Height} at {tile.X},{tile.Y}), pid {process.Id}");
    }

    private async Task WaitForHostAsync(IProgress<string> progress, CancellationToken ct)
    {
        Process host = started[^1].Process;
        var began = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (HasExited(host))
                throw new InvalidOperationException(
                    "Player 1's game closed while it was starting. Its log is in " + WorkDir);

            if (Verdicts().TryGetValue("p1", out string? verdict) && verdict == Hosting)
            {
                log($"Player 1 is hosting after {(DateTime.UtcNow - began).TotalSeconds:0}s.");
                return;
            }

            int elapsed = (int)(DateTime.UtcNow - began).TotalSeconds;
            if (elapsed >= HostTimeoutSeconds)
            {
                // The joiners retry on their own, so carrying on is better than giving up here.
                log($"Player 1 had not reported a session after {HostTimeoutSeconds}s; starting the others anyway.");
                return;
            }

            PlaceWindows();
            progress.Report($"Starting player 1's game and opening the session… {elapsed}s");
            await Task.Delay(1000, ct);
        }
    }

    private async Task WaitAsync(int seconds, string message, IProgress<string> progress, CancellationToken ct)
    {
        for (int i = 0; i < seconds; i++)
        {
            PlaceWindows();
            progress.Report(message);
            await Task.Delay(1000, ct);
        }
    }

    private void Reset(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            log($"Could not clear {dir}: {e.Message}");
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }
}
