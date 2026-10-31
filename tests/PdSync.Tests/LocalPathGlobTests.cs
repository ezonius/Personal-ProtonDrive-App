using FluentAssertions;
using PdSync.Scan;

namespace PdSync.Tests;

public sealed class LocalPathGlobTests
{
    [Fact]
    public void Escape_ProtectsGlobMetacharacters()
    {
        LocalPathGlob.Escape(@"C:\tmp\a[b]*?.txt")
            .Should().Be(@"C:\tmp\a[[]b][*][?].txt");
    }

    [Fact]
    public void Escape_LeavesOrdinaryPathsAlone()
    {
        LocalPathGlob.Escape("/tmp/a b.txt").Should().Be("/tmp/a b.txt");
    }

    [Fact]
    public void RequiresStaging_OnlyForBraceExpansion()
    {
        LocalPathGlob.RequiresStaging("/tmp/{a,b}.txt").Should().BeTrue();
        LocalPathGlob.RequiresStaging("/tmp/{a}.txt").Should().BeFalse();
        LocalPathGlob.RequiresStaging("/tmp/a[1].txt").Should().BeFalse();
    }
}
