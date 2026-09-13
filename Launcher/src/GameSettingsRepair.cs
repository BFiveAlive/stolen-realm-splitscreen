using Microsoft.Win32;

namespace SplitScreenLauncher;

/// <summary>
/// Undoes the one piece of damage an older version of the mod could leave behind.
///
/// Before the mod stopped it, a split-screen window saved its controller setup - player 1 owning
/// a single gamepad and no keyboard or mouse - into the game's registry settings when it closed.
/// Every later launch loaded that, modded or not, and the game failed to start properly: thousands
/// of errors a minute, and in a session, stats and menus that made no sense.
///
/// The fix in the mod stops new damage. This clears existing damage, so nobody who played with
/// the earlier version has to find a registry value by hand. Rewired rebuilds the value with
/// normal defaults on the next start.
/// </summary>
internal static class GameSettingsRepair
{
    private const string KeyPath = @"Software\Burst2Flame Entertainment\Stolen Realm";
    private const string ValuePrefix = "RewiredSaveData_ControllerAssignments";

    /// <summary>Returns what it did, or null if nothing needed doing.</summary>
    internal static string? RepairControllerAssignments()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null)
            return null;

        foreach (string name in key.GetValueNames().Where(n => n.StartsWith(ValuePrefix, StringComparison.Ordinal)))
        {
            string text = key.GetValue(name) switch
            {
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                string s => s,
                _ => string.Empty
            };

            // The damaged form: player 0 - the one every window plays as - without a keyboard.
            // The game itself never saves that; a freshly rebuilt value gives player 0 both.
            string compact = text.Replace(" ", string.Empty);
            if (!compact.Contains("{\"id\":0,\"hasKeyboard\":false", StringComparison.Ordinal))
                continue;

            key.DeleteValue(name, throwOnMissingValue: false);
            return "Reset the game's saved controller assignments, which an earlier split-screen session had "
                 + "left without a keyboard or mouse. The game rebuilds them with defaults.";
        }

        return null;
    }
}
