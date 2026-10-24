// tests/Ecr.Infrastructure.Tests/Jobs/OutboxFlushJobTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>✎ D-451: разове «відправити зараз» шле лише листи аркуша, а не всю чергу.</summary>
public sealed class OutboxFlushJobTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public void Відправляє_лише_коди_листів_аркуша()
    {
        // ⛔ Мутація: `FlushAsync(null)` (уся черга) — при недоступній пошті кожне подання витрачало б спробу чужих
        // подій (J1-01), і зведення збоїв ставали б `Failed` за хвилини. Тут — рівно два коди листів аркуша.
        Assert.Equal(
            [SheetWorkflowNotifier.SubmittedEventCode, SheetWorkflowNotifier.ReturnedEventCode],
            OutboxFlushJob.EventCodes);
        Assert.True(typeof(IOutboxFlushJob).IsAssignableFrom(typeof(OutboxFlushJob)));
    }
}
