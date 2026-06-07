using VastSpinup.Services;
using Xunit;

namespace VastSpinup.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.1.0",  0, 1, 0)]
    [InlineData("0.1.0",   0, 1, 0)]
    [InlineData("V2.0.0",  2, 0, 0)]
    [InlineData("v0.1.0.5", 0, 1, 0, 5)]
    public void TryParseTag_strips_v_prefix(string tag, int major, int minor, int build, int rev = -1)
    {
        Assert.True(UpdateChecker.TryParseTag(tag, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
        if (rev >= 0) Assert.Equal(rev, v.Revision);
    }

    [Theory]
    [InlineData("v1.2.3-rc1",   1, 2, 3)]
    [InlineData("1.2.3-alpha",  1, 2, 3)]
    [InlineData("v1.2.3+build7", 1, 2, 3)]
    public void TryParseTag_strips_prerelease_and_build_metadata(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateChecker.TryParseTag(tag, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(build, v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("vabc")]
    public void TryParseTag_rejects_garbage(string? tag)
    {
        Assert.False(UpdateChecker.TryParseTag(tag!, out _));
    }
}
