using System.Text.Json;
using Ipx2Mqtt.Ipx;

namespace Ipx2Mqtt.Tests;

public class IpxProtocolTests
{
    [Fact]
    public void ParseMessage_ReadsBooleanState()
    {
        var diag = new IpxDiagnostics();

        var change = Assert.Single(IpxStateParser.ParseMessage("{\"_id\":65638,\"on\":false}", diag));

        Assert.Equal(new IpxStateChange(65638, false), change);
        Assert.Equal(0, diag.TotalRejected);
    }

    [Fact]
    public void ParseMessage_ReadsNumericAndTextValues()
    {
        var diag = new IpxDiagnostics();

        Assert.Equal(new IpxStateChange(196618, 33.0), Assert.Single(IpxStateParser.ParseMessage("{\"_id\":196618,\"value\":33}", diag)));
        Assert.Equal(new IpxStateChange(524288, "49.5, 5.8"), Assert.Single(IpxStateParser.ParseMessage("{\"_id\":524288,\"value\":\"49.5, 5.8\"}", diag)));
    }

    [Fact]
    public void ParseMessage_AcceptsArrays()
    {
        var changes = IpxStateParser.ParseMessage("[{\"_id\":1,\"on\":true},{\"_id\":2,\"value\":1.5}]", new IpxDiagnostics());

        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"hello\":\"world\"}")]
    [InlineData("{\"_id\":\"abc\",\"on\":true}")]
    [InlineData("{\"_id\":1}")]
    [InlineData("{\"_id\":1,\"value\":null}")]
    [InlineData("42")]
    [InlineData("{\"_id\":1,\"value\":{\"nested\":true}}")]
    public void ParseMessage_IgnoresUnknownOrMalformedMessagesWithoutThrowing(string text)
    {
        var diag = new IpxDiagnostics();

        var changes = IpxStateParser.ParseMessage(text, diag);

        Assert.Empty(changes);
        Assert.Equal(1, diag.TotalRejected);
    }

    [Fact]
    public void ParseMessage_KeepsValidEntriesOfAMixedArray()
    {
        var diag = new IpxDiagnostics();

        var changes = IpxStateParser.ParseMessage("[{\"_id\":1,\"on\":true},{\"bad\":1}]", diag);

        Assert.Single(changes);
        Assert.Equal(1, diag.TotalRejected);
    }

    [Fact]
    public void ParseIoObjects_ReadsSnapshotAndSkipsBadEntries()
    {
        var diag = new IpxDiagnostics();
        using var doc = JsonDocument.Parse(
            "[{\"_id\":65536,\"name\":\"[01-IPX]Relay cmd 1\",\"link0\":0,\"link1\":1,\"virtual\":false,\"on\":true},{\"name\":\"broken\"}]");

        var objects = IpxStateParser.ParseIoObjects(doc.RootElement, diag);

        var relay = Assert.Single(objects);
        Assert.Equal(65536, relay.Id);
        Assert.Equal(IpxObjectKind.Io, relay.Kind);
        Assert.Equal(true, relay.Value);
        Assert.Equal(1, diag.TotalRejected);
    }

    [Fact]
    public void ParseAnaObjects_ReadsValueUnitAndDecimals()
    {
        using var doc = JsonDocument.Parse(
            "[{\"_id\":262153,\"name\":\"[WEATHER]Temperature day\",\"unit\":\"°C\",\"nbdecimal\":2,\"virtual\":false,\"value\":9.19}]");

        var temp = Assert.Single(IpxStateParser.ParseAnaObjects(doc.RootElement, new IpxDiagnostics()));

        Assert.Equal(9.19, temp.Value);
        Assert.Equal("°C", temp.Unit);
        Assert.Equal(2, temp.Decimals);
    }

    [Theory]
    [InlineData(65536, IpxObjectKind.Io)]
    [InlineData(65760, IpxObjectKind.Io)]
    [InlineData(196608, IpxObjectKind.Ana)]
    [InlineData(262153, IpxObjectKind.Ana)]
    [InlineData(327687, IpxObjectKind.Ana)]
    [InlineData(524288, IpxObjectKind.Str)]
    public void IdRanges_ClassifyObjects(long id, IpxObjectKind expected)
    {
        Assert.Equal(expected, IpxIdRanges.KindOf(id));
    }

    [Fact]
    public void IdRanges_ReturnNullForUnknownIds()
    {
        Assert.Null(IpxIdRanges.KindOf(12));
        Assert.Null(IpxIdRanges.KindOf(1048576));
    }

    [Fact]
    public void Diagnostics_AggregatesWarningsAndRespectsInterval()
    {
        var diag = new IpxDiagnostics();
        var logger = new CapturingLogger();

        diag.ReportIfDue(logger, TimeSpan.Zero);
        Assert.Empty(logger.Warnings);

        diag.RecordRejected("a", "x");
        diag.RecordRejected("b", "y");
        diag.ReportIfDue(logger, TimeSpan.FromHours(1));
        Assert.Empty(logger.Warnings);

        diag.ReportIfDue(logger, TimeSpan.Zero);
        Assert.Single(logger.Warnings);
        diag.ReportIfDue(logger, TimeSpan.Zero);
        Assert.Single(logger.Warnings);
        Assert.Equal(2, diag.TotalRejected);
    }

    [Fact]
    public void Crypto_RoundTripsWithAes128Ecb()
    {
        const string key = "oPUYnd10U1RBtSyX"; // 16 characters, like the IPX secureKey

        var encrypted = IpxCrypto.EncryptAesEcb("{\"login\":\"api\",\"passwd\":\"secret\"}", key);

        Assert.Equal("{\"login\":\"api\",\"passwd\":\"secret\"}", IpxCrypto.DecryptAesEcb(encrypted, key));
        // ECB is deterministic: same input and key give the same output.
        Assert.Equal(encrypted, IpxCrypto.EncryptAesEcb("{\"login\":\"api\",\"passwd\":\"secret\"}", key));
    }

    [Fact]
    public void Crypto_EncryptLoginWrapsCredentialsInTheExpectedJson()
    {
        const string key = "T40m4gb2SBE7mNW8";

        var encrypted = IpxCrypto.EncryptLogin("api", "pw", key);

        Assert.Equal("{\"login\":\"api\",\"passwd\":\"pw\"}", IpxCrypto.DecryptAesEcb(encrypted, key));
    }

    [Theory]
    [InlineData(true, false, "ON")]
    [InlineData(false, false, "OFF")]
    [InlineData(true, true, "OFF")]
    [InlineData(0.0, false, "OFF")]
    [InlineData(1.0, false, "ON")]
    [InlineData("on", false, "ON")]
    public void Payloads_OnOff(object value, bool invert, string expected)
    {
        Assert.Equal(expected, IpxPayloads.OnOff(value, invert));
    }

    [Fact]
    public void PushReceiver_ConvertsValues()
    {
        var receiver = new IpxPushReceiver();
        var received = new List<IpxStateChange>();
        receiver.StateChanged += received.Add;

        Assert.True(receiver.TrySubmit(65544, "on", out _));
        Assert.True(receiver.TrySubmit(262153, "9.5", out _));
        Assert.False(receiver.TrySubmit(1, " ", out var error));

        Assert.Equal(new[] { new IpxStateChange(65544, true), new IpxStateChange(262153, 9.5) }, received);
        Assert.NotEmpty(error);
    }
}
