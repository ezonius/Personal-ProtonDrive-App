using System.Text;

namespace PdSync.Config;

/// <summary>
/// Path identity: NFC-normalized, case-folded. pdsync never syncs case (SPEC
/// D14), and macOS may hand us NFD names, so every folded key goes through here.
/// Relative paths use POSIX separators and have no leading slash; the sync root
/// itself is the empty string.
/// </summary>
public static class PathKey
{
    /// <summary>Canonical display form: Unicode NFC. Case is preserved.</summary>
    public static string Nfc(string value) => value.Normalize(NormalizationForm.FormC);

    /// <summary>Identity form: NFC-normalized, case-folded.</summary>
    public static string Fold(string value) => Nfc(value).ToLowerInvariant();
}
