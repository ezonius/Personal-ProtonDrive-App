namespace PdSync.Remote;

/// <summary>
/// Name grammar for remote temporary nodes created by the staged-upload path.
/// A staged upload is only safe to clean up when its leaf matches this exact
/// grammar; a stale <c>pdsync-upload-&lt;12 hex&gt;</c> node can otherwise be
/// mistaken for user content (SPEC §6.2, §6.4).
/// </summary>
public static class StagedUpload
{
    public const string Prefix = "pdsync-upload-";
    public const int HexLength = 12;

    /// <summary>Creates a fresh staging name using 12 lowercase hex characters.</summary>
    public static string NewName() =>
        Prefix + Guid.NewGuid().ToString("N")[..HexLength];

    /// <summary>
    /// True only for an exact <c>pdsync-upload-</c> prefix followed by 12
    /// lowercase ASCII hex characters. Case, length, and extension matter.
    /// </summary>
    public static bool IsStagingLeaf(string? leaf)
    {
        if (leaf is null || leaf.Length != Prefix.Length + HexLength)
            return false;
        if (!leaf.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        for (var i = Prefix.Length; i < leaf.Length; i++)
        {
            var c = leaf[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }

        return true;
    }
}
