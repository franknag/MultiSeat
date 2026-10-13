using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MultiSeat.Service;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Service.Steam;
using MultiSeat.Service.Streaming;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Steam;

/// <summary>
/// The three places Steam can start, each driven through its REAL method:
///   (a) SeatManager.LaunchAppInSeatAsync   (POST /api/seats/{id}/launch)
///   (b) OnConnectAppLauncher.ProcessSeat   (LaunchOnConnect)
///   (c) ApolloConfigBuilder.BuildConfig    (the seat's apps.json)
/// What is recorded is what the real code hands to ProcessInjector.LaunchInSessionAsync, or the
/// apps.json it really writes. Nothing here repeats the rewrite rules; they live in one class and
/// SteamRewriteTests covers them on their own.
/// </summary>
public class SteamLaunchPathTests
{
    /// <summary>A ProcessInjector that records the launch instead of creating a process.</summary>
    private sealed class RecordingInjector : ProcessInjector
    {
        public ConcurrentQueue<(int Session, string Account, string Exe, string? Args, string? WorkDir)> Launches { get; } = new();

        public RecordingInjector() : base(
            new SteamNoopLogger<ProcessInjector>(), Options.Create(new MultiSeatOptions()), sessionLauncher: null!) { }

        public override Task<int> LaunchInSessionAsync(
            int sessionId, string accountName, string exePath, string? arguments = null,
            string? workingDir = null, CancellationToken ct = default, bool allowConsoleSession = false)
        {
            Launches.Enqueue((sessionId, accountName, exePath, arguments, workingDir));
            return Task.FromResult(4242);
        }
    }

    private static string SeatDir(SteamSandbox sb, string account) => Path.Combine(sb.SeatRoot, $"Steam-{account}");

    // ── (a) POST /api/seats/{id}/launch ─────────────────────────────────────

