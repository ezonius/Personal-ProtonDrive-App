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
    public void Detect_ReadsLocalRootFolderPath_AndSkipsInvalidEntries()
    {
        using var temp = new TempDirectory();
        var mapped = temp.Combine("mapped");
        var path = temp.Write("Mappings.json", $$"""
            {
              "Mappings": [
                { "Local": { "RootFolderPath": "{{mapped.Replace("\\", "\\\\")}}" } },
                { "Local": { "RootFolderPath": "relative/path" } },
                { "Local": {} },
                { "Remote": {} }
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
