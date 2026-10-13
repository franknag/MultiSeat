using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using MultiSeat.Service.Api;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Steam;

/// <summary>
/// A refusal to start Steam for a seat. Raised instead of letting the launch fall through to the
/// system Steam, because that is exactly what this feature exists to prevent: starting the host's
/// Steam from a seat closes the Steam running in the console or in another seat.
/// </summary>
internal sealed class SteamIsolationException : InvalidOperationException
{
    public SteamIsolationException(string message) : base(message) { }
}

/// <summary>A launch after <see cref="SteamIsolation"/> has had its say.</summary>
/// <param name="Rewritten">False when the launch is exactly what the caller passed in.</param>
internal readonly record struct SteamLaunch(
    string Executable, string? Arguments, string? WorkingDirectory, bool Rewritten);

/// <summary>What <see cref="SteamIsolation.EnsureSeatCopyAsync"/> found and did.</summary>
internal sealed record SteamCopyResult(
    string SeatDir, bool Created, bool UpToDate, int FilesCopied, long BytesCopied, int Failures);

/// <summary>
/// Lets each seat run its own Steam next to the console's and the other seats'.
///
/// WHY. Steam allows one running instance per machine and enforces it through a machine-wide IPC
/// name, so starting Steam in a seat closes the Steam in the console session or in another seat.
/// Verified live on this project's reference host (2026-10-13): launching with
/// <c>-master_ipc_name_override NAME</c> lets two Steams run side by side. But two Steams on the
/// SAME install folder did not work: the second failed to sign in, because both share config,
/// logs and login files. Giving the seat its OWN copy of the client folder fixed it. So this
/// class does two things:
///   1. keeps a per-seat copy of the Steam client folder (<see cref="SteamClientCopier"/>), and
///   2. rewrites every launch of the system steam.exe to start that copy with a private IPC name.
///
/// NOT VERIFIED: that games and Steam's helper processes find the right Steam. One helper,
/// bin\gldriverquery.exe, exited with 0xC0000135 in a seat; the cause is unknown.
///
/// OFF BY DEFAULT. With <c>MultiSeat:SteamIsolation:Enabled = false</c> every method here returns
/// its input unchanged and touches nothing on disk.
///
/// ONE PLACE. Steam can start from three paths: POST /api/seats/{id}/launch
/// (SeatManager.LaunchAppInSeatAsync), LaunchOnConnect (OnConnectAppLauncher), and the app
/// entries in a seat's Apollo apps.json (ApolloConfigBuilder). Each calls this class, so the
/// matching and the argument rule exist once. A launch that is not the system Steam's steam.exe
/// is never touched.
///
/// The seat folder is NOT deleted when a seat is torn down. It holds the player's Steam login and
/// downloads, and a re-provisioned seat reuses it. docs/steam-isolation.md says how to remove it.
/// </summary>
public sealed class SteamIsolation
{
    /// <summary>The Steam switch that gives an instance its own IPC name.</summary>
    internal const string OverrideFlag = "-master_ipc_name_override";

