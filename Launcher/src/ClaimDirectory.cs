using System.Globalization;
using System.Text;

namespace SplitScreenLauncher;

/// <summary>
/// The launcher's half of the in-game controller-claiming exchange, used for controllers the
/// launcher cannot match itself. See SeatClaim.cs in the mod for the other half:
///
///   turn.txt              written here    the seat that should listen next, or -1 for nobody
///   ready-N.txt           written by N    seat N's game has loaded and can hear its controllers
///   seat-N.txt            written by N    seat N took a controller: index=, name=, hardware=
///   xinput-reserved.txt   written here    XInput slots already given to windows, one per line
/// </summary>
internal sealed class ClaimDirectory
{
    internal ClaimDirectory(string root) => Root = root;

    internal string Root { get; }

    internal void Reset()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);

        Directory.CreateDirectory(Root);
        Disarm();
    }

    internal void Arm(int seat) => Write("turn.txt", seat.ToString(CultureInfo.InvariantCulture));

    internal void Disarm() => Write("turn.txt", "-1");

    /// <summary>
    /// Pads handed out by slot, so a window claiming in game does not also take one of them when
    /// its owner presses a button in their own window.
    /// </summary>
    internal void ReserveXInputSlots(IEnumerable<int> slots) =>
        Write("xinput-reserved.txt", string.Join(Environment.NewLine,
            slots.Select(s => s.ToString(CultureInfo.InvariantCulture))));

    internal bool IsReady(int seat) => File.Exists(Path.Combine(Root, $"ready-{seat}.txt"));

    /// <summary>The name of the controller seat N claimed, or null if it has not claimed one.</summary>
    internal string? ClaimedController(int seat)
    {
        string file = Path.Combine(Root, $"seat-{seat}.txt");
        if (!File.Exists(file))
            return null;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? name = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("name=", StringComparison.Ordinal))
                    name = line["name=".Length..].Trim();
            }

            return string.IsNullOrEmpty(name) ? "Controller" : name;
        }
        catch (IOException)
        {
            return null;   // Mid-write; the next poll will read it.
        }
    }

    private void Write(string name, string body)
    {
        Directory.CreateDirectory(Root);

        string final = Path.Combine(Root, name);
        string temp = final + ".tmp";

        // A game window may have the file open for reading at the instant this replaces it. It
        // holds it for well under a millisecond, so a short retry is all it takes.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, body, Encoding.UTF8);
                File.Move(temp, final, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(20);
            }
        }
    }
}
