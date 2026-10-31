using MAB.DotIgnore;
using PdSync.Config;

namespace PdSync.Scan;

/// <summary>One effective rule after the ignore file and inline excludes are merged.</summary>
public sealed record IgnoreRuleInfo(
    int Number,
    string Pattern,
    string Source,
    int? LineNumber);

/// <summary>
/// The verdict for one relative path. <see cref="MatchedPath"/> can differ from
/// <see cref="Path"/> when an ignored parent directory is the deciding rule.
/// </summary>
public sealed record IgnoreDecision(
    string Path,
    bool IsIgnored,
    string? Pattern,
    int? RuleNumber,
    string? Source,
    string? MatchedPath);

public sealed class IgnoreRulesException : Exception
{
    public string SourceLabel { get; }
    public int? LineNumber { get; }
    public string? Pattern { get; }

    public IgnoreRulesException(
        string source,
        int? lineNumber,
        string? pattern,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SourceLabel = source;
        LineNumber = lineNumber;
        Pattern = pattern;
    }
}

/// <summary>
/// gitignore-style rules for one sync root (SPEC §3.3 / D7/D12/D17). One file,
/// then inline <c>exclude</c> patterns appended after it so later rules win.
/// Matching is case-sensitive unless the sync opts into <c>ignoreCase</c>.
/// </summary>
public sealed class IgnoreRules
{
    private readonly IgnoreList _list;
    private readonly IReadOnlyList<IgnoreRuleInfo> _rules;

    private IgnoreRules(string filePath, IgnoreList list, IReadOnlyList<IgnoreRuleInfo> rules)
    {
        FilePath = filePath;
        _list = list;
        _rules = rules;
    }

    public string FilePath { get; }

    public IReadOnlyList<IgnoreRuleInfo> Rules => _rules;

    public bool IsEmpty => _rules.Count == 0;

    public static IgnoreRules Load(SyncConfig sync) =>
        Load(sync.IgnoreFile, sync.Exclude, sync.Options.IgnoreCase);

    public static IgnoreRules Load(
        string ignoreFilePath,
        IReadOnlyList<string>? inlinePatterns = null,
        IgnoreCasePolicy casePolicy = IgnoreCasePolicy.Sensitive)
    {
        if (string.IsNullOrWhiteSpace(ignoreFilePath))
            throw new ArgumentException("ignore file path is empty", nameof(ignoreFilePath));

        var flags = casePolicy == IgnoreCasePolicy.Insensitive
            ? MatchFlags.CASEFOLD
            : MatchFlags.NONE;

        var list = new IgnoreList();
        var rules = new List<IgnoreRuleInfo>();
        var number = 0;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(ignoreFilePath);
        }
        catch (FileNotFoundException)
        {
            lines = Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            lines = Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IgnoreRulesException(
                ignoreFilePath,
                null,
                null,
                $"cannot read ignore file: {ex.Message}",
                ex);
        }

        if (lines.Length > 0 && lines[0].Length > 0 && lines[0][0] == '\uFEFF')
            lines[0] = lines[0][1..];

        for (var i = 0; i < lines.Length; i++)
        {
            var pattern = lines[i];
            if (IsCommentOrBlank(pattern))
                continue;

            Add(list, rules, pattern, $"{ignoreFilePath}:{i + 1}", i + 1, flags, ref number);
        }

        if (inlinePatterns is not null)
        {
            for (var i = 0; i < inlinePatterns.Count; i++)
            {
                var pattern = inlinePatterns[i];
                var source = $"exclude[{i}]";
                if (string.IsNullOrEmpty(pattern))
                    throw new IgnoreRulesException(source, null, pattern, "exclude pattern is empty");
                if (IsCommentOrBlank(pattern))
                    continue;

                Add(list, rules, pattern, source, null, flags, ref number);
            }
        }

        if (list.Rules.Count != rules.Count)
        {
            throw new IgnoreRulesException(
                ignoreFilePath,
                null,
                null,
                "ignore rules could not be parsed consistently; fix the pattern list");
        }

