using System.Text.Json;
using MultiSeat.Service.Api;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Steam;

/// <summary>
/// A default install must send the same seat JSON as before per-seat Steam existed, so the new
/// property is left out entirely while it is null. Serialized with the API's own options.
/// </summary>
public class SteamSeatJsonTests
{
    private static JsonElement Serialize(SeatInfo seat)
    {
        var options = new JsonSerializerOptions();
        ApiServer.ConfigureApiJson(options);
        return JsonDocument.Parse(JsonSerializer.Serialize(seat, options)).RootElement;
    }

    [Fact]
    public void WithTheFeatureOff_TheSeatJsonHasNoSteamIsolationProperty()
    {
        var json = Serialize(new SeatInfo { AccountName = "Seat01" });

        Assert.False(json.TryGetProperty("steamIsolation", out _));
        Assert.DoesNotContain("steamIsolation", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        // Other null properties are still written as before (only this one is omitted).
        Assert.True(json.TryGetProperty("errorMessage", out var err));
        Assert.Equal(JsonValueKind.Null, err.ValueKind);
    }

    [Fact]
    public void WithTheFeatureOn_TheStatusIsWritten()
    {
        var seat = new SeatInfo
        {
            AccountName = "Seat01",
            SteamIsolation = new SteamIsolationStatus
            {
                Enabled = true, IpcName = "ms-seat01-1a2b3c4d", Folder = @"C:\x\Steam-Seat01", FolderPresent = true,
            },
        };

        var s = Serialize(seat).GetProperty("steamIsolation");

        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal("ms-seat01-1a2b3c4d", s.GetProperty("ipcName").GetString());
        Assert.True(s.GetProperty("folderPresent").GetBoolean());
    }
}
