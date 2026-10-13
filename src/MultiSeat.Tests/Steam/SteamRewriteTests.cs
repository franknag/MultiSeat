using System.Text.Json.Nodes;
using MultiSeat.Service.Steam;
using Xunit;

namespace MultiSeat.Tests.Steam;

/// <summary>
/// The pure rules of per-seat Steam: how a seat's IPC name is made, when a launch counts as "the
/// system Steam", and exactly how its arguments change. Every input here is a real string a
/// caller could send; none of it needs a Windows session.
/// </summary>
public class SteamRewriteTests
{
    // ── IPC name ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Seat01")]
    [InlineData("a")]
    [InlineData("Gamer.One-2")]
    [InlineData("ABCDEFGHIJKLMNOPQRST")]   // 20 characters, the longest the API allows
    public void IpcName_IsDeterministic_AndOnlyLowercaseDigitsAndHyphens(string account)
    {
        var first = SteamIsolation.IpcNameFor(account);
        Assert.Equal(first, SteamIsolation.IpcNameFor(account));
        Assert.Matches("^ms-[a-z0-9-]{1,12}-[0-9a-f]{8}$", first);
        Assert.True(first.Length <= 24, first);
    }

    [Fact]
    public void IpcName_IsTheSameForDifferentCase_BecauseWindowsAccountsAreCaseInsensitive()
    {
        Assert.Equal(SteamIsolation.IpcNameFor("SeatOne"), SteamIsolation.IpcNameFor("seatone"));
    }

