using FluentAssertions;
using PdSync.Exec;
using PdSync.Plan;

namespace PdSync.Tests;

public sealed class ExecutorTests
{
    [Fact]
    public void ExecutionReport_CountsByOutcomeStatus()
    {
        var action = new PlanAction(PlanActionKind.DropBase, "a.txt", "a.txt");
        var report = new ExecutionReport(
            new[]
            {
                new ActionOutcome(action, true),
                new ActionOutcome(action, false, "boom"),
                new ActionOutcome(action, false, "skipped: earlier failure", Outcome: "skipped"),
            },
            Array.Empty<PlanAdoption>(),
            Array.Empty<string>());

        report.Succeeded.Should().Be(1);
        report.Failed.Should().Be(1);
        report.Skipped.Should().Be(1);
    }
}