        return new IgnoreRules(ignoreFilePath, list, rules);
    }

    /// <summary>
    /// Matches a sync-root-relative POSIX path. The root itself (empty path) is
    /// never ignored. The path may use either slash style for CLI convenience.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var path = NormalizeRelativePath(relativePath);
        return path.Length != 0 && _list.IsIgnored(path, isDirectory);
    }

    /// <summary>
    /// Matches an already-canonical scanner path. Backslashes are literal name
    /// characters here (legal on Unix); the public overload accepts native
    /// separators for CLI input.
    /// </summary>
    internal bool IsIgnoredNormalized(string canonicalRelativePath, bool isDirectory) =>
        canonicalRelativePath.Length != 0 && _list.IsIgnored(canonicalRelativePath, isDirectory);

    /// <summary>
    /// Explains the deciding rule. Mirrors MAB's parent-directory cascade: a
    /// child cannot be re-included while an ancestor directory is still ignored
    /// (the same rule as git). No match means the path is included.
    /// </summary>
    public IgnoreDecision Decide(string relativePath, bool isDirectory)
    {
        var path = NormalizeRelativePath(relativePath);
        if (path.Length == 0)
            return new IgnoreDecision(string.Empty, false, null, null, null, null);

        var finalVerdict = _list.IsIgnored(path, isDirectory);
        var segments = path.Split('/');

        // The verdict comes from MAB; the explanation is the last rule of the
        // matching polarity that touched the path or one of its parents. This
        // keeps Explain() in lockstep with matching for directory negations and
        // the "ignored parent wins" cascade.
        var decision = finalVerdict
            ? FindRule(path, segments, isDirectory, negated: false, ignored: true)
            : FindRule(path, segments, isDirectory, negated: true, ignored: false);

        return decision ?? new IgnoreDecision(path, finalVerdict, null, null, null, path);
    }

    private IgnoreDecision? FindRule(
        string path,
        string[] segments,
        bool isDirectory,
        bool negated,
        bool ignored)
    {
        for (var i = _list.Rules.Count - 1; i >= 0; i--)
        {
            var rule = _list.Rules[i];
            var isNegation = (rule.PatternFlags & PatternFlags.NEGATION) != 0;
            if (isNegation != negated)
                continue;

            string? matchedPath = null;
            if (rule.IsMatch(path, isDirectory))
            {
                matchedPath = path;
            }
            else
            {
                for (var depth = 1; depth < segments.Length; depth++)
                {
                    var ancestor = string.Join('/', segments, 0, depth);
                    if (rule.IsMatch(ancestor, true))
                    {
                        matchedPath = ancestor;
                        break;
                    }
                }
            }

            if (matchedPath is null)
                continue;

            var info = _rules[i];
            return new IgnoreDecision(
                path,
                ignored,
                info.Pattern,
                info.Number,
                info.Source,
                matchedPath);
        }

        return null;
    }

    /// <summary>
    /// Canonical relative display path: NFC, POSIX separators, no leading slash,
    /// no empty/'.' segments. '..' is rejected so callers cannot escape the root.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;

        var text = relativePath.Replace('\\', '/');
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            throw new ArgumentException("path must be relative to the sync root", nameof(relativePath));
        if (text.StartsWith("//", StringComparison.Ordinal) ||
            text.StartsWith("~/", StringComparison.Ordinal))
            throw new ArgumentException("path must be relative to the sync root", nameof(relativePath));

        var segments = new List<string>();
        foreach (var segment in text.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                throw new ArgumentException("path must not contain '..'", nameof(relativePath));

            segments.Add(segment);
        }

        return PathKey.Nfc(string.Join('/', segments));
    }

    private static bool IsCommentOrBlank(string pattern) =>
        string.IsNullOrWhiteSpace(pattern) || pattern[0] == '#';

    private static void Add(
        IgnoreList list,
        List<IgnoreRuleInfo> rules,
        string pattern,
        string source,
        int? lineNumber,
        MatchFlags flags,
        ref int number)
    {
        try
        {
            list.AddRule(pattern, flags);
        }
        catch (Exception ex)
        {
            throw new IgnoreRulesException(
                source,
                lineNumber,
                pattern,
                $"{source}: invalid ignore pattern '{pattern}': {ex.Message}",
                ex);
        }

        number++;
        rules.Add(new IgnoreRuleInfo(number, pattern, source, lineNumber));
    }
}