    [Fact]
    public void IpcName_IsUniquePerAccount_EvenWhenTheReadablePartCollides()
    {
        // "Gamer.1" and "Gamer-1" both read "gamer-1"; names that agree for 12 characters and
        // differ afterwards share their whole readable part. Only the hash tells them apart.
        var names = new[]
        {
            "Gamer.1", "Gamer-1", "Gamer_1x",
            "LongAccountA1", "LongAccountA2", "LongAccountB1",
        }.Where(SteamIsolation.IsUsableAccountName)
         .Select(SteamIsolation.IpcNameFor).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..\\evil")]
    [InlineData("a b")]
    [InlineData("Seat\"01")]
    [InlineData("Seat01 -evil")]
    [InlineData("Seat;01")]
    [InlineData("Seat&calc")]
    [InlineData("Seat%PATH%")]
    [InlineData("-Seat")]
    [InlineData(".Seat")]
    [InlineData("Seat.")]                      // Windows strips the dot: would share a folder with "Seat"
    [InlineData("TwentyOneCharactersXY")]      // 21 characters
    [InlineData("Seat\n01")]
    [InlineData("Seät")]
    public void HostileOrMalformedAccountNames_AreRefused_NotSanitised(string account)
    {
        Assert.False(SteamIsolation.IsUsableAccountName(account));
        Assert.Throws<SteamIsolationException>(() => SteamIsolation.IpcNameFor(account));
        Assert.Throws<SteamIsolationException>(() => SteamIsolation.SeatFolderName(account));
    }

    [Fact]
    public void IpcName_NeverContainsAnythingASteamCommandLineCouldMisreadAsAnotherArgument()
    {
        var name = SteamIsolation.IpcNameFor("Gamer.One-2");
        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain('\\', name);
        Assert.False(name.StartsWith('-'));
    }

    [Fact]
    public void SeatFolderName_FollowsTheAccount() =>
        Assert.Equal("Steam-Seat01", SteamIsolation.SeatFolderName("Seat01"));

    // ── Path matching ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"c:\program files (x86)\steam\STEAM.EXE")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"C:/Program Files (x86)/Steam/steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"""C:\Program Files (x86)\Steam\steam.exe""")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"C:\Program Files (x86)\Steam\.\bin\..\steam.exe")]
    [InlineData(@"C:\Steam\", @"C:\Steam")]
    [InlineData(@"C:\Steam", @"  C:\Steam\\  ")]
    public void SamePath_IgnoresCaseSlashesQuotesDotsAndTrailingSeparators(string a, string b) =>
        Assert.True(SteamIsolation.SamePath(a, b));

    [Theory]
    [InlineData(@"C:\Steam\steam.exe", @"C:\Steam2\steam.exe")]
    [InlineData(@"C:\Steam\steam.exe", @"C:\Steam\steamwebhelper.exe")]
    [InlineData(@"C:\Steam\steam.exe", @"D:\Steam\steam.exe")]
    [InlineData(@"C:\Steam", @"C:\Steam\bin")]
    public void SamePath_RejectsDifferentLocations(string a, string b) =>
        Assert.False(SteamIsolation.SamePath(a, b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad\0path\\steam.exe")]
    public void NormalizePath_GivesNull_ForTextThatIsNotAPath(string? text)
    {
        Assert.Null(SteamIsolation.NormalizePath(text));
        Assert.False(SteamIsolation.SamePath(text, text));
    }

    [Fact]
    public void ResolveExecutable_FindsABareNameOnPath_ButNotOneThatIsNotThere()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation(path: $"{Path.Combine(sb.Root, "nowhere")};{sb.SteamDir}");

        Assert.True(iso.IsSystemSteam("steam.exe", null));
        Assert.True(iso.IsSystemSteam("STEAM", null));          // CreateProcess adds .exe
        Assert.False(iso.IsSystemSteam("notsteam.exe", null));   // not on PATH at all
        Assert.False(sb.Isolation(path: null).IsSystemSteam("steam.exe", null)); // PATH has no Steam
    }

    [Fact]
    public void IsSystemSteam_UsesTheSourceDir_NotAHardcodedPath()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        Assert.True(iso.IsSystemSteam(Path.Combine(sb.SteamDir, "steam.exe"), null));
        Assert.True(iso.IsSystemSteam(Path.Combine(sb.SteamDir, "STEAM.EXE").Replace('\\', '/'), null));
        Assert.False(iso.IsSystemSteam(Path.Combine(sb.SteamDir, "bin", "gldriverquery.exe"), null));
        Assert.False(iso.IsSystemSteam(@"C:\Program Files (x86)\Steam\steam.exe", null)); // sandbox Steam is elsewhere
    }

    [Fact]
    public void SourceDir_FallsBackFromOptionToRegistryToTheStandardPath()
    {
        using var sb = new SteamSandbox();
        var opts = sb.Options(sourceDir: " ");
        opts.SteamIsolation.SourceDir = string.Empty;

        SteamIsolation Make(string? reg) => new(
            new SteamNoopLogger<SteamIsolation>(), Microsoft.Extensions.Options.Options.Create(opts),
            () => reg, () => null, null);

        Assert.True(SteamIsolation.SamePath(@"D:\Games\Steam", Make("d:/Games/Steam").ResolveSourceDir()));
        Assert.Equal(@"C:\Program Files (x86)\Steam", Make(null).ResolveSourceDir());

        opts.SteamIsolation.SourceDir = sb.SteamDir;
        Assert.Equal(sb.SteamDir, Make(@"D:\Games\Steam").ResolveSourceDir());
    }

    [Fact]
    public void SeatRoot_DefaultsToTheSharedLibraryDir_AndTheSeatFolderSitsBeneathIt()
    {
        using var sb = new SteamSandbox();
        var opts = sb.Options();
        opts.SteamIsolation.SeatRootDir = string.Empty;
        var iso = sb.Isolation(opts);

        Assert.Equal(Path.Combine(sb.Root, "shared", "Steam-Seat01"), iso.SeatDirFor("Seat01"));
    }

    // ── Arguments ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "-master_ipc_name_override ms-x")]
    [InlineData("", "-master_ipc_name_override ms-x")]
    [InlineData("   ", "-master_ipc_name_override ms-x")]
    [InlineData("-bigpicture", "-master_ipc_name_override ms-x -bigpicture")]
    [InlineData("-silent -login a b", "-master_ipc_name_override ms-x -silent -login a b")]
    public void WithOverride_PutsTheFlagFirst(string? args, string expected) =>
        Assert.Equal(expected, SteamIsolation.WithOverride(args, "ms-x"));

    [Theory]
    [InlineData("-master_ipc_name_override mine -bigpicture")]
    [InlineData("-bigpicture -master_ipc_name_override mine")]
    [InlineData("-MASTER_IPC_NAME_OVERRIDE mine")]
    [InlineData("\"-master_ipc_name_override\" mine")]
    public void WithOverride_LeavesACallersOwnOverrideAlone(string args)
    {
        Assert.True(SteamIsolation.HasOverride(args));
        Assert.Equal(args, SteamIsolation.WithOverride(args, "ms-x"));
    }

    [Fact]
    public void WithOverride_IsIdempotent_ItNeverStacksTheFlag()
    {
        var once = SteamIsolation.WithOverride("-bigpicture", "ms-x");
        var twice = SteamIsolation.WithOverride(once, "ms-x");

        Assert.Equal(once, twice);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "master_ipc_name_override"));
    }

    [Fact]
    public void HasOverride_DoesNotMistakeALongerSwitchOrAValueForTheFlag()
    {
        Assert.False(SteamIsolation.HasOverride("-master_ipc_name_override_not"));
        Assert.False(SteamIsolation.HasOverride("-login master_ipc_name_override"));
        Assert.False(SteamIsolation.HasOverride(null));
    }

    // ── Splitting a command line ────────────────────────────────────────────

    [Theory]
    [InlineData("steam.exe -bigpicture", "steam.exe", "-bigpicture")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -bigpicture", @"C:\Program Files (x86)\Steam\steam.exe", "-bigpicture")]
    [InlineData("C:\\Program Files (x86)\\Steam\\steam.exe -bigpicture -silent", @"C:\Program Files (x86)\Steam\steam.exe", "-bigpicture -silent")]
    [InlineData("C:\\Steam\\steam.exe", @"C:\Steam\steam.exe", "")]
    [InlineData("  \"C:\\Steam\\steam.exe\"", @"C:\Steam\steam.exe", "")]
    [InlineData("notepad foo.txt", "notepad", "foo.txt")]
    [InlineData("steam://rungameid/10", "steam://rungameid/10", "")]
    public void SplitCommand_SeparatesTheProgramFromItsArguments(string command, string program, string remainder)
    {
        var (p, r) = SteamIsolation.SplitCommand(command);
        Assert.Equal(program, p);
        Assert.Equal(remainder, r);
    }

    // ── Rewrite: the single decision all three launch paths use ─────────────

    [Fact]
    public void Rewrite_RedirectsTheSystemSteam_ToTheSeatCopy_WithTheOverrideFirst()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var r = iso.Rewrite("Seat01", Path.Combine(sb.SteamDir, "steam.exe"), "-bigpicture", null);

        Assert.True(r.Rewritten);
        Assert.Equal(Path.Combine(sb.SeatRoot, "Steam-Seat01", "steam.exe"), r.Executable);
        Assert.Equal($"-master_ipc_name_override {SteamIsolation.IpcNameFor("Seat01")} -bigpicture", r.Arguments);
        Assert.Equal(Path.Combine(sb.SeatRoot, "Steam-Seat01"), r.WorkingDirectory);
    }

    [Fact]
    public void Rewrite_KeepsAWorkingDirectoryThatIsNotTheSystemSteamFolder_AndMovesOneThatIs()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var exe = Path.Combine(sb.SteamDir, "steam.exe");

        Assert.Equal(@"D:\elsewhere", iso.Rewrite("Seat01", exe, null, @"D:\elsewhere").WorkingDirectory);
        Assert.Equal(Path.Combine(sb.SeatRoot, "Steam-Seat01"),
            iso.Rewrite("Seat01", exe, null, sb.SteamDir + "\\").WorkingDirectory);
    }

