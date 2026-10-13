using MultiSeat.Service.Steam;
using Xunit;

namespace MultiSeat.Tests.Steam;

/// <summary>
/// The seat's own copy of the Steam client folder, built from a fake Steam tree on disk. The
/// assertions list what is really in the seat folder afterwards, so a wrong exclusion rule shows
/// up as a wrong file list and not as a wrong constant.
/// </summary>
public class SteamSeatCopyTests
{
    private static readonly CancellationToken NoCancel = CancellationToken.None;

    private static List<string> Expected(IEnumerable<string> files) =>
        files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public async Task FirstCopy_HasExactlyTheClientFiles_AndNoneOfTheStateFiles()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var result = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.True(result.Created);
        Assert.Equal(0, result.Failures);
        var copied = SteamSandbox.RelativeFiles(result.SeatDir)
            .Where(f => f != SteamClientCopier.MarkerFileName).ToList();
        Assert.Equal(Expected(SteamSandbox.ClientFiles), copied);

        // Spelled out so the intent is readable, not just the equality above.
        foreach (var state in SteamSandbox.StateFiles)
            Assert.False(File.Exists(Path.Combine(result.SeatDir, state)), $"{state} must not be copied");
        Assert.False(Directory.Exists(Path.Combine(result.SeatDir, "steamapps")));
        Assert.False(Directory.Exists(Path.Combine(result.SeatDir, "config")));
    }

    [Fact]
    public async Task Copy_PreservesContent_AndTheSourceIsLeftUntouched()
    {
        using var sb = new SteamSandbox();
        var before = SteamSandbox.RelativeFiles(sb.SteamDir);

        var result = await sb.Isolation().EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.Equal(@"bin\cef\cef.dll", File.ReadAllText(Path.Combine(result.SeatDir, @"bin\cef\cef.dll")));
        Assert.Equal(before, SteamSandbox.RelativeFiles(sb.SteamDir));
    }

    [Fact]
    public async Task TheSeatFolderIsProtected_BeforeTheFirstFileIsCopied_AndOnlyWhenItIsCreated()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        await iso.EnsureSeatCopyAsync("Seat01", NoCancel);
        File.WriteAllText(Path.Combine(sb.SteamDir, "steam.exe"), "updated client");
        await iso.EnsureSeatCopyAsync("Seat01", NoCancel);   // a refresh, not a creation

        var call = Assert.Single(sb.Protected);
        Assert.Equal(Path.Combine(sb.SeatRoot, "Steam-Seat01"), call.Dir);
        Assert.Equal("Seat01", call.Account);
        // Files copied after the ACL is set inherit it. Copied first, they would keep the parent's.
        Assert.Equal(0, call.FilesAlreadyThere);
    }

    [Fact]
    public async Task SecondCall_WithNothingChanged_CopiesNothing()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        var again = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.True(again.UpToDate);
        Assert.False(again.Created);
        Assert.Equal(0, again.FilesCopied);
    }

    [Fact]
    public async Task Refresh_PicksUpAnUpdatedClient_AndLeavesEverythingTheSeatOwnsAlone()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = (await iso.EnsureSeatCopyAsync("Seat01", NoCancel)).SeatDir;

        // The seat uses its Steam: it signs in, downloads a game, writes config and logs.
        var seatOwned = new Dictionary<string, string>
        {
            [@"config\config.vdf"] = "seat's own config",
            [@"config\loginusers.vdf"] = "seat's login",
            ["loginusers.vdf"] = "seat's login at the root",
            ["ssfn777"] = "seat's sentry file",
            [@"userdata\555\config\localconfig.vdf"] = "seat's userdata",
            [@"steamapps\common\Game\game.bin"] = "seat's downloaded game",
            [@"logs\bootstrap_log.txt"] = "seat's log",
            [@"appcache\appinfo.vdf"] = "seat's cache",
        };
        foreach (var (rel, text) in seatOwned) SteamSandbox.Write(Path.Combine(seatDir, rel), text);

        // The system Steam updates itself: a client file changes and a new one appears.
        File.WriteAllText(Path.Combine(sb.SteamDir, "steam.exe"), "new client build, longer");
        SteamSandbox.Write(Path.Combine(sb.SteamDir, @"package\steam_client_new.manifest"), "m2");
        // ...and the system Steam's own login data changes too. None of it may reach the seat.
        File.WriteAllText(Path.Combine(sb.SteamDir, @"config\config.vdf"), "console's NEW config");
        File.WriteAllText(Path.Combine(sb.SteamDir, "loginusers.vdf"), "console's NEW login");

        var refresh = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.False(refresh.UpToDate);
        Assert.False(refresh.Created);
        Assert.Equal("new client build, longer", File.ReadAllText(Path.Combine(seatDir, "steam.exe")));
        Assert.True(File.Exists(Path.Combine(seatDir, @"package\steam_client_new.manifest")));
        foreach (var (rel, text) in seatOwned)
            Assert.Equal(text, File.ReadAllText(Path.Combine(seatDir, rel)));
    }

    [Fact]
    public async Task Refresh_LeavesAnUnchangedFileUntouched_ItOnlyCopiesWhatDiffers()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = (await iso.EnsureSeatCopyAsync("Seat01", NoCancel)).SeatDir;

        // Plant bytes in the seat's copy that differ from the source but have the SAME size and
        // write time. A refresh that trusts size and write time must leave them alone; one that
        // recopies every file would put the source's bytes back.
        var unchanged = Path.Combine(seatDir, @"public\steam.css");
        var original = File.ReadAllText(unchanged);
        var planted = new string('Z', original.Length);
        var stamp = File.GetLastWriteTimeUtc(unchanged);
        File.WriteAllText(unchanged, planted);
        File.SetLastWriteTimeUtc(unchanged, stamp);

        // A real client update elsewhere triggers the refresh.
        File.WriteAllText(Path.Combine(sb.SteamDir, "steam.exe"), "new client build");

        var refresh = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.False(refresh.UpToDate);
        Assert.Equal(1, refresh.FilesCopied);                          // only steam.exe
        Assert.Equal(planted, File.ReadAllText(unchanged));            // bytes untouched
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(unchanged));      // write time untouched
        Assert.Equal("new client build", File.ReadAllText(Path.Combine(seatDir, "steam.exe")));
    }

    [Fact]
    public void Sync_NeverWalksIntoTheFolderItIsWriting_EvenIfItSitsInsideTheSource()
    {
        using var sb = new SteamSandbox();
        var inside = Path.Combine(sb.SteamDir, "seats", "Steam-Seat01");

        var result = SteamClientCopier.Sync(sb.SteamDir, inside, NoCancel);

        Assert.Empty(result.Failures);
        Assert.DoesNotContain(SteamSandbox.RelativeFiles(inside), f => f.StartsWith("seats", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Expected(SteamSandbox.ClientFiles), SteamSandbox.RelativeFiles(inside));
    }

    [Fact]
    public async Task Refresh_NeverDeletesAFileTheSourceNoLongerHas()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = (await iso.EnsureSeatCopyAsync("Seat01", NoCancel)).SeatDir;
        SteamSandbox.Write(Path.Combine(seatDir, "seat_made_this.txt"), "x");   // e.g. Steam.cfg
        File.Delete(Path.Combine(sb.SteamDir, @"public\steam.css"));
        File.WriteAllText(Path.Combine(sb.SteamDir, "steamclient.dll"), "newer");

        await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.True(File.Exists(Path.Combine(seatDir, "seat_made_this.txt")));
        Assert.True(File.Exists(Path.Combine(seatDir, @"public\steam.css")));
    }

    [Fact]
    public async Task RefusesWhenTheSourceHasNoSteamExe_AndCreatesNothing()
    {
        using var sb = new SteamSandbox();
        File.Delete(Path.Combine(sb.SteamDir, "steam.exe"));

        var ex = await Assert.ThrowsAsync<SteamIsolationException>(
            () => sb.Isolation().EnsureSeatCopyAsync("Seat01", NoCancel));

        Assert.Contains("no steam.exe", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(sb.SeatRoot, "Steam-Seat01")));
        Assert.Empty(sb.Protected);
    }

    [Fact]
    public async Task RefusesWhenTheSourceFolderDoesNotExist()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation(sb.Options(sourceDir: Path.Combine(sb.Root, "NoSteamHere")));

        await Assert.ThrowsAsync<SteamIsolationException>(() => iso.EnsureSeatCopyAsync("Seat01", NoCancel));
        Assert.Empty(Directory.GetDirectories(sb.SeatRoot));
    }

    [Theory]
    [InlineData("inside")]    // seat root inside the Steam folder: the copy would walk into its own output
    [InlineData("same")]
    public async Task RefusesWhenTheSeatFolderAndTheSteamFolderOverlap(string layout)
    {
        using var sb = new SteamSandbox();
        var seatRoot = layout == "inside" ? Path.Combine(sb.SteamDir, "seats") : sb.SteamDir;
        var iso = sb.Isolation(sb.Options(seatRoot: seatRoot));

        var ex = await Assert.ThrowsAsync<SteamIsolationException>(
            () => iso.EnsureSeatCopyAsync("Seat01", NoCancel));

        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public async Task RefusesAnUnusableAccountName_BeforeAnythingIsCreated()
    {
        using var sb = new SteamSandbox();

        await Assert.ThrowsAsync<SteamIsolationException>(
            () => sb.Isolation().EnsureSeatCopyAsync("..\\..\\Windows", NoCancel));

        Assert.Empty(Directory.GetDirectories(sb.SeatRoot));
    }

    [Fact]
    public async Task TwoSeats_GetTwoSeparateFolders_AndTheirDataStaysApart()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var a = await iso.EnsureSeatCopyAsync("SeatA", NoCancel);
        var b = await iso.EnsureSeatCopyAsync("SeatB", NoCancel);
        SteamSandbox.Write(Path.Combine(a.SeatDir, @"config\config.vdf"), "A");

        Assert.NotEqual(a.SeatDir, b.SeatDir);
        Assert.False(File.Exists(Path.Combine(b.SeatDir, @"config\config.vdf")));
    }

    [Fact]
    public async Task ConcurrentCallsForOneSeat_AllSucceed_AndTheFolderIsComplete()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => iso.EnsureSeatCopyAsync("Seat01", NoCancel)));

        Assert.Single(results, r => r.Created);               // exactly one did the work
        Assert.Single(sb.Protected);
        Assert.Equal(Expected(SteamSandbox.ClientFiles),
            SteamSandbox.RelativeFiles(results[0].SeatDir).Where(f => f != SteamClientCopier.MarkerFileName).ToList());
    }

    [Fact]
    public async Task ARefreshThatCannotReplaceAFileInUse_KeepsTheWorkingCopy_AndRetriesNextTime()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = (await iso.EnsureSeatCopyAsync("Seat01", NoCancel)).SeatDir;
        File.WriteAllText(Path.Combine(sb.SteamDir, "steamclient.dll"), "updated dll, different size");

        // The seat's Steam is running and holds its copy open for writing.
        using (new FileStream(Path.Combine(seatDir, "steamclient.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);
            Assert.Equal(1, result.Failures);
            Assert.True(File.Exists(Path.Combine(seatDir, "steam.exe")));   // still usable
        }

        // Not recorded as done, so the next call tries again and now succeeds.
        var retry = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);
        Assert.False(retry.UpToDate);
        Assert.Equal(0, retry.Failures);
        Assert.Equal("updated dll, different size", File.ReadAllText(Path.Combine(seatDir, "steamclient.dll")));
    }

    [Fact]
    public async Task AFirstCopyThatFails_IsRefused_NotReportedAsReady()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = Path.Combine(sb.SeatRoot, "Steam-Seat01");
        // A FOLDER named steam.exe makes the copy of steam.exe fail while there is no working
        // steam.exe in the seat folder, which is the case where launching must be refused.
        Directory.CreateDirectory(Path.Combine(seatDir, "steam.exe"));

        var ex = await Assert.ThrowsAsync<SteamIsolationException>(
            () => iso.EnsureSeatCopyAsync("Seat01", NoCancel));

        Assert.Contains("Could not build", ex.Message);
        Assert.Null(SteamClientCopier.ReadMarker(seatDir));
        Assert.False(iso.Describe("Seat01")!.FolderPresent);
    }

    // ── Reparse points ──────────────────────────────────────────────────────

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public async Task ASourceJunction_IsNotFollowed_SoNothingOutsideTheTreeIsCopied()
    {
        using var sb = new SteamSandbox();
        var outside = Path.Combine(sb.Root, "outside");
        SteamSandbox.Write(Path.Combine(outside, "secret.txt"), "from outside the Steam folder");
        if (!TryMakeJunction(Path.Combine(sb.SteamDir, "bin", "escape"), outside))
            return;   // cannot create a junction here; nothing to assert

        var result = await sb.Isolation().EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.False(Directory.Exists(Path.Combine(result.SeatDir, "bin", "escape")));
        Assert.DoesNotContain(SteamSandbox.RelativeFiles(result.SeatDir), f => f.EndsWith("secret.txt"));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));   // the target is intact
    }

    [Fact]
    public async Task ADestinationJunction_IsNotWrittenThrough_SoNothingLandsOutsideTheSeatFolder()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = Path.Combine(sb.SeatRoot, "Steam-Seat01");
        var outside = Path.Combine(sb.Root, "victim");
        Directory.CreateDirectory(seatDir);
        if (!TryMakeJunction(Path.Combine(seatDir, "bin"), outside))
            return;

        var result = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        // bin\ is a link planted in the seat folder. Files under it must not be written through it.
        Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        Assert.True(result.Failures > 0 || !File.Exists(Path.Combine(outside, "gldriverquery.exe")));
    }

    [Fact]
    public async Task ASeatFolderThatIsItselfAJunction_IsRefused()
    {
        using var sb = new SteamSandbox();
        var seatDir = Path.Combine(sb.SeatRoot, "Steam-Seat01");
        var outside = Path.Combine(sb.Root, "victim");
        if (!TryMakeJunction(seatDir, outside))
            return;

        await Assert.ThrowsAsync<SteamIsolationException>(
            () => sb.Isolation().EnsureSeatCopyAsync("Seat01", NoCancel));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // ── The fingerprint ─────────────────────────────────────────────────────

    [Fact]
    public void Marker_ChangesWhenTheClientChanges_AndNotWhenOnlyStateChanges()
    {
        using var sb = new SteamSandbox();
        var start = SteamClientCopier.ComputeMarker(sb.SteamDir);
        Assert.Equal(start, SteamClientCopier.ComputeMarker(sb.SteamDir));

        // The console's own Steam writes logs and config all day. That must not look like an update.
        File.WriteAllText(Path.Combine(sb.SteamDir, @"logs\connection_log.txt"), "more log lines than before");
        File.WriteAllText(Path.Combine(sb.SteamDir, @"config\config.vdf"), "different");
        Assert.Equal(start, SteamClientCopier.ComputeMarker(sb.SteamDir));

        File.WriteAllText(Path.Combine(sb.SteamDir, "steam.exe"), "a new client build");
        var afterExe = SteamClientCopier.ComputeMarker(sb.SteamDir);
        Assert.NotEqual(start, afterExe);

        SteamSandbox.Write(Path.Combine(sb.SteamDir, @"package\steam_client_new.manifest"), "m");
        Assert.NotEqual(afterExe, SteamClientCopier.ComputeMarker(sb.SteamDir));
    }

    [Fact]
    public async Task AMarkerWithoutASteamExe_VouchesForNothing()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var seatDir = (await iso.EnsureSeatCopyAsync("Seat01", NoCancel)).SeatDir;
        File.Delete(Path.Combine(seatDir, "steam.exe"));

        var again = await iso.EnsureSeatCopyAsync("Seat01", NoCancel);

        Assert.False(again.UpToDate);
        Assert.True(File.Exists(Path.Combine(seatDir, "steam.exe")));
    }

    // ── Exclusion rules, one name at a time ─────────────────────────────────

    [Theory]
    [InlineData("steamapps", true)] [InlineData("STEAMAPPS", true)] [InlineData("logs", true)]
    [InlineData("config", true)] [InlineData("userdata", true)] [InlineData("dumps", true)]
    [InlineData("appcache", true)] [InlineData("depotcache", true)] [InlineData("htmlcache", true)]
    [InlineData("bin", false)] [InlineData("package", false)] [InlineData("logs2", false)]
    [InlineData("myconfig", false)] [InlineData("steamapps2", false)]
    public void ExcludedDirectories_AreExactNamesOnly(string name, bool excluded) =>
        Assert.Equal(excluded, SteamClientCopier.IsExcludedDirectory(name));

    [Theory]
    [InlineData("ssfn1", true)] [InlineData("SSFN9999999", true)] [InlineData("ssfn", true)]
    [InlineData("loginusers.vdf", true)] [InlineData("LoginUsers.VDF", true)]
    [InlineData("notssfn", false)] [InlineData("loginusers.vdf.bak", false)]
    [InlineData("steam.exe", false)] [InlineData("config.vdf", false)]
    public void ExcludedFiles_FollowTheirRules(string name, bool excluded) =>
        Assert.Equal(excluded, SteamClientCopier.IsExcludedFile(name));
}
