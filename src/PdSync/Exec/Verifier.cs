using PdSync.Config;
using PdSync.Proton;
using PdSync.Scan;

namespace PdSync.Exec;

/// <summary>Result detail for one upload/download verification, included in the run log.</summary>
public sealed record VerificationSummary(
    string Method,
    string Result,
    bool DownloadBack = false,
    string? Detail = null);

/// <summary>A failure raised by verification; carries the summary for the JSONL run log.</summary>
public sealed class VerificationException : SyncException
{
    public VerificationException(VerificationSummary verification)
        : base(verification.Detail ?? "verification failed")
    {
        Verification = verification;
    }

    public VerificationSummary Verification { get; }
}

/// <summary>
/// Verification for uploads and downloads (SPEC §6.5). Digest claims are used
/// as fingerprints, never as a security boundary. A missing digest downgrades
/// to size, per safety rule 15.
/// </summary>
public sealed class Verifier
{
    private readonly ProtonCli _cli;
    private readonly VerifyOptions _options;

    public Verifier(ProtonCli cli, VerifyOptions options)
    {
        _cli = cli;
        _options = options;
    }

    public (ProtonNode Node, VerificationSummary Verification) VerifyUpload(
        string parentRemote,
        string expectedName,
        string localPath)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists)
            throw new SyncException($"uploaded local file disappeared: {localPath}");

        var nodes = _cli.List(parentRemote);
        var node = nodes.FirstOrDefault(n =>
            string.Equals(n.Name, expectedName, StringComparison.Ordinal));
        if (node is null)
        {
            throw Fail("size",
                $"verification failed: '{expectedName}' was not found in the remote parent after upload");
        }

        if (node.Size != info.Length)
        {
            throw Fail("size",
                $"verification failed for '{expectedName}': remote size {node.Size} != local size {info.Length}");
        }

        if (_options.Mode == VerifyMode.Size)
            return (node, new VerificationSummary("size", "ok"));

        var localSha1 = ContentHash.ComputeSha1(localPath);
        var method = string.IsNullOrEmpty(node.Sha1) ? "size" : "size+sha1";
        if (!string.IsNullOrEmpty(node.Sha1) &&
            !string.Equals(localSha1, node.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(method,
                $"verification failed for '{expectedName}': local SHA-1 {localSha1} != remote claim {node.Sha1}");
        }

        // Sample mode only covers uploads whose remote digest is missing: those are
        // the transfers the listing cannot verify. `minSizeMb` bounds a partial
        // sample; at 100% every such upload is audited regardless of size.
        var audit = _options.Mode == VerifyMode.Full ||
                    (_options.Mode == VerifyMode.Sample &&
                     string.IsNullOrEmpty(node.Sha1) &&
                     (_options.SamplePercent >= 100 ||
                      info.Length >= _options.MinSizeMb * 1024L * 1024L) &&
                     IsSampled(expectedName, localSha1, _options.SamplePercent));

        if (audit)
        {
            method += "+download-back";
            try
            {
                DownloadBackAndCompare(parentRemote, expectedName, info.Length, localSha1);
            }
            catch (SyncException ex)
            {
                throw Fail(method, ex.Message, downloadBack: true);
            }
        }

        return (node, new VerificationSummary(method, "ok", DownloadBack: audit));
    }

    private void DownloadBackAndCompare(
        string parentRemote,
        string expectedName,
        long expectedSize,
        string expectedSha1)
    {
        var staging = Path.Combine(Path.GetTempPath(), "pdsync-verify-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(staging);
        try
        {
            _cli.Download(parentRemote.TrimEnd('/') + "/" + expectedName, staging);
            var downloaded = Path.Combine(staging, expectedName);
            if (!File.Exists(downloaded))
            {
                var files = Directory.GetFiles(staging);
                downloaded = files.Length == 1
                    ? files[0]
                    : throw new SyncException($"download-back verification failed for '{expectedName}'");
            }

            var info = new FileInfo(downloaded);
            if (info.Length != expectedSize)
            {
                throw new SyncException(
                    $"download-back verification failed for '{expectedName}': size {info.Length} != {expectedSize}");
            }

            var actual = ContentHash.ComputeSha1(downloaded);
            if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncException(
                    $"download-back verification failed for '{expectedName}': SHA-1 {actual} != {expectedSha1}");
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    private static bool IsSampled(string name, string sha1, int percent)
    {
        if (percent <= 0)
            return false;
        if (percent >= 100)
            return true;

        var hash = ContentHash.ComputeSha1(System.Text.Encoding.UTF8.GetBytes(name + "\n" + sha1));
        var bucket = uint.Parse(hash[..8], System.Globalization.NumberStyles.HexNumber);
        return bucket % 100 < (uint)percent;
    }

    public VerificationSummary VerifyDownload(RemoteEntry remote, string downloadedPath)
    {
        var info = new FileInfo(downloadedPath);
        if (!info.Exists)
            throw Fail("size", $"download did not produce a file: {downloadedPath}");
        if (info.Length != remote.Size)
        {
            throw Fail("size",
                $"verification failed for '{remote.Path}': downloaded size {info.Length} != remote size {remote.Size}");
        }

        if (string.IsNullOrEmpty(remote.Sha1))
            return new VerificationSummary("size", "ok");

        var localSha1 = ContentHash.ComputeSha1(downloadedPath);
        if (!string.Equals(localSha1, remote.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail("size+sha1",
                $"verification failed for '{remote.Path}': downloaded SHA-1 {localSha1} != remote claim {remote.Sha1}");
        }

        return new VerificationSummary("size+sha1", "ok");
    }

    private static VerificationException Fail(
        string method,
        string message,
        bool downloadBack = false) =>
        new(new VerificationSummary(method, "failed", downloadBack, message));
}
