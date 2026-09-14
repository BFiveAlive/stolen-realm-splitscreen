using System.IO.Compression;
using System.Security.Cryptography;

namespace SplitScreenLauncher;

/// <summary>
/// Installs BepInEx into the game folder when it is not there yet.
///
/// SplitCoopMod is a BepInEx plugin, so without BepInEx the games start but never host, join or
/// take their controllers. Rather than send a player off to install it by hand, the launcher fetches
/// the same release the Stolen Realm mod installer uses, pinned to one version and one checksum.
///
/// It is downloaded when needed, never shipped inside the launcher, so this repository still
/// distributes no third-party binaries. BepInEx works through a proxy DLL beside the game's
/// executable; nothing in the game's own files is changed.
/// </summary>
internal static class BepInExInstaller
{
    internal const string Version = "5.4.23.5";

    internal const string ReleasePage = "https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5";

    private const string DownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";

    // The same pin as the Stolen Realm mod installer's manifest, so both tools install identical files.
    private const string Sha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";

    /// <summary>Downloads, verifies and unpacks BepInEx. Returns what it did.</summary>
    internal static async Task<string> InstallAsync(string gameDir, IProgress<string>? progress, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // GitHub rejects requests with no user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StolenRealmSplitScreen/" + typeof(BepInExInstaller).Assembly.GetName().Version);

        progress?.Report($"Downloading BepInEx {Version} (needed once, for the split-screen mod)…");
        byte[] zip = await http.GetByteArrayAsync(DownloadUrl, ct);

        // A corrupt, truncated or substituted download is refused before anything is written.
        string actual = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The BepInEx download did not match its checksum (expected {Sha256}, got {actual}). Nothing was installed.");
        }

        progress?.Report($"Installing BepInEx {Version}…");
        int files = Extract(zip, gameDir);

        if (!ModInstaller.BepInExInstalled(gameDir))
            throw new InvalidDataException("BepInEx was unpacked, but BepInEx\\core\\BepInEx.dll is still missing.");

        return $"Installed BepInEx {Version} into the game folder ({files} files).";
    }

    /// <summary>
    /// Unpacks the archive over the game folder. Every entry is checked before anything is written,
    /// so an entry pointing outside the folder cannot leave a half-extracted install behind.
    /// </summary>
    internal static int Extract(byte[] zipBytes, string gameDir)
    {
        string root = Path.GetFullPath(gameDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var planned = new List<(ZipArchiveEntry Entry, string Destination)>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;   // a directory entry

            string destination = Path.GetFullPath(
                Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The BepInEx archive has an entry outside the game folder: {entry.FullName}");

            planned.Add((entry, destination));
        }

        foreach (var (entry, destination) in planned)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        return planned.Count;
    }
}