    [Fact]
    public void Rewrite_GivenTheSameLaunchTwice_GivesTheSameAnswer_AndFeedingItsOutputBackChangesNothingMore()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var exe = Path.Combine(sb.SteamDir, "steam.exe");

        var a = iso.Rewrite("Seat01", exe, "-bigpicture", null);
        var b = iso.Rewrite("Seat01", exe, "-bigpicture", null);
        Assert.Equal(a, b);

        // Already pointing at the seat copy: not the system Steam, so left exactly as given.
        var again = iso.Rewrite("Seat01", a.Executable, a.Arguments, a.WorkingDirectory);
        Assert.False(again.Rewritten);
        Assert.Equal(a.Arguments, again.Arguments);
        Assert.Equal(a.Executable, again.Executable);
    }

    [Fact]
    public void Rewrite_DoesNotAddASecondOverride_WhenTheCallerSuppliedOne()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var r = iso.Rewrite("Seat01", Path.Combine(sb.SteamDir, "steam.exe"),
            "-master_ipc_name_override mine -bigpicture", null);

        Assert.True(r.Rewritten);   // still goes to the seat's own folder
        Assert.Equal("-master_ipc_name_override mine -bigpicture", r.Arguments);
    }

    [Theory]
    [InlineData(@"bin\gldriverquery.exe")]
    [InlineData(@"bin\steamwebhelper.exe")]
    public void Rewrite_LeavesEveryOtherExecutableUntouched(string relative)
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();
        var exe = Path.Combine(sb.SteamDir, relative);

        var r = iso.Rewrite("Seat01", exe, "-x", @"C:\work");

        Assert.False(r.Rewritten);
        Assert.Equal((exe, "-x", @"C:\work"), (r.Executable, r.Arguments, r.WorkingDirectory));
    }

    [Fact]
    public void Rewrite_LeavesANonSteamProgramUntouched_EvenWithAnUnusableAccountName()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        var r = iso.Rewrite("not a valid name", @"C:\Windows\notepad.exe", "a.txt", null);

        Assert.False(r.Rewritten);
        Assert.Equal(@"C:\Windows\notepad.exe", r.Executable);
    }

    [Fact]
    public void Rewrite_RefusesTheSystemSteam_ForAnUnusableAccountName_RatherThanFallingThrough()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation();

        Assert.Throws<SteamIsolationException>(() =>
            iso.Rewrite("..\\evil", Path.Combine(sb.SteamDir, "steam.exe"), null, null));
    }

    [Fact]
    public void Rewrite_WhenDisabled_ReturnsExactlyTheInput_EvenForTheSystemSteam()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation(sb.Options(enabled: false));
        var exe = Path.Combine(sb.SteamDir, "steam.exe");

        var r = iso.Rewrite("Seat01", exe, "-bigpicture", sb.SteamDir);

        Assert.False(r.Rewritten);
        Assert.Equal((exe, "-bigpicture", sb.SteamDir), (r.Executable, r.Arguments, r.WorkingDirectory));
        Assert.Null(iso.Describe("Seat01"));
    }

    // ── apps.json ───────────────────────────────────────────────────────────

    [Fact]
    public void RewriteAppsJson_PointsSteamEntriesAtTheSeatCopy_AndNothingElse()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation(path: sb.SteamDir);
        var seatExe = Path.Combine(sb.SeatRoot, "Steam-Seat01", "steam.exe");
        var ipc = SteamIsolation.IpcNameFor("Seat01");
        var json = $$"""
            {
              "env": { "PATH": "$(PATH)" },
              "apps": [
                { "name": "Desktop", "image-path": "desktop.png" },
                { "name": "Bare", "cmd": "steam.exe -bigpicture" },
                { "name": "Full", "cmd": "\"{{Path.Combine(sb.SteamDir, "steam.exe").Replace("\\", "\\\\")}}\" -bigpicture", "working-dir": "{{sb.SteamDir.Replace("\\", "\\\\")}}" },
                { "name": "Detached", "cmd": "", "detached": ["{{Path.Combine(sb.SteamDir, "steam.exe").Replace("\\", "\\\\")}} steam://rungameid/10", "notepad.exe"] },
                { "name": "Other", "cmd": "C:\\Windows\\notepad.exe a.txt" },
                { "name": "Own", "cmd": "steam.exe -master_ipc_name_override mine" }
              ]
            }
            """;

        var result = iso.RewriteAppsJson("Seat01", json, out var count);

        var apps = JsonNode.Parse(result)!["apps"]!.AsArray();
        string Cmd(int i) => apps[i]!["cmd"]!.GetValue<string>();
        var seatExeQuoted = seatExe.Contains(' ') ? $"\"{seatExe}\"" : seatExe;

        Assert.Equal(4, count);
        Assert.Null(apps[0]!["cmd"]);
        Assert.Equal($"{seatExeQuoted} -master_ipc_name_override {ipc} -bigpicture", Cmd(1));
        Assert.Equal($"{seatExeQuoted} -master_ipc_name_override {ipc} -bigpicture", Cmd(2));
        Assert.Equal(Path.Combine(sb.SeatRoot, "Steam-Seat01"), apps[2]!["working-dir"]!.GetValue<string>());
        Assert.Equal($"{seatExeQuoted} -master_ipc_name_override {ipc} steam://rungameid/10",
            apps[3]!["detached"]![0]!.GetValue<string>());
        Assert.Equal("notepad.exe", apps[3]!["detached"]![1]!.GetValue<string>());
        Assert.Equal(@"C:\Windows\notepad.exe a.txt", Cmd(4));
        Assert.Equal($"{seatExeQuoted} -master_ipc_name_override mine", Cmd(5));
        Assert.Equal("$(PATH)", JsonNode.Parse(result)!["env"]!["PATH"]!.GetValue<string>());
    }

    [Fact]
    public void RewriteAppsJson_ReturnsTheTextByteForByte_WhenDisabled_NothingMatches_OrItIsNotJson()
    {
        using var sb = new SteamSandbox();
        const string noSteam = "{ \"apps\": [ { \"name\": \"A\", \"cmd\": \"notepad.exe\" } ] }";
        const string withSteam = "{ \"apps\": [ { \"name\": \"A\", \"cmd\": \"steam.exe -bigpicture\" } ] }";

        Assert.Same(noSteam, sb.Isolation(path: sb.SteamDir).RewriteAppsJson("Seat01", noSteam, out var n1));
        Assert.Equal(0, n1);

        Assert.Same(withSteam, sb.Isolation(sb.Options(enabled: false), sb.SteamDir)
            .RewriteAppsJson("Seat01", withSteam, out var n2));
        Assert.Equal(0, n2);

        const string garbage = "{ this is not json";
        Assert.Same(garbage, sb.Isolation(path: sb.SteamDir).RewriteAppsJson("Seat01", garbage, out _));
    }

    [Fact]
    public void RewriteAppsJson_IsIdempotent_RunningItOnItsOwnOutputChangesNothing()
    {
        using var sb = new SteamSandbox();
        var iso = sb.Isolation(path: sb.SteamDir);
        const string json = "{ \"apps\": [ { \"name\": \"S\", \"cmd\": \"steam.exe -bigpicture\" } ] }";

        var once = iso.RewriteAppsJson("Seat01", json, out var first);
        var twice = iso.RewriteAppsJson("Seat01", once, out var second);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Same(once, twice);
    }
}
