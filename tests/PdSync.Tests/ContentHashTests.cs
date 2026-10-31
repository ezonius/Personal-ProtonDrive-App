using System.Text;
using FluentAssertions;
using PdSync.Scan;

namespace PdSync.Tests;

public sealed class ContentHashTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EmptyContent_HasTheWellKnownSha1()
    {
        ContentHash.ComputeSha1(new MemoryStream()).Should()
            .Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
    }

    [Fact]
    public void Abc_HasTheWellKnownSha1()
    {
        var bytes = Encoding.UTF8.GetBytes("abc");

        ContentHash.ComputeSha1(new MemoryStream(bytes)).Should()
            .Be("a9993e364706816aba3e25717850c26c9cd0d89d");
    }

    [Fact]
    public void FileOverload_MatchesTheStreamOverload()
    {
        var path = _temp.Write("abc.txt", "abc");

        ContentHash.ComputeSha1(path).Should()
            .Be(ContentHash.ComputeSha1(new MemoryStream(Encoding.UTF8.GetBytes("abc"))));
    }

    [Fact]
    public void Cache_RequiresMatchingSizeAndMtime()
    {
        var cache = new ContentHashCache();

        cache.Set("a.txt", 10, 100, "hash-a");

        cache.TryGet("a.txt", 10, 100, out var hit).Should().BeTrue();
        hit.Should().Be("hash-a");

        cache.TryGet("a.txt", 11, 100, out _).Should().BeFalse();
        cache.TryGet("a.txt", 10, 101, out _).Should().BeFalse();
        cache.TryGet("b.txt", 10, 100, out _).Should().BeFalse();
    }

    [Fact]
    public void Cache_EvictsOldestEntriesAtCapacity()
    {
        var cache = new ContentHashCache(capacity: 2);

        cache.Set("a", 1, 1, "a");
        cache.Set("b", 1, 1, "b");
        cache.Set("c", 1, 1, "c");

        cache.Count.Should().Be(2);
        cache.TryGet("a", 1, 1, out _).Should().BeFalse();
        cache.TryGet("b", 1, 1, out _).Should().BeTrue();
        cache.TryGet("c", 1, 1, out _).Should().BeTrue();
    }
}
