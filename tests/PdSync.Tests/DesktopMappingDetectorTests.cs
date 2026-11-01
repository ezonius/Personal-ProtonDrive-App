using FluentAssertions;
using PdSync.Diagnostics;

namespace PdSync.Tests;

public sealed class DesktopMappingDetectorTests
{
    [Fact]
    public void Detect_MissingFile_ReturnsNoMappingsAndNoDiagnostic()
    {
        using var temp = new TempDirectory();
        var detector = new DesktopMappingDetector(new[] { temp.Combine("missing", "Mappings.json") });

        var scan = detector.Detect();

        scan.Mappings.Should().BeEmpty();
        scan.Diagnostic.Should().BeNull();
    }

    [Fact]
    public void Detect_MalformedJson_ReturnsNoMappingsAndDiagnostic()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("Mappings.json", "{ not valid json");
        var detector = new DesktopMappingDetector(new[] { path });

        var scan = detector.Detect();

        scan.Mappings.Should().BeEmpty();
        scan.Diagnostic.Should().NotBeNull();
        scan.Diagnostic.Should().Contain(path);
    }

    [Fact]
    public void Detect_UnrecognizedShape_ReturnsNoMappingsAndDiagnostic()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("Mappings.json", """{ "SomethingElse": [] }""");
        var detector = new DesktopMappingDetector(new[] { path });

        var scan = detector.Detect();

        scan.Mappings.Should().BeEmpty();
        scan.Diagnostic.Should().Contain("Mappings array");
    }

    [Fact]
    public void Detect_AllEntriesInvalid_ReportsSchemaDriftInsteadOfNoMappings()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("Mappings.json", """
            {
              "Mappings": [
                { "Local": {} },
                { "Local": { "RootFolderPath": "relative/path" } },
                { "Remote": {} }
              ]
            }
            """);
        var detector = new DesktopMappingDetector(new[] { path });

        var scan = detector.Detect();

        scan.Mappings.Should().BeEmpty();
        scan.Diagnostic.Should().NotBeNull();
        scan.Diagnostic.Should().Contain("3 entries");
        scan.Diagnostic.Should().Contain(path);
    }

    [Fact]
    public void Detect_PartiallyInvalidEntries_ReportsWarningAndKeepsMappings()
    {
        using var temp = new TempDirectory();
        var mapped = temp.Combine("mapped");
        var path = temp.Write("Mappings.json", $$"""
            {
              "Mappings": [
                { "Local": { "RootFolderPath": "{{mapped.Replace("\\", "\\\\")}}" } },
                { "Local": {} }
              ]
            }
            """);
        var detector = new DesktopMappingDetector(new[] { path });

        var scan = detector.Detect();

        scan.Mappings.Should().ContainSingle().Which.LocalRoot.Should().Be(mapped);
        scan.Diagnostic.Should().NotBeNull();
        scan.Diagnostic.Should().Contain("1 of 2");
    }

    [Fact]
    public void Detect_ReadsLocalRootFolderPath_AndSkipsInvalidEntries()
    {
        using var temp = new TempDirectory();
        var mapped = temp.Combine("mapped");
        var path = temp.Write("Mappings.json", $$"""
            {
              "Mappings": [
                { "Local": { "RootFolderPath": "{{mapped.Replace("\\", "\\\\")}}" } }
              ]
            }
            """);
        var detector = new DesktopMappingDetector(new[] { path });

        var scan = detector.Detect();

        scan.Diagnostic.Should().BeNull();
        scan.Mappings.Should().ContainSingle()
            .Which.LocalRoot.Should().Be(mapped);
    }

    [Fact]
    public void Classify_DetectsExactOverlap()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("cloud", "ProtonDrive");

        DesktopMappingOverlapDetector.Classify(root, root)
            .Should().Be(DesktopMappingOverlapKind.Exact);
    }

    [Fact]
    public void Classify_DetectsLocalRootInsideMapping()
    {
        using var temp = new TempDirectory();
        var mapping = temp.Combine("cloud", "ProtonDrive");
        var local = Path.Combine(mapping, "My files", "docs");

        DesktopMappingOverlapDetector.Classify(local, mapping)
            .Should().Be(DesktopMappingOverlapKind.LocalRootInsideMapping);
    }

    [Fact]
    public void Classify_DetectsMappingInsideLocalRoot()
    {
        using var temp = new TempDirectory();
        var local = temp.Combine("cloud");
        var mapping = Path.Combine(local, "ProtonDrive");

        DesktopMappingOverlapDetector.Classify(local, mapping)
            .Should().Be(DesktopMappingOverlapKind.MappingInsideLocalRoot);
    }

    [Fact]
    public void Classify_DoesNotMatchSiblingWithCommonPrefix()
    {
        using var temp = new TempDirectory();
        var local = temp.Combine("cloud", "ProtonDrive");

        DesktopMappingOverlapDetector.Classify(local, local + "Backup")
            .Should().Be(DesktopMappingOverlapKind.None);
    }

    [Fact]
    public void Classify_IsCaseAndNfcInsensitive()
    {
        using var temp = new TempDirectory();
        var composed = Path.Combine(temp.Root, "Caf\u00e9");
        var decomposed = Path.Combine(temp.Root, "CAFE\u0301");

        DesktopMappingOverlapDetector.Classify(composed, decomposed)
            .Should().Be(DesktopMappingOverlapKind.Exact);
    }

    [Fact]
    public void Classify_TrailingSeparationDoesNotChangeIdentity()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("cloud");

        DesktopMappingOverlapDetector.Classify(root, root + Path.DirectorySeparatorChar)
            .Should().Be(DesktopMappingOverlapKind.Exact);
    }

    [Fact]
    public void Classify_FilesystemRootIsHandled()
    {
        using var temp = new TempDirectory();
        var root = Path.GetPathRoot(temp.Root)!;
        var child = Path.Combine(root, "pdsync-overlap-child");

        DesktopMappingOverlapDetector.Classify(root, root)
            .Should().Be(DesktopMappingOverlapKind.Exact);
        DesktopMappingOverlapDetector.Classify(child, root)
            .Should().Be(DesktopMappingOverlapKind.LocalRootInsideMapping);
        DesktopMappingOverlapDetector.Classify(root, child)
            .Should().Be(DesktopMappingOverlapKind.MappingInsideLocalRoot);
    }

    [Fact]
    public void Classify_UncShareRootTrailingSeparatorIsExact()
    {
        if (!OperatingSystem.IsWindows())
            return;

        DesktopMappingOverlapDetector.Classify(@"\\server\share", @"\\server\share\")
            .Should().Be(DesktopMappingOverlapKind.Exact);
        DesktopMappingOverlapDetector.Classify(@"\\server\share\folder", @"\\server\share")
            .Should().Be(DesktopMappingOverlapKind.LocalRootInsideMapping);
    }

    [Fact]
    public void FindAll_ReturnsEveryDistinctOverlappingMapping()
    {
        using var temp = new TempDirectory();
        var local = temp.Combine("cloud");
        var mappings = new[]
        {
            new DesktopMapping(Path.Combine(local, "a"), "m.json"),
            new DesktopMapping(Path.Combine(local, "a"), "m.json"),
            new DesktopMapping(Path.Combine(local, "b"), "m.json"),
            new DesktopMapping(temp.Combine("unrelated"), "m.json"),
        };

        var overlaps = DesktopMappingOverlapDetector.FindAll(local, mappings);

        overlaps.Should().HaveCount(2);
        overlaps.Select(overlap => overlap.Mapping.LocalRoot)
            .Should().BeEquivalentTo(new[] { Path.Combine(local, "a"), Path.Combine(local, "b") });
        overlaps.Should().OnlyContain(overlap =>
            overlap.Kind == DesktopMappingOverlapKind.MappingInsideLocalRoot);
    }

    [Fact]
    public void Find_ReturnsFirstOverlappingMapping()
    {
        var mappings = new[]
        {
            new DesktopMapping(Path.Combine(Path.GetTempPath(), "unrelated"), "m.json"),
            new DesktopMapping(Path.Combine(Path.GetTempPath(), "root"), "m.json"),
        };
        var local = Path.Combine(Path.GetTempPath(), "root", "docs");

        var overlap = DesktopMappingOverlapDetector.Find(local, mappings);

        overlap.Should().NotBeNull();
        overlap!.Kind.Should().Be(DesktopMappingOverlapKind.LocalRootInsideMapping);
        overlap.Mapping.LocalRoot.Should().Be(mappings[1].LocalRoot);
    }
}
