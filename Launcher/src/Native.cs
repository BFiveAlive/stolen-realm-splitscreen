using System.Runtime.InteropServices;

namespace SplitScreenLauncher;

/// <summary>
/// The window placement calls.
///
/// The game is told its window size on the command line but not where to put it, so each instance
/// comes up wherever Windows felt like and has to be moved into its tile afterwards.
/// </summary>
internal static partial class Native
{
    internal const uint SwpShowWindow = 0x0040;
    internal const int SwShowNormal = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out Rect rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    /// <summary>
    /// A dark title bar to match the window. Windows 10 before 20H1 does not know the attribute
    /// and simply ignores it, which is the right outcome.
    /// </summary>
    internal static void UseDarkTitleBar(nint hWnd)
    {
        const int immersiveDarkMode = 20;
        int on = 1;
        DwmSetWindowAttribute(hWnd, immersiveDarkMode, ref on, sizeof(int));
    }
}
