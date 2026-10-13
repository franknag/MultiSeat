using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Steam;

namespace MultiSeat.Tests.Steam;

internal sealed class SteamNoopLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// A scratch area holding a fake system Steam install and a seat root, removed on dispose.
/// The Steam tree contains every kind of file the copy rules care about, so a test asserts on
/// what really lands in the seat folder rather than on the rules' own constants.
/// </summary>
internal sealed class SteamSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ms-steam-{Guid.NewGuid():N}");
    public string SteamDir => Path.Combine(Root, "Steam");
    public string SeatRoot => Path.Combine(Root, "seats");

    /// <summary>Directories handed to the protect step, in order, with the file count at that moment.</summary>
    public List<(string Dir, string Account, int FilesAlreadyThere)> Protected { get; } = [];

    public SteamSandbox()
    {
        Directory.CreateDirectory(SteamDir);
        Directory.CreateDirectory(SeatRoot);
        WriteSteamTree();
    }

    /// <summary>Files that must be copied, relative to the Steam folder.</summary>
    public static readonly string[] ClientFiles =
    [
        @"steam.exe", @"steamclient.dll", @"steamclient64.dll",
        @"bin\cef\cef.dll", @"bin\gldriverquery.exe",
        @"package\steam_client_win64.manifest", @"public\steam.css",
        @"resource\skins\ok.txt",
        // Looks like config to a careless filter but is not a name on the exclusion list.
        @"resource\configuration.txt",
        // A folder named like an excluded one but with extra characters is client data.
        @"bin\logs2\keep.txt",
        // "ssfn" only counts at the START of a file name.
        @"resource\notssfn.txt",
    ];

    /// <summary>Files that belong to ONE Steam and must never be copied.</summary>
    public static readonly string[] StateFiles =
    [
        @"ssfn1234567890", @"SSFNabcdef", @"loginusers.vdf", @"config\loginusers.vdf",
        @"config\config.vdf", @"logs\connection_log.txt", @"userdata\1234\config\localconfig.vdf",
        @"steamapps\common\Game\game.bin", @"steamapps\libraryfolders.vdf",
        @"dumps\crash.dmp", @"appcache\appinfo.vdf", @"depotcache\1.manifest",
        @"htmlcache\Cache\f_0001",
        // Excluded at ANY depth and without regard to case, as robocopy /XD does.
        @"bin\Logs\deep.txt", @"resource\CONFIG\nested.txt",
        @"public\userdata\x.txt",
    ];

    public void WriteSteamTree()
    {
        foreach (var rel in ClientFiles.Concat(StateFiles))
            Write(Path.Combine(SteamDir, rel), rel);
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public MultiSeatOptions Options(bool enabled = true, string? sourceDir = null, string? seatRoot = null) => new()
    {
        SharedGameLibraryDir = Path.Combine(Root, "shared"),
        SteamIsolation = new SteamIsolationOptions
        {
            Enabled = enabled,
            SourceDir = sourceDir ?? SteamDir,
            SeatRootDir = seatRoot ?? SeatRoot,
        },
    };

    public SteamIsolation Isolation(MultiSeatOptions? options = null, string? path = null) =>
        new(new SteamNoopLogger<SteamIsolation>(),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            readRegistryInstallPath: () => null,
            readPathVariable: () => path,
            protectDirectory: (dir, account) =>
                Protected.Add((dir, account, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length)));

    public static List<string> RelativeFiles(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
