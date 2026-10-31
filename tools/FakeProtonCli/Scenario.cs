using System.Text.Json;

namespace FakeProtonCli;

public sealed class Scenario
{
    public int LatencyMs { get; set; }
    public List<ScenarioRule> Rules { get; set; } = new();

    public static Scenario? Load(string? explicitPath, string stateDir)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var candidate = Path.Combine(stateDir, "scenario.json");
            if (File.Exists(candidate))
                path = candidate;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), FakeJson.Pretty);
    }
}

public sealed class ScenarioRule
{
    public string Match { get; set; } = "";
    public int Times { get; set; } // 0 = unlimited
    public int DelayMs { get; set; }
    public int ExitCode { get; set; }
    public string? Stderr { get; set; }
    public bool AuthExpired { get; set; }
    public bool TruncateDownload { get; set; }
}

public sealed class ScenarioOutcome
{
    public int LatencyMs { get; set; }
    public int ExitCode { get; set; }
    public string? Stderr { get; set; }
    public bool AuthExpired { get; set; }
    public bool TruncateDownload { get; set; }
}

public static class ScenarioEngine
{
    public static ScenarioOutcome Evaluate(Scenario? scenario, IReadOnlyList<string> args, FakeState state)
    {
        var outcome = new ScenarioOutcome { LatencyMs = scenario?.LatencyMs ?? 0 };
        if (scenario is null || scenario.Rules.Count == 0)
            return outcome;

        var joined = string.Join(' ', args);
        for (var i = 0; i < scenario.Rules.Count; i++)
        {
            var rule = scenario.Rules[i];
            if (!Matches(rule.Match, joined))
                continue;

            var key = i.ToString();
            var hits = state.RuleHits.TryGetValue(key, out var h) ? h : 0;
            if (rule.Times > 0 && hits >= rule.Times)
                continue;

            state.RuleHits[key] = hits + 1;
            outcome.LatencyMs += rule.DelayMs;
            outcome.AuthExpired |= rule.AuthExpired;
            outcome.TruncateDownload |= rule.TruncateDownload;
            if (rule.ExitCode != 0)
            {
                outcome.ExitCode = rule.ExitCode;
                outcome.Stderr = rule.Stderr;
            }
            break; // first matching rule wins
        }

        return outcome;
    }

    private static bool Matches(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