    private static (SeatManager Manager, SeatInfo Seat, RecordingInjector Injector) NewSeatManager(
        SteamSandbox sb, MultiSeatOptions options, bool withSteam = true, string account = "Seat01")
    {
        var injector = new RecordingInjector();
        var steam = withSteam ? sb.Isolation(options, path: sb.SteamDir) : null;
        var mgr = new SeatManager(
            new SteamNoopLogger<SeatManager>(), Options.Create(options),
            accounts: null!, sessionLauncher: null!, processInjector: injector, displayManager: null!,
            apolloManager: null!, configBuilder: null!, portAllocator: null!, firewall: null!,
            audioRouter: null!, controllerManager: null!, inputRouter: null!, inputHookManager: null!,
            hidHide: null!, onConnectApps: null!, serverQuery: null!, hostApollo: null!,
            emulatorSeeders: Array.Empty<MultiSeat.Service.Emulators.IEmulatorConfigSeeder>(),
            lifecycleGate: new SeatLifecycleGate(),
            steamIsolation: steam);
        var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = account, SessionId = 7, Status = SeatStatus.Ready };
        mgr.RegisterSeatDirect(seat);
        return (mgr, seat, injector);
    }

    [Fact]
    public async Task ApiLaunch_OfTheSystemSteam_StartsTheSeatCopyWithItsOwnIpcName()
    {
        using var sb = new SteamSandbox();
        var (mgr, seat, injector) = NewSeatManager(sb, sb.Options());

        await mgr.LaunchAppInSeatAsync(seat.Id, new LaunchAppRequest
        {
            ExecutablePath = Path.Combine(sb.SteamDir, "steam.exe"), Arguments = "-bigpicture",
        }, CancellationToken.None);

        var launch = Assert.Single(injector.Launches);
        Assert.Equal(7, launch.Session);
        Assert.Equal(Path.Combine(SeatDir(sb, "Seat01"), "steam.exe"), launch.Exe);
        Assert.Equal($"-master_ipc_name_override {SteamIsolation.IpcNameFor("Seat01")} -bigpicture", launch.Args);
        Assert.Equal(SeatDir(sb, "Seat01"), launch.WorkDir);
        // The copy was made before the launch, so the executable it points at exists.
        Assert.True(File.Exists(launch.Exe));
        // The seat reports it, for the API and the dashboard.
        Assert.True(seat.SteamIsolation!.FolderPresent);
        Assert.Equal(SteamIsolation.IpcNameFor("Seat01"), seat.SteamIsolation.IpcName);
    }

    [Fact]
    public async Task ApiLaunch_OfSomethingElse_IsHandedOverExactlyAsRequested_EvenWithIsolationOn()
    {
        using var sb = new SteamSandbox();
        var (mgr, seat, injector) = NewSeatManager(sb, sb.Options());

        await mgr.LaunchAppInSeatAsync(seat.Id, new LaunchAppRequest
        {
            ExecutablePath = @"C:\Windows\notepad.exe", Arguments = "a.txt", WorkingDirectory = @"C:\work",
        }, CancellationToken.None);

        var launch = Assert.Single(injector.Launches);
        Assert.Equal((@"C:\Windows\notepad.exe", "a.txt", @"C:\work"), (launch.Exe, launch.Args, launch.WorkDir));
        Assert.Empty(Directory.GetDirectories(sb.SeatRoot));   // no Steam folder built for a non-Steam launch
        Assert.Null(seat.SteamIsolation);
    }

    [Fact]
    public async Task ApiLaunch_WithIsolationOff_IsByteIdenticalToToday_EvenForTheSystemSteam()
    {
        using var sb = new SteamSandbox();
        var (mgr, seat, injector) = NewSeatManager(sb, sb.Options(enabled: false));
        var exe = Path.Combine(sb.SteamDir, "steam.exe");

        await mgr.LaunchAppInSeatAsync(seat.Id, new LaunchAppRequest
        {
            ExecutablePath = exe, Arguments = "-bigpicture", WorkingDirectory = sb.SteamDir,
        }, CancellationToken.None);

        var launch = Assert.Single(injector.Launches);
        Assert.Equal((exe, "-bigpicture", sb.SteamDir), (launch.Exe, launch.Args, launch.WorkDir));
        Assert.Empty(Directory.GetDirectories(sb.SeatRoot));
        Assert.Null(seat.SteamIsolation);
    }

    [Fact]
    public async Task ApiLaunch_WithNoSteamIsolationServiceAtAll_StillWorks()
    {
        // Older call sites and tests construct the manager without the service.
        using var sb = new SteamSandbox();
        var (mgr, seat, injector) = NewSeatManager(sb, sb.Options(), withSteam: false);

        await mgr.LaunchAppInSeatAsync(seat.Id, new LaunchAppRequest { ExecutablePath = @"C:\x\y.exe" }, CancellationToken.None);

        Assert.Equal(@"C:\x\y.exe", Assert.Single(injector.Launches).Exe);
    }

    [Fact]
    public async Task ApiLaunch_IsRefusedAsAConflict_WhenTheSeatCopyCannotBeBuilt_AndNothingIsStarted()
    {
        using var sb = new SteamSandbox();
        var (mgr, seat, injector) = NewSeatManager(sb, sb.Options());
        // A folder named steam.exe in the seat folder makes the first copy fail.
        Directory.CreateDirectory(Path.Combine(SeatDir(sb, "Seat01"), "steam.exe"));

        var ex = await Assert.ThrowsAsync<ResourceConflictException>(() => mgr.LaunchAppInSeatAsync(
            seat.Id, new LaunchAppRequest { ExecutablePath = Path.Combine(sb.SteamDir, "steam.exe") }, CancellationToken.None));

        Assert.Contains("Could not build", ex.Message);
        Assert.Empty(injector.Launches);                      // never fell back to the system Steam
        Assert.Equal(SeatStatus.Ready, seat.Status);          // and the seat did not move to Streaming
    }

    // ── (b) LaunchOnConnect ─────────────────────────────────────────────────

    private static (OnConnectAppLauncher Launcher, string LogPath, string Root) NewWatcher(
        MultiSeatOptions options, RecordingInjector injector, SteamIsolation? steam)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ms-onconnect-steam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Seat01"));
        var logPath = Path.Combine(root, "Seat01", "apollo.log");
        File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));
        options.ApolloConfigDir = root;
        options.LaunchOnConnectDelayMs = 0;

        var apollo = new ApolloManager(new SteamNoopLogger<ApolloManager>(), Options.Create(options),
            configBuilder: null!, processInjector: null!);
        return (new OnConnectAppLauncher(new SteamNoopLogger<OnConnectAppLauncher>(), Options.Create(options),
            apollo, injector, steam), logPath, root);
    }

    private static async Task<(int Session, string Account, string Exe, string? Args, string? WorkDir)> NextLaunch(
        RecordingInjector injector)
    {
        for (var i = 0; i < 400; i++)
        {
            if (injector.Launches.TryPeek(out _)) { injector.Launches.TryDequeue(out var l); return l; }
            await Task.Delay(25);
        }
        throw new TimeoutException("no launch was recorded");
    }

    private static async Task<List<(int Session, string Account, string Exe, string? Args, string? WorkDir)>> AllLaunches(
        RecordingInjector injector, int expected)
    {
        var got = new List<(int, string, string, string?, string?)>();
        for (var i = 0; i < 400 && got.Count < expected; i++)
        {
            while (injector.Launches.TryDequeue(out var l)) got.Add(l);
            if (got.Count < expected) await Task.Delay(25);
        }
        return got;
    }

    private static void Connect(string logPath) =>
        File.AppendAllText(logPath, "[2026-10-13 10:00:00.000]: Info: CLIENT CONNECTED\n", new UTF8Encoding(false));

    [Fact]
    public async Task LaunchOnConnect_RedirectsASteamEntryToTheSeatCopy_AndLeavesOtherEntriesAlone()
    {
        using var sb = new SteamSandbox();
        var options = sb.Options();
        options.LaunchOnConnect =
        [
            new LaunchOnConnectApp { Path = Path.Combine(sb.SteamDir, "steam.exe"), Arguments = "-bigpicture" },
            new LaunchOnConnectApp { Path = @"C:\Tools\EmulationStation.exe", WorkingDirectory = @"C:\Tools" },
        ];
        var injector = new RecordingInjector();
        var (launcher, logPath, root) = NewWatcher(options, injector, sb.Isolation(options, sb.SteamDir));
        try
        {
            var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat01", SessionId = 5, Status = SeatStatus.Ready };
            launcher.ProcessSeat(seat, CancellationToken.None);   // seed at end of the empty log
            Connect(logPath);
            launcher.ProcessSeat(seat, CancellationToken.None);

            var launches = await AllLaunches(injector, 2);

            Assert.Equal(2, launches.Count);
            Assert.Equal(Path.Combine(SeatDir(sb, "Seat01"), "steam.exe"), launches[0].Exe);
            Assert.Equal($"-master_ipc_name_override {SteamIsolation.IpcNameFor("Seat01")} -bigpicture", launches[0].Args);
            Assert.Equal(SeatDir(sb, "Seat01"), launches[0].WorkDir);
            Assert.True(File.Exists(launches[0].Exe));
            Assert.Equal((@"C:\Tools\EmulationStation.exe", (string?)null, (string?)@"C:\Tools"),
                (launches[1].Exe, launches[1].Args, launches[1].WorkDir));
            Assert.True(seat.SteamIsolation!.FolderPresent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaunchOnConnect_WithIsolationOff_LaunchesExactlyWhatWasConfigured()
    {
        using var sb = new SteamSandbox();
        var options = sb.Options(enabled: false);
        var exe = Path.Combine(sb.SteamDir, "steam.exe");
        options.LaunchOnConnect = [new LaunchOnConnectApp { Path = exe, Arguments = "-bigpicture" }];
        var injector = new RecordingInjector();
        var (launcher, logPath, root) = NewWatcher(options, injector, sb.Isolation(options, sb.SteamDir));
        try
        {
            var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat01", SessionId = 5, Status = SeatStatus.Ready };
            launcher.ProcessSeat(seat, CancellationToken.None);
            Connect(logPath);
            launcher.ProcessSeat(seat, CancellationToken.None);

            var launch = await NextLaunch(injector);

            Assert.Equal((exe, "-bigpicture", (string?)null), (launch.Exe, launch.Args, launch.WorkDir));
            Assert.Empty(Directory.GetDirectories(sb.SeatRoot));
            Assert.Null(seat.SteamIsolation);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaunchOnConnect_DoesNotStartSteam_WhenTheSeatCopyCannotBeBuilt()
    {
        using var sb = new SteamSandbox();
        var options = sb.Options();
        var steamExe = Path.Combine(sb.SteamDir, "steam.exe");
        options.LaunchOnConnect =
        [
            new LaunchOnConnectApp { Path = steamExe },
            new LaunchOnConnectApp { Path = @"C:\Tools\Other.exe" },
        ];
        Directory.CreateDirectory(Path.Combine(SeatDir(sb, "Seat01"), "steam.exe"));   // the copy cannot succeed
        var injector = new RecordingInjector();
        var (launcher, logPath, root) = NewWatcher(options, injector, sb.Isolation(options, sb.SteamDir));
        try
        {
            var seat = new SeatInfo { Id = Guid.NewGuid(), AccountName = "Seat01", SessionId = 5, Status = SeatStatus.Ready };
            launcher.ProcessSeat(seat, CancellationToken.None);
            Connect(logPath);
            launcher.ProcessSeat(seat, CancellationToken.None);

            var launches = await AllLaunches(injector, 2);

            // Only the second app started; Steam was refused and did NOT fall back to the system one.
            var only = Assert.Single(launches);
            Assert.Equal(@"C:\Tools\Other.exe", only.Exe);
        }
        finally { Directory.Delete(root, true); }
    }

    // ── (c) the seat's Apollo apps.json ─────────────────────────────────────

    private static (ApolloConfigBuilder Builder, string ConfigDir, string Root) NewBuilder(
        SteamSandbox sb, MultiSeatOptions options, string appsJson, bool withSteam = true)
    {
        var apolloDir = Path.Combine(sb.Root, "apollo");
        Directory.CreateDirectory(Path.Combine(apolloDir, "config"));
        File.WriteAllText(Path.Combine(apolloDir, "config", "apps.json"), appsJson);
        options.ApolloExePath = Path.Combine(apolloDir, "sunshine.exe");
        options.ApolloConfigDir = Path.Combine(sb.Root, "seatcfg");
        var steam = withSteam ? sb.Isolation(options, sb.SteamDir) : null;
        return (new ApolloConfigBuilder(new SteamNoopLogger<ApolloConfigBuilder>(), Options.Create(options), steam),
            options.ApolloConfigDir, sb.Root);
    }

    private const string SteamApps = """
        { "apps": [
          { "name": "Desktop", "image-path": "desktop.png", "virtual-display": true },
          { "name": "Steam Big Picture", "cmd": "steam.exe -bigpicture" },
          { "name": "Notepad", "cmd": "notepad.exe" }
        ] }
        """;

    [Fact]
    public void ApolloApps_ForASeat_StartTheSeatsOwnSteam_AndKeepTheRest()
    {
        using var sb = new SteamSandbox();
        var (builder, configDir, _) = NewBuilder(sb, sb.Options(), SteamApps);

        builder.BuildConfig(new SeatInfo { AccountName = "Seat01", PortBase = 48100 }, configDir);

        var written = File.ReadAllText(Path.Combine(configDir, "Seat01", "config", "apps.json"));
        var apps = JsonNode.Parse(written)!["apps"]!.AsArray();
        var seatExe = Path.Combine(SeatDir(sb, "Seat01"), "steam.exe");
        Assert.Equal(
            $"{(seatExe.Contains(' ') ? $"\"{seatExe}\"" : seatExe)} -master_ipc_name_override {SteamIsolation.IpcNameFor("Seat01")} -bigpicture",
            apps[1]!["cmd"]!.GetValue<string>());
        Assert.Equal("notepad.exe", apps[2]!["cmd"]!.GetValue<string>());
        Assert.Null(apps[0]!["virtual-display"]);   // the existing console-only strip still happens
    }

    [Fact]
    public void ApolloApps_AreWrittenExactlyAsBefore_WhenIsolationIsOff()
    {
        using var sb = new SteamSandbox();
        var (off, dirOff, _) = NewBuilder(sb, sb.Options(enabled: false), SteamApps);
        off.BuildConfig(new SeatInfo { AccountName = "Seat01", PortBase = 48100 }, dirOff);
        var withFlagOff = File.ReadAllText(Path.Combine(dirOff, "Seat01", "config", "apps.json"));

        // The same builder built the way it was before this feature existed (no Steam service).
        using var sb2 = new SteamSandbox();
        var (none, dirNone, _) = NewBuilder(sb2, sb2.Options(), SteamApps, withSteam: false);
        none.BuildConfig(new SeatInfo { AccountName = "Seat01", PortBase = 48100 }, dirNone);
        var withoutService = File.ReadAllText(Path.Combine(dirNone, "Seat01", "config", "apps.json"));

        Assert.Equal(withoutService, withFlagOff);
        Assert.Contains("steam.exe -bigpicture", withFlagOff);
        Assert.DoesNotContain("master_ipc_name_override", withFlagOff);
    }

    [Fact]
    public void ApolloApps_GiveEachSeatItsOwnFolderAndIpcName()
    {
        using var sb = new SteamSandbox();
        var (builder, configDir, _) = NewBuilder(sb, sb.Options(), SteamApps);

        builder.BuildConfig(new SeatInfo { AccountName = "SeatA", PortBase = 48100 }, configDir);
        builder.BuildConfig(new SeatInfo { AccountName = "SeatB", PortBase = 48130 }, configDir);

        string Cmd(string account) => JsonNode.Parse(File.ReadAllText(
            Path.Combine(configDir, account, "config", "apps.json")))!["apps"]![1]!["cmd"]!.GetValue<string>();
        Assert.Contains("Steam-SeatA", Cmd("SeatA"));
        Assert.Contains(SteamIsolation.IpcNameFor("SeatA"), Cmd("SeatA"));
        Assert.Contains("Steam-SeatB", Cmd("SeatB"));
        Assert.Contains(SteamIsolation.IpcNameFor("SeatB"), Cmd("SeatB"));
        Assert.NotEqual(Cmd("SeatA"), Cmd("SeatB"));
    }
}
