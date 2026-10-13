using System.Security.Cryptography;
using System.Text;

namespace MultiSeat.Service.Steam;

/// <summary>
/// Builds and refreshes one seat's private copy of the Steam client folder.
///
/// Pure file work with no service state, so tests can run it against a fake Steam tree in a temp
/// folder. <see cref="SteamIsolation"/> decides WHEN to call it and where the folders are.
///
/// What is copied is what was verified live on 2026-10-13: a robocopy of the install folder that
/// left out the directories and files below, 1.36 GB, on which a second Steam signed in with its
/// own account. Everything excluded is either state that belongs to ONE Steam (config, logins,
/// logs, caches, the games) or is rebuilt by Steam itself.
///
/// Three rules this class never breaks:
///   - It never deletes anything. A refresh adds and overwrites client files and leaves the rest.
///   - It never touches an excluded name, so a refresh cannot reach the seat's own login,
///     config, userdata or games. Excluded names are not even enumerated in the source.
///   - It never follows a reparse point (junction or symlink), in the source or the destination.
///     A junction inside the Steam folder could point at the whole drive.
/// </summary>
internal static class SteamClientCopier
{
    /// <summary>
    /// Directory names left out, at ANY depth and compared without regard to case. Any depth
    /// because robocopy /XD, the command that was actually verified, matches names at every level.
    /// </summary>
    internal static readonly string[] ExcludedDirectoryNames =
        ["steamapps", "logs", "config", "userdata", "dumps", "appcache", "depotcache", "htmlcache"];

    /// <summary>Exact file names left out, at any depth.</summary>
    internal static readonly string[] ExcludedFileNames = ["loginusers.vdf"];

    /// <summary>File-name prefixes left out: the ssfn sentry files that remember a Steam Guard login.</summary>
    internal static readonly string[] ExcludedFilePrefixes = ["ssfn"];

    /// <summary>Written into the seat folder once a copy finished with no failures.</summary>
    internal const string MarkerFileName = "multiseat-steam-copy.marker";

    // Bumped if the copy rules above change, so an old marker cannot vouch for a copy made by
    // different rules.
    private const string MarkerFormat = "v1";

    internal static bool IsExcludedDirectory(string name) =>
        ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static bool IsExcludedFile(string name) =>
        ExcludedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ExcludedFilePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// A cheap fingerprint of "which Steam client is installed": size and write time of the files
    /// that change when the client updates. steam.exe and the two client DLLs are hashed, and so is
    /// the listing of the package folder, where Steam keeps its update manifests and bundles.
    /// Reads metadata only, never file contents.
    /// </summary>
    internal static string ComputeMarker(string sourceDir)
    {
        var sb = new StringBuilder(MarkerFormat).Append('\n');

        foreach (var name in new[] { "steam.exe", "steamclient.dll", "steamclient64.dll" })
            AppendFile(sb, new FileInfo(Path.Combine(sourceDir, name)), name);

        var package = new DirectoryInfo(Path.Combine(sourceDir, "package"));
        if (package.Exists && !IsReparsePoint(package))
        {
            foreach (var f in package.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                AppendFile(sb, f, "package/" + f.Name);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void AppendFile(StringBuilder sb, FileInfo f, string label)
    {
        sb.Append(label.ToLowerInvariant()).Append('|');
        if (f.Exists)
            sb.Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks);
        else
            sb.Append("absent");
        sb.Append('\n');
    }

    internal static string? ReadMarker(string seatDir)
    {
        try
        {
            var path = Path.Combine(seatDir, MarkerFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// True when the seat folder holds a finished copy of THIS source: the same marker, and a
    /// steam.exe actually present (a marker left behind by a half-deleted folder proves nothing).
    /// </summary>
    internal static bool IsUpToDate(string sourceDir, string seatDir) =>
        File.Exists(Path.Combine(seatDir, "steam.exe"))
        && string.Equals(ReadMarker(seatDir), ComputeMarker(sourceDir), StringComparison.Ordinal);

    internal sealed record SyncResult(int FilesCopied, long BytesCopied, int FilesSkippedReparse, IReadOnlyList<string> Failures);

    /// <summary>
    /// Copy the client files from <paramref name="sourceDir"/> into <paramref name="seatDir"/>.
    /// A file is copied when it is missing in the seat folder or differs in size or write time, so
    /// a refresh after a Steam update moves only what changed. Failures (a file held open by a
    /// running seat Steam, say) are collected, not thrown, and the walk carries on.
    /// Does NOT write the marker; the caller does that only when there were no failures.
    /// </summary>
    internal static SyncResult Sync(string sourceDir, string seatDir, CancellationToken ct)
    {
        var failures = new List<string>();
        var destinationRoot = Path.GetFullPath(seatDir).TrimEnd(Path.DirectorySeparatorChar);
        int copied = 0, reparse = 0;
        long bytes = 0;

        void Walk(DirectoryInfo src, string dst)
        {
            ct.ThrowIfCancellationRequested();

            // A link planted in the seat folder (the seat owns this tree and can create one) would
            // carry every file below it out to wherever it points. Skip the whole subtree.
            var dstInfo = new DirectoryInfo(dst);
            if (dstInfo.Exists && IsReparsePoint(dstInfo))
            {
                failures.Add($"{dst}: destination folder is a reparse point");
                return;
            }
            Directory.CreateDirectory(dst);

            IEnumerable<FileSystemInfo> entries;
            try { entries = src.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{src.FullName}: {ex.Message}");
                return;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (IsReparsePoint(entry)) { reparse++; continue; }

                if (entry is DirectoryInfo dir)
                {
                    if (IsExcludedDirectory(dir.Name)) continue;
                    // Second line of defence behind SteamIsolation.RefuseOverlap: never walk into
                    // the folder being written, or the copy would chase its own output forever.
                    if (string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), destinationRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(dir, Path.Combine(dst, dir.Name));
                }
                else if (entry is FileInfo file)
                {
                    if (IsExcludedFile(file.Name)) continue;

                    var target = Path.Combine(dst, file.Name);
                    try
                    {
                        var existing = new FileInfo(target);
                        if (existing.Exists && IsReparsePoint(existing))
                        {
                            // Never write through a link planted in the seat folder.
                            failures.Add($"{target}: destination is a reparse point");
                            continue;
                        }
                        if (existing.Exists
                            && existing.Length == file.Length
                            && existing.LastWriteTimeUtc == file.LastWriteTimeUtc)
                            continue;

                        // File.Copy keeps the source write time, which is what the check above
                        // compares on the next refresh.
                        File.Copy(file.FullName, target, overwrite: true);
                        copied++;
                        bytes += file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{target}: {ex.Message}");
                    }
                }
            }
        }

        Walk(new DirectoryInfo(sourceDir), seatDir);
        return new SyncResult(copied, bytes, reparse, failures);
    }
}
