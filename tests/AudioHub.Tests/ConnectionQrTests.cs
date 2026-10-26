using AudioHub.Core.Connection;

namespace AudioHub.Tests;

/// <summary>
/// Pins the QR payload format, which is the one string two platforms must read identically.
/// </summary>
public sealed class ConnectionQrTests
{
    private static readonly Guid HubId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Build_RendersEveryFieldInTheDocumentedOrder()
    {
        ConnectionQrPayload payload = new(
            "10.20.30.40",
            6980,
            "ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2",
            HubId,
            "b321-0f13-afca-6cd2",
            "DESKTOP-1");

        string text = ConnectionQr.Build(payload);

        Assert.Equal(
            "audiohub://c/1?h=10.20.30.40&p=6980&k=ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2"
            + "&i=11111111-2222-3333-4444-555555555555&f=b321-0f13-afca-6cd2&n=DESKTOP-1",
            text);
    }

    [Fact]
    public void Build_OmitsTheKeyWhenTheUserTurnedItOff()
    {
        string text = ConnectionQr.Build(new ConnectionQrPayload("192.168.1.5", 6980, Name: "PC"));

        Assert.DoesNotContain("k=", text);
        Assert.Contains("h=192.168.1.5", text);
        Assert.Contains("n=PC", text);
    }

    [Fact]
    public void Parse_RoundTripsAKeyAndANonAsciiName()
    {
        ConnectionQrPayload original = new(
            "172.20.3.4",
            7000,
            "ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2",
            HubId,
            "b321-0f13-afca-6cd2",
            "我的电脑");

        Assert.True(ConnectionQr.TryParse(ConnectionQr.Build(original), out ConnectionQrPayload? parsed));

        Assert.NotNull(parsed);
        Assert.Equal(original.Host, parsed!.Host);
        Assert.Equal(original.Port, parsed.Port);
        Assert.Equal(original.PairingKey, parsed.PairingKey);
        Assert.Equal(original.HubId, parsed.HubId);
        Assert.Equal(original.Fingerprint, parsed.Fingerprint);
        Assert.Equal(original.Name, parsed.Name);
        Assert.True(parsed.CarriesKey);
    }

    [Fact]
    public void Parse_AcceptsAPayloadWithoutTheOptionalFields()
    {
        Assert.True(ConnectionQr.TryParse("audiohub://c/1?h=10.0.0.1&p=6980", out ConnectionQrPayload? parsed));
        Assert.Equal("10.0.0.1", parsed!.Host);
        Assert.Null(parsed.PairingKey);
        Assert.Null(parsed.HubId);
        Assert.False(parsed.CarriesKey);
    }

    [Theory]
    [InlineData("https://example.com/c/1?h=10.0.0.1&p=6980")]
    [InlineData("audiohub://c/2?h=10.0.0.1&p=6980")]
    [InlineData("audiohub://other/1?h=10.0.0.1&p=6980")]
    [InlineData("audiohub://c/1?p=6980")]
    [InlineData("audiohub://c/1?h=not-an-ip&p=6980")]
    [InlineData("audiohub://c/1?h=::1&p=6980")]
    [InlineData("audiohub://c/1?h=10.0.0.1")]
    [InlineData("audiohub://c/1?h=10.0.0.1&p=0")]
    [InlineData("audiohub://c/1?h=10.0.0.1&p=70000")]
    [InlineData("不是二维码")]
    [InlineData("")]
    public void Parse_RejectsAnythingItCannotUseCompletely(string text)
    {
        Assert.False(ConnectionQr.TryParse(text, out ConnectionQrPayload? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_IgnoresUnknownFieldsSoOlderPhonesKeepWorking()
    {
        string text = "audiohub://c/1?h=10.0.0.1&p=6980&z=future&n=PC";

        Assert.True(ConnectionQr.TryParse(text, out ConnectionQrPayload? parsed));
        Assert.Equal("PC", parsed!.Name);
    }

    [Fact]
    public void Parse_KeepsTheFirstValueWhenAFieldRepeats()
    {
        string text = "audiohub://c/1?h=10.0.0.1&p=6980&h=10.0.0.2";

        Assert.True(ConnectionQr.TryParse(text, out ConnectionQrPayload? parsed));
        Assert.Equal("10.0.0.1", parsed!.Host);
    }
}
