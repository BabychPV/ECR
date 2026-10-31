// tests/Ecr.Application.Tests/Integration/SourceEventSyncOutcomeTests.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// D-464: блок ліміту видалення синку подій читається з СИРОГО конверта підсумку (<c>JobStatus.eventRemovalBlocked</c>):
/// лише для <c>Succeeded</c>, лише ключ підсумку синку, лише коли видалення справді заблоковано.
/// </summary>
public sealed class SourceEventSyncOutcomeTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-464")]
    public void Заблоковане_видалення_дає_лічильники_інакше_null()
    {
        var blocked = Done(("removalBlocked", "2"), ("removalCount", "57"), ("removalLimit", "30"), ("removalLinked", "150"));

        Assert.Equal(new SourceEventRemovalBlock(2, 57, 30, 150), SourceEventSyncOutcome.RemovalBlockOf("Succeeded", blocked));

        // Не завершена, не заблоковано, інший ключ, старий конверт без параметра, не конверт — null.
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Running", blocked));
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Succeeded", Done(("removalBlocked", "0"))));
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Succeeded", Done(("created", "1"))));
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf(
            "Succeeded",
            JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                "jobs.formulaRecalcDone", new Dictionary<string, string> { ["removalBlocked"] = "1" }))));
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Succeeded", "Synchronization done"));
    }

    private static string Done(params (string Name, string Value)[] parameters)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            SourceEventSyncOutcome.DoneKey, parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)));
}
