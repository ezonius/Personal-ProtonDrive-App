using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.State;

namespace PdSync.Cli;

/// <summary>
/// <c>pdsync history [sync...] [--limit N] [--json]</c> (SPEC §3.1, M5).
/// Read-only: it opens the state DB through <see cref="StateStore.OpenReadOnly"/>,
/// never creates or migrates it, and never takes the run lock or calls Proton.
/// Action totals come from the run row's <c>stats_json</c>; the JSONL logs are
/// not required.
/// </summary>
public static class HistoryCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record PlannedStats(
        int Considered,
        int Actions,
        int Uploads,
        int Downloads,
        int Deletions,
        int Adoptions,
        int Conflicts,
        int Skipped);

    private sealed record ParsedStats(
        PlannedStats? Planned,
        int? Succeeded,
        int? Failed,
        int? Skipped);

    private sealed record RunRow(
        long Id,
        string Status,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        PlannedStats? Planned,
        int? Succeeded,
        int? Failed,
        int? Skipped);

    private sealed record SyncRows(
        string Sync,
        string State,
        int Pending,
        IReadOnlyList<RunRow> Runs);

    public static int Execute(
        PdsyncConfig config,
        IReadOnlyList<SyncConfig> syncs,
        int limit,
        bool json,
        TextWriter output)
    {
        var syncRows = new List<SyncRows>();
        foreach (var sync in syncs)
        {
            using var state = StateStore.OpenReadOnly(config, sync);
            var runs = state.LoadRuns(limit);
            var runRows = runs.Select(ToRow).ToList();
            syncRows.Add(new SyncRows(
                sync.Name,
                state.IsInMemory ? "absent" : state.DatabasePath,
                state.LoadPending().Count,
                runRows));
        }

        if (json)
        {
            WriteJson(output, new { command = "history", limit, syncs = syncRows });
            return 0;
        }

        foreach (var sync in syncRows)
        {
            if (sync.Runs.Count == 0)
            {
                output.WriteLine($"{sync.Sync}: no runs recorded");
                continue;
            }

            output.WriteLine($"{sync.Sync}:");
            foreach (var run in sync.Runs)
                output.WriteLine("  " + Describe(run));
        }

        return 0;
    }

    private static RunRow ToRow(RunRecord run)
    {
        var stats = TryParseStats(run.StatsJson);
        return new RunRow(
            run.Id,
            run.Status,
            run.StartedAt,
            run.FinishedAt,
            stats?.Planned,
            stats?.Succeeded,
            stats?.Failed,
            stats?.Skipped);
    }

    private static string Describe(RunRow run)
    {
        var started = Format(run.StartedAt);
        var finished = run.FinishedAt is null ? "-" : Format(run.FinishedAt.Value);
        var line = $"run {run.Id}  {run.Status,-8}  {started} -> {finished}";

        if (run.Planned is not null)
        {
            line += $"  planned {run.Planned.Actions} " +
                    $"(up {run.Planned.Uploads}, down {run.Planned.Downloads}, " +
                    $"del {run.Planned.Deletions}, conflict {run.Planned.Conflicts}, " +
                    $"adopt {run.Planned.Adoptions})";
        }

        if (run.Succeeded is not null)
        {
            line += $"  ok {run.Succeeded}  fail {run.Failed ?? 0}  skip {run.Skipped ?? 0}";
        }

        return line;
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// Defensive parse of <c>stats_json</c>. Null, non-object, or malformed JSON
    /// yields <c>null</c> (the row is still shown with no summary). Property
    /// lookup is case-insensitive because the writer uses PascalCase members
    /// from <c>PlanStats</c> while the envelope is lowercase.
    /// </summary>
    private static ParsedStats? TryParseStats(string? statsJson)
    {
        if (string.IsNullOrWhiteSpace(statsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(statsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            PlannedStats? planned = null;
            if (TryGetProperty(root, "planned", out var plannedElement) &&
                plannedElement.ValueKind == JsonValueKind.Object)
            {
                planned = new PlannedStats(
                    IntValue(plannedElement, "considered"),
                    IntValue(plannedElement, "actions"),
                    IntValue(plannedElement, "uploads"),
                    IntValue(plannedElement, "downloads"),
                    IntValue(plannedElement, "deletions"),
                    IntValue(plannedElement, "adoptions"),
                    IntValue(plannedElement, "conflicts"),
                    IntValue(plannedElement, "skipped"));
            }

            return new ParsedStats(
                planned,
                OptInt(root, "succeeded"),
                OptInt(root, "failed"),
                OptInt(root, "skipped"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int IntValue(JsonElement element, string name) => OptInt(element, name) ?? 0;

    private static int? OptInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var number) ? number : null;
    }

    private static void WriteJson(TextWriter output, object value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
