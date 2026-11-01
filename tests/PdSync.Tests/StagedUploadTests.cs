using FluentAssertions;
using PdSync.Remote;

namespace PdSync.Tests;

public sealed class StagedUploadTests
{
    [Theory]
    [InlineData("pdsync-upload-0123456789ab")]
    [InlineData("pdsync-upload-abcdef012345")]
    [InlineData("pdsync-upload-000000000000")]
    [InlineData("pdsync-upload-ffffffffffff")]
    public void IsStagingLeaf_AcceptsExactGrammar(string leaf) =>
        StagedUpload.IsStagingLeaf(leaf).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pdsync-upload-")]
    [InlineData("pdsync-upload-0123456789a")]      // 11 hex
    [InlineData("pdsync-upload-0123456789abc")]    // 13 hex
    [InlineData("pdsync-upload-0123456789AB")]     // uppercase
    [InlineData("pdsync-upload-0123456789ag")]     // non-hex
    [InlineData("PDSYNC-upload-0123456789ab")]     // wrong case prefix
    [InlineData("pdsync-upload-0123456789ab.txt")] // extension
    [InlineData("prefix-pdsync-upload-0123456789ab")]
    [InlineData("pdsync-upload-0123456789ab/child")]
    public void IsStagingLeaf_RejectsAnythingElse(string? leaf) =>
        StagedUpload.IsStagingLeaf(leaf).Should().BeFalse();

    [Fact]
    public void NewName_IsAlwaysAValidStagingLeaf()
    {
        var name = StagedUpload.NewName();

        name.Should().StartWith(StagedUpload.Prefix);
        name.Should().HaveLength(StagedUpload.Prefix.Length + StagedUpload.HexLength);
        StagedUpload.IsStagingLeaf(name).Should().BeTrue();
    }
}
