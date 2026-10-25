using AudioHub.Core.Discovery;

namespace AudioHub.Tests;

/// <summary>
/// Pins the hub identity, which every later pairing promise is made against.
/// </summary>
/// <remarks>
/// If this identity ever drifts — regenerating on startup, changing when the network changes, or
/// reading back differently from how it was written — a phone would either have to re-pair for no
/// reason or, worse, accept a different hub as the one it already trusted. Both failures look the
/// same from the outside: everything stops working, with no explanation.
/// </remarks>
public sealed class HubIdentityTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public HubIdentityTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AudioHub.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "identity.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing has no bearing on the result.
        }
    }

    [Fact]
    public void FirstLoad_WritesTheIdentityToDisk()
    {
        Assert.False(File.Exists(_path));

        HubIdentity identity = HubIdentity.LoadOrCreate(_path);

        Assert.True(File.Exists(_path));
        Assert.NotEqual(Guid.Empty, identity.HubId);
    }

    [Fact]
    public void SecondLoad_ReturnsTheSameIdRatherThanANewOne()
    {
        Guid first = HubIdentity.LoadOrCreate(_path).HubId;

        // Deliberately a fresh instance reading the same file, which is exactly what a restart is.
        HubIdentity second = HubIdentity.LoadOrCreate(_path);

        Assert.Equal(first, second.HubId);
        Assert.Equal(first, second.HubId);
    }

    [Fact]
    public void SameId_AlwaysGivesTheSameFingerprint()
    {
        Guid id = Guid.Parse("8f2c1d4e-5b6a-4c8d-9e0f-1a2b3c4d5e6f");

        string first = HubIdentity.FromGuid(id).Fingerprint;
        string second = HubIdentity.FromGuid(id).Fingerprint;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Fingerprint_IsFourGroupsOfFourLowercaseHexDigits()
    {
        string fingerprint = HubIdentity.FromGuid(Guid.NewGuid()).Fingerprint;

        string[] groups = fingerprint.Split('-');
        Assert.Equal(4, groups.Length);
        Assert.All(groups, group =>
        {
            Assert.Equal(4, group.Length);
            Assert.All(group, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"'{c}' is not lowercase hex."));
        });
    }

    [Fact]
    public void DifferentIds_GiveDifferentFingerprints()
    {
        Assert.NotEqual(
            HubIdentity.FromGuid(Guid.NewGuid()).Fingerprint,
            HubIdentity.FromGuid(Guid.NewGuid()).Fingerprint);
    }

    [Fact]
    public void CorruptFile_IsReplacedRatherThanThrowing()
    {
        HubIdentity original = HubIdentity.LoadOrCreate(_path);
        File.WriteAllText(_path, "this is not json at all");

        HubIdentity recovered = HubIdentity.LoadOrCreate(_path);

        Assert.NotEqual(original.HubId, recovered.HubId);
        Assert.NotEqual(Guid.Empty, recovered.HubId);
    }

    [Fact]
    public void FutureFileVersion_IsIgnoredRatherThanMisread()
    {
        HubIdentity original = HubIdentity.LoadOrCreate(_path);
        File.WriteAllText(_path, $"{{\"Version\":999,\"HubId\":\"{Guid.NewGuid()}\"}}");

        HubIdentity recovered = HubIdentity.LoadOrCreate(_path);

        Assert.NotEqual(original.HubId, recovered.HubId);
    }

    [Fact]
    public void SavedIdentity_SurvivesAWriteAndReadRoundTrip()
    {
        Guid id = Guid.NewGuid();
        HubIdentity.FromGuid(id).Save(_path);

        Assert.Equal(id, HubIdentity.LoadOrCreate(_path).HubId);
    }

    [Fact]
    public void DefaultPath_LivesUnderLocalApplicationData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, HubIdentity.DefaultPath);
        Assert.EndsWith("identity.json", HubIdentity.DefaultPath);
        Assert.Contains("AudioHub", HubIdentity.DefaultPath);
    }
}
