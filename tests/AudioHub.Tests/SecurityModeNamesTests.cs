using AudioHub.Core.Discovery;

namespace AudioHub.Tests;

public class SecurityModeNamesTests
{
    [Theory]
    [InlineData(SecurityMode.None, "None")]
    [InlineData(SecurityMode.Optional, "Optional")]
    [InlineData(SecurityMode.Required, "Required")]
    public void ToNameThenParseRoundTrips(SecurityMode mode, string expected)
    {
        Assert.Equal(expected, SecurityModeNames.ToName(mode));
        Assert.Equal(mode, SecurityModeNames.Parse(expected));
    }

    [Theory]
    [InlineData("none", SecurityMode.None)]
    [InlineData("  REQUIRED ", SecurityMode.Required)]
    [InlineData("optional", SecurityMode.Optional)]
    public void ParseIsCaseInsensitiveAndTrims(string text, SecurityMode expected) =>
        Assert.Equal(expected, SecurityModeNames.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public void UnknownFallsBackToOptional(string? text) =>
        Assert.Equal(SecurityMode.Optional, SecurityModeNames.Parse(text));
}
