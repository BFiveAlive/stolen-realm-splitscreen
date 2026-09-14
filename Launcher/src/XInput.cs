using System.Runtime.InteropServices;

namespace SplitScreenLauncher;

internal enum PadButton { Up, Down, Left, Right, A, B, X, Y, LeftShoulder, RightShoulder, Start, Back }

/// <summary>
/// Reads Xbox-style controllers directly, before any game is running.
///
/// This works because the game's input library, Rewired, builds its XInput devices from the same
/// four Windows slots and reports each slot as the joystick's systemId. A pad that presses A here
/// in slot 2 is therefore exactly the pad the game will know by systemId 2, so the launcher can
/// hand each window its controller up front and nobody has to press anything in game.
///
/// XInput has four slots, full stop - a Windows limit, not this launcher's. Players beyond four
/// use keyboard and mouse or a non-XInput controller claimed in game.
/// </summary>
internal sealed partial class PadPoller
{
    internal const int SlotCount = 4;

    private const uint ErrorSuccess = 0;

    // The left stick doubles as a d-pad. Two thresholds, so a stick resting near the edge of the
    // first does not register a stream of presses.
    private const short StickOn = 20000;
    private const short StickOff = 10000;

    private static readonly (ushort Mask, PadButton Button)[] Map =
    [
        (0x0001, PadButton.Up), (0x0002, PadButton.Down), (0x0004, PadButton.Left), (0x0008, PadButton.Right),
        (0x0010, PadButton.Start), (0x0020, PadButton.Back),
        (0x0100, PadButton.LeftShoulder), (0x0200, PadButton.RightShoulder),
        (0x1000, PadButton.A), (0x2000, PadButton.B), (0x4000, PadButton.X), (0x8000, PadButton.Y)
    ];

    private readonly ushort[] last = new ushort[SlotCount];
    private readonly ushort[] stick = new ushort[SlotCount];
    private readonly bool[] connected = new bool[SlotCount];

    internal event Action<int, PadButton>? ButtonPressed;

    internal event Action<int>? Disconnected;

    internal bool IsConnected(int slot) => slot is >= 0 and < SlotCount && connected[slot];

    /// <summary>Call often - every frame or so. Raises an event per newly pressed button.</summary>
    internal void Poll()
    {
        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (XInputGetState((uint)slot, out State state) != ErrorSuccess)
            {
                if (connected[slot])
                {
                    connected[slot] = false;
                    last[slot] = 0;
                    stick[slot] = 0;
                    Disconnected?.Invoke(slot);
                }
                continue;
            }

            bool wasConnected = connected[slot];
            connected[slot] = true;

            ushort buttons = (ushort)(state.Gamepad.Buttons | StickAsDpad(slot, state.Gamepad.ThumbLX, state.Gamepad.ThumbLY));

            // A pad that has just appeared reports whatever is already held; that is not a press.
            if (!wasConnected)
            {
                last[slot] = buttons;
                continue;
            }

            ushort pressed = (ushort)(buttons & ~last[slot]);
            last[slot] = buttons;

            if (pressed == 0)
                continue;

            foreach (var (mask, button) in Map)
            {
                if ((pressed & mask) != 0)
                    ButtonPressed?.Invoke(slot, button);
            }
        }
    }

    private ushort StickAsDpad(int slot, short x, short y)
    {
        ushort held = stick[slot];

        held = Axis(held, 0x0004, 0x0008, x);   // left / right
        held = Axis(held, 0x0002, 0x0001, y);   // down / up (XInput's Y is positive upward)

        stick[slot] = held;
        return held;
    }

    private static ushort Axis(ushort held, ushort negative, ushort positive, short value)
    {
        if (value <= -StickOn) held = (ushort)((held | negative) & ~positive);
        else if (value >= StickOn) held = (ushort)((held | positive) & ~negative);
        else if (value > -StickOff && value < StickOff) held = (ushort)(held & ~(negative | positive));

        return held;
    }

    /// <summary>A short buzz, so a player knows which pad just joined or readied.</summary>
    internal static void Rumble(int slot, int milliseconds, ushort strength = 42000)
    {
        if (slot is < 0 or >= SlotCount)
            return;

        var on = new Vibration { LeftMotor = strength, RightMotor = strength };
        XInputSetState((uint)slot, ref on);

        _ = Task.Delay(milliseconds).ContinueWith(_ =>
        {
            var off = new Vibration();
            XInputSetState((uint)slot, ref off);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration
    {
        public ushort LeftMotor;
        public ushort RightMotor;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint userIndex, out State state);

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputSetState(uint userIndex, ref Vibration vibration);
}
