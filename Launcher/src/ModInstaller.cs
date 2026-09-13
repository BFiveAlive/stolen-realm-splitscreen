using System.Reflection;
using System.Security.Cryptography;

namespace SplitScreenLauncher;

/// <summary>
/// Keeps the game's copy of SplitCoopMod the one this launcher was built with.
///
/// The launcher and the mod talk to each other - the controller claiming is half in each - so a
/// launcher paired with an older mod would start the games and then wait forever for a claim the
/// mod does not know how to make. Shipping the mod inside the launcher and copying it over when it
/// differs means there is only ever one thing to download, and no way to have mismatched halves.
/// </summary>
internal static class ModInstaller
{
    private const string ResourceName = "SplitCoopMod.dll";

    internal static bool HasBundledMod =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    internal static bool BepInExInstalled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"));

    /// <summary>Copies the bundled mod in if it is missing or different. Returns what it did, or null.</summary>
    internal static string? EnsureInstalled(SessionOptions options)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        byte[] bundled;
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bundled = buffer.ToArray();
        }

        string target = options.PluginPath;
        bool existed = File.Exists(target);

        if (existed && SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bundled)))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bundled);

        return existed
            ? "Updated SplitCoopMod in the game folder to the version this launcher needs."
            : "Installed SplitCoopMod into the game folder.";
    }
}