    private static readonly Regex UnquotedExe =
        new(@"^(?<exe>.*?\.exe)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SteamIsolation> _logger;
    private readonly MultiSeatOptions _options;
    private readonly Func<string?> _readRegistryInstallPath;
    private readonly Func<string?> _readPathVariable;
    private readonly Action<string, string> _protectDirectory;

    // One copy or refresh per seat at a time: a provision and a launch can arrive together.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public SteamIsolation(ILogger<SteamIsolation> logger, IOptions<MultiSeatOptions> options)
        : this(logger, options, ReadInstallPathFromRegistry,
               () => Environment.GetEnvironmentVariable("PATH"), null)
    {
    }

    // The three seams exist so tests can supply a registry value, a PATH and a directory-protect
    // step without a real Steam, a real seat account or an elevated token.
    internal SteamIsolation(
        ILogger<SteamIsolation> logger, IOptions<MultiSeatOptions> options,
        Func<string?> readRegistryInstallPath, Func<string?> readPathVariable,
        Action<string, string>? protectDirectory)
    {
        _logger = logger;
        _options = options.Value;
        _readRegistryInstallPath = readRegistryInstallPath;
        _readPathVariable = readPathVariable;
        _protectDirectory = protectDirectory ?? ProtectSeatDirectory;
    }

    public bool Enabled => _options.SteamIsolation.Enabled;

    // ── Names ───────────────────────────────────────────────────────────

    /// <summary>
    /// True for an account name that is safe to put in a folder name and to derive an IPC name
    /// from. It is the same rule the API already enforces on seat accounts, plus: no trailing dot,
    /// which Windows strips from a folder name, so "Gamer." and "Gamer" would share one folder.
    /// </summary>
    internal static bool IsUsableAccountName(string? account) =>
        ApiInputValidation.IsValidAccountName(account) && !account!.EndsWith('.');

    /// <summary>
    /// The seat's Steam IPC name: "ms-", up to 12 readable characters of the account name, "-",
    /// then 8 hex digits of a hash of the whole lower-cased name.
    ///
    /// Deterministic (the same account always gets the same name, so a restarted seat finds its
    /// own Steam), unique per account (the hash separates "Gamer.1" from "Gamer-1" and from
    /// accounts that differ only after the 12th character), at most 24 characters, and made only
    /// of a-z, 0-9 and "-", so it is safe as a command-line value whatever the account is called.
    /// Windows account names are case-insensitive, so the case is folded before hashing.
    /// </summary>
    internal static string IpcNameFor(string account)
    {
        if (!IsUsableAccountName(account))
            throw new SteamIsolationException($"'{account}' is not a usable seat account name.");

        var folded = account.ToLowerInvariant();
        var slug = new StringBuilder();
        foreach (var c in folded)
        {
            if (slug.Length == 12) break;
            slug.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folded)))[..8].ToLowerInvariant();
        return $"ms-{slug}-{hash}";
    }

    /// <summary>The seat's folder name under the seat root.</summary>
    internal static string SeatFolderName(string account)
    {
        if (!IsUsableAccountName(account))
            throw new SteamIsolationException($"'{account}' is not a usable seat account name.");
        return $"Steam-{account}";
    }

    // ── Paths ───────────────────────────────────────────────────────────

    /// <summary>
    /// A full, comparable form of a path: quotes and surrounding blanks removed, "." and ".."
    /// resolved, forward slashes turned into backslashes, trailing separators dropped. Null for
    /// text that is not a path. Does not touch the disk, so it does not resolve 8.3 short names
    /// or links; a launch spelled that way is not recognised as Steam and is left alone.
    /// </summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool SamePath(string? a, string? b)
    {
        var na = NormalizePath(a);
        var nb = NormalizePath(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadInstallPathFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The system Steam folder: the configured SourceDir, else Steam's own InstallPath from the
    /// registry, else the standard 32-bit Program Files location.
    /// </summary>
    internal string ResolveSourceDir()
    {
        var configured = NormalizePath(_options.SteamIsolation.SourceDir);
        if (configured is not null) return configured;

        var registry = NormalizePath(_readRegistryInstallPath());
        if (registry is not null) return registry;

        return @"C:\Program Files (x86)\Steam";
    }

    internal string SystemSteamExe => Path.Combine(ResolveSourceDir(), "steam.exe");

    /// <summary>Where the per-seat folders live: SeatRootDir, else the shared game library root.</summary>
    internal string ResolveSeatRoot() =>
        NormalizePath(_options.SteamIsolation.SeatRootDir)
        ?? NormalizePath(_options.SharedGameLibraryDir)
        ?? @"C:\MultiSeatGames";

    internal string SeatDirFor(string account) => Path.Combine(ResolveSeatRoot(), SeatFolderName(account));

    internal string SeatSteamExeFor(string account) => Path.Combine(SeatDirFor(account), "steam.exe");

    /// <summary>
    /// The full path an executable name will run as, or null when it cannot be told. A path with a
    /// folder is made absolute (against the working directory when relative). A bare name such as
    /// "steam.exe" is searched for along PATH the way CreateProcess would, with ".exe" added when
    /// there is no extension. Reads the disk only to look for bare names on PATH.
    /// </summary>
    internal string? ResolveExecutable(string? exe, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var text = exe.Trim().Trim('"');

        var hasFolder = text.Contains('\\') || text.Contains('/') || text.Contains(':');
        if (hasFolder)
        {
            if (!Path.IsPathRooted(text) && !string.IsNullOrWhiteSpace(workingDirectory))
                text = Path.Combine(workingDirectory, text);
            return NormalizePath(text);
        }

        var name = Path.HasExtension(text) ? text : text + ".exe";
        var searchDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory)) searchDirs.Add(workingDirectory);
        searchDirs.AddRange((_readPathVariable() ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in searchDirs)
        {
            var candidate = NormalizePath(Path.Combine(dir.Trim('"'), name));
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ── Arguments ───────────────────────────────────────────────────────

    internal static bool HasOverride(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t.Trim('"'), OverrideFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Put the override in front of the caller's arguments, once. If the caller already passed
    /// their own override, theirs wins and the text is returned untouched: they chose a name on
    /// purpose, and a second flag would leave it unclear which one Steam obeys.
    /// </summary>
    internal static string WithOverride(string? arguments, string ipcName)
    {
        if (HasOverride(arguments)) return arguments!;
        return string.IsNullOrWhiteSpace(arguments)
            ? $"{OverrideFlag} {ipcName}"
            : $"{OverrideFlag} {ipcName} {arguments}";
    }

    private static string QuoteIfNeeded(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// Split a command line such as <c>"C:\Steam\steam.exe" -bigpicture</c> into program and the
    /// rest. A quoted program ends at the closing quote. An unquoted one ends at the first ".exe"
    /// followed by a blank or the end, so an unquoted path with spaces
    /// (<c>C:\Program Files (x86)\Steam\steam.exe -bigpicture</c>) still splits correctly; if
    /// there is no ".exe" it ends at the first blank.
    /// </summary>
    internal static (string Program, string Remainder) SplitCommand(string command)
    {
        var text = command.TrimStart();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? (text[1..], string.Empty) : (text[1..close], text[(close + 1)..].TrimStart());
        }

        var m = UnquotedExe.Match(text);
        if (m.Success) return (m.Groups["exe"].Value, text[m.Length..].TrimStart());

        var blank = text.IndexOfAny([' ', '\t']);
        return blank < 0 ? (text, string.Empty) : (text[..blank], text[(blank + 1)..].TrimStart());
    }

    // ── The rewrite ─────────────────────────────────────────────────────

    /// <summary>True when this launch starts the system Steam's steam.exe.</summary>
    internal bool IsSystemSteam(string? exe, string? workingDirectory)
    {
        var resolved = ResolveExecutable(exe, workingDirectory);
        return resolved is not null
            && string.Equals(resolved, NormalizePath(SystemSteamExe), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pure half of a launch rewrite: no folder is created or copied.
    ///
    /// Disabled, or anything but the system Steam's steam.exe: returned exactly as given.
    /// The system steam.exe: redirected to the seat's copy, <c>-master_ipc_name_override</c> put in
    /// front of the arguments (once, never over the caller's own), and the working directory moved
    /// to the seat folder when it was unset or was the system Steam folder.
    /// </summary>
    internal SteamLaunch Rewrite(string account, string exe, string? arguments, string? workingDirectory)
    {
        var unchanged = new SteamLaunch(exe, arguments, workingDirectory, false);
        if (!Enabled || !IsSystemSteam(exe, workingDirectory)) return unchanged;

        var seatDir = SeatDirFor(account);                 // throws on an unusable account
        var seatWorkingDir =
            string.IsNullOrWhiteSpace(workingDirectory) || SamePath(workingDirectory, ResolveSourceDir())
                ? seatDir
                : workingDirectory;

        return new SteamLaunch(
            SeatSteamExeFor(account), WithOverride(arguments, IpcNameFor(account)), seatWorkingDir, true);
    }

    /// <summary>
    /// What the launch paths in SeatManager and OnConnectAppLauncher call. Rewrites the launch
    /// and, when it was rewritten, makes sure the seat's Steam folder exists and is current first.
    /// Throws <see cref="SteamIsolationException"/> when the seat's Steam cannot be made ready,
    /// so the launch is refused rather than quietly started on the system Steam.
    /// </summary>
    internal async Task<SteamLaunch> PrepareLaunchAsync(
        string account, string exe, string? arguments, string? workingDirectory, CancellationToken ct)
    {
        var launch = Rewrite(account, exe, arguments, workingDirectory);
        if (launch.Rewritten)
            await EnsureSeatCopyAsync(account, ct);
        return launch;
    }

    /// <summary>
    /// The app-list half, used by ApolloConfigBuilder. Rewrites the <c>cmd</c> of each app (and each
    /// string in its <c>detached</c> list) that starts the system steam.exe, and moves a
    /// <c>working-dir</c> that is the system Steam folder. Returns the document text unchanged,
    /// byte for byte, when nothing matched, when this is disabled, or when the text is not JSON.
    /// Only paths are rewritten here; the copy is made when the seat is provisioned and again at
    /// launch, so a missing copy shows up as a failed launch and not as a silently wrong one.
    /// <c>prep-cmd</c> entries are not touched.
    /// </summary>
    internal string RewriteAppsJson(string account, string json, out int rewritten)
    {
        rewritten = 0;
        if (!Enabled) return json;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }

        if (root?["apps"] is not JsonArray apps) return json;

        foreach (var app in apps)
        {
            if (app is not JsonObject entry) continue;
            var appDir = entry["working-dir"] is JsonValue wd && wd.TryGetValue<string>(out var wdText) ? wdText : null;

            var touched = false;
            if (entry["cmd"] is JsonValue cmdNode && cmdNode.TryGetValue<string>(out var cmd))
            {
                var next = RewriteCommandLine(account, cmd, appDir);
                if (next is not null) { entry["cmd"] = next; touched = true; }
            }

            if (entry["detached"] is JsonArray detached)
            {
                for (var i = 0; i < detached.Count; i++)
                {
                    if (detached[i] is JsonValue d && d.TryGetValue<string>(out var text)
                        && RewriteCommandLine(account, text, appDir) is { } next)
                    {
                        detached[i] = next;
                        touched = true;
                    }
                }
            }

            if (touched)
            {
                rewritten++;
                if (appDir is not null && SamePath(appDir, ResolveSourceDir()))
                    entry["working-dir"] = SeatDirFor(account);
            }
        }

        return rewritten == 0
            ? json
            : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The rewritten command line, or null when it does not start the system Steam.</summary>
    private string? RewriteCommandLine(string account, string commandLine, string? workingDirectory)
    {
        var (program, rest) = SplitCommand(commandLine);
        if (!IsSystemSteam(program, workingDirectory)) return null;

        var arguments = WithOverride(rest, IpcNameFor(account));
        return $"{QuoteIfNeeded(SeatSteamExeFor(account))} {arguments}";
    }

    // ── The seat's folder ───────────────────────────────────────────────

    /// <summary>
    /// Create the seat's Steam folder, or bring it up to date with the system Steam, and return
    /// what was done. Cheap when nothing changed: it compares a small fingerprint of the system
    /// Steam with the one recorded in the seat folder and stops there.
    ///
    /// A refresh adds and overwrites client files only. It never touches the seat's own config,
    /// userdata, steamapps or login files, and never deletes anything (see SteamClientCopier).
    ///
    /// Throws <see cref="SteamIsolationException"/> when the system Steam has no steam.exe, when
    /// the folders overlap, or when a first copy fails. A refresh that fails part-way on a seat
    /// that already has a working copy (a file is held open by the running seat Steam) is logged
    /// and the existing copy is used; the next launch tries again.
    /// </summary>
    internal async Task<SteamCopyResult> EnsureSeatCopyAsync(string account, CancellationToken ct)
    {
        var seatDir = SeatDirFor(account);                 // validates the account
        var source = ResolveSourceDir();

        if (!File.Exists(Path.Combine(source, "steam.exe")))
            throw new SteamIsolationException(
                $"Steam is not installed at '{source}' (no steam.exe there), so there is nothing to copy " +
                "for this seat. Install Steam, or set MultiSeat:SteamIsolation:SourceDir.");

        RefuseOverlap(source, seatDir);

        var gate = _locks.GetOrAdd(seatDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => EnsureSeatCopy(account, source, seatDir, ct), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private SteamCopyResult EnsureSeatCopy(string account, string source, string seatDir, CancellationToken ct)
    {
        if (SteamClientCopier.IsUpToDate(source, seatDir))
            return new SteamCopyResult(seatDir, false, true, 0, 0, 0);

        var destInfo = new DirectoryInfo(seatDir);
        if (destInfo.Exists && (destInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SteamIsolationException(
                $"The seat Steam folder '{seatDir}' is a link. It is left alone; remove the link or choose another SeatRootDir.");

        var created = !destInfo.Exists;
        if (created)
        {
            Directory.CreateDirectory(seatDir);
            // Before the first file lands, so every copied file inherits the restricted ACL.
            _protectDirectory(seatDir, account);
        }

        var hadWorkingCopy = File.Exists(Path.Combine(seatDir, "steam.exe"));
        var marker = SteamClientCopier.ComputeMarker(source);
        var sync = SteamClientCopier.Sync(source, seatDir, ct);

        if (sync.Failures.Count == 0)
        {
            File.WriteAllText(Path.Combine(seatDir, SteamClientCopier.MarkerFileName), marker);
            _logger.LogInformation(
                "Seat {Account}: Steam folder {Dir} {What}: {Files} file(s), {Mb:F0} MB copied",
                account, seatDir, created ? "created" : "refreshed", sync.FilesCopied,
                sync.BytesCopied / 1048576.0);
        }
        else if (hadWorkingCopy || File.Exists(Path.Combine(seatDir, "steam.exe")))
        {
            _logger.LogWarning(
                "Seat {Account}: refreshing {Dir} failed for {Count} file(s), so the existing copy is used " +
                "and the refresh is retried at the next launch. First: {First}",
                account, seatDir, sync.Failures.Count, sync.Failures[0]);
        }
        else
        {
            throw new SteamIsolationException(
                $"Could not build the Steam folder '{seatDir}' ({sync.Failures.Count} file(s) failed). " +
                $"First: {sync.Failures[0]}");
        }

        return new SteamCopyResult(
            seatDir, created, false, sync.FilesCopied, sync.BytesCopied, sync.Failures.Count);
    }

    /// <summary>One folder inside the other would make the copy walk into its own output.</summary>
    internal static void RefuseOverlap(string source, string seatDir)
    {
        var s = NormalizePath(source)!.TrimEnd('\\') + "\\";
        var d = NormalizePath(seatDir)!.TrimEnd('\\') + "\\";
        if (s.StartsWith(d, StringComparison.OrdinalIgnoreCase) || d.StartsWith(s, StringComparison.OrdinalIgnoreCase))
            throw new SteamIsolationException(
                $"The Steam source '{source}' and the seat folder '{seatDir}' overlap. " +
                "Set MultiSeat:SteamIsolation:SeatRootDir outside the Steam install.");
    }

    /// <summary>
    /// Lock the seat's folder to SYSTEM, Administrators and that seat (Modify), without inheriting.
    ///
    /// This is narrower than the shared library's pattern, which grants BUILTIN\Users Modify.
    /// That is right for ROMs and SteamLibrary, which seats are meant to share. It would be wrong
    /// here: the folder holds one player's Steam login, and an inherited Users grant would let
    /// every other seat read or replace it. So this reuses the seat credentials ACL that
    /// ApolloConfigBuilder already applies to the seat's Apollo folders.
    ///
    /// If the account cannot be resolved the folder keeps what it inherits and a warning says so.
    /// Failing the launch instead would not help: a seat whose account does not resolve cannot
    /// run Steam in the first place.
    /// </summary>
    private void ProtectSeatDirectory(string dir, string account)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, account)
                .Translate(typeof(SecurityIdentifier));
            new DirectoryInfo(dir).SetAccessControl(ApolloConfigBuilder.BuildSeatCredentialsAcl(sid));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not restrict {Dir} to {Account}; it keeps its inherited permissions, so other " +
                "local accounts may be able to read this seat's Steam login", dir, account);
        }
    }

    // ── Reporting ───────────────────────────────────────────────────────

    /// <summary>
    /// What the API and dashboard show for a seat, or null when this feature is off (or the
    /// account name is unusable), so a default install adds nothing to a seat's JSON.
    /// </summary>
    internal SteamIsolationStatus? Describe(string account)
    {
        if (!Enabled || !IsUsableAccountName(account)) return null;

        var seatDir = SeatDirFor(account);
        return new SteamIsolationStatus
        {
            Enabled = true,
            IpcName = IpcNameFor(account),
            Folder = seatDir,
            FolderPresent = File.Exists(Path.Combine(seatDir, "steam.exe")),
        };
    }
}
