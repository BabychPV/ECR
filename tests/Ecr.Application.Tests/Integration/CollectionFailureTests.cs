// tests/Ecr.Application.Tests/Integration/CollectionFailureTests.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Розпізнавання відмови в автентифікації (<c>H-20</c>) у ДВОХ форматах
/// <c>itg.CollectionRun.ErrorMessage</c>: конверт із U12 і речення до нього.
/// </summary>
/// <remarks>
/// ⛔ Старі рядки лишаються в базі: розпізнавання лише конверта мовчки злило б
/// їх зі звичайним збоєм збору у зведенні. Мутація «прибрати гілку
/// AuthenticationMarker» → червоний <see cref="Старе_речення_розпізнається"/>;
/// мутація «прибрати гілку конверта» → червоний <see cref="Конверт_розпізнається_за_ключем"/>.
/// </remarks>
public sealed class CollectionFailureTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "U12")]
    public void Конверт_розпізнається_за_ключем()
    {
        var reason = CollectionFailure.AuthenticationRefusedReason("STACK-1", "401 Unauthorized");

        Assert.True(JobProgressMessageCodec.TryDecode(reason, out var envelope));
        Assert.Equal(CollectionFailure.AuthenticationRefusedKey, envelope.Key);
        Assert.Equal("STACK-1", envelope.Params!["sourceCode"]);
        Assert.Equal("401 Unauthorized", envelope.Params["detail"]);

        // Конверт не несе українського маркера — розпізнається саме ключем.
        Assert.DoesNotContain(CollectionFailure.AuthenticationMarker, reason, StringComparison.Ordinal);
        Assert.True(CollectionFailure.IsAuthenticationRefusal(reason));

        // І всередині рамки «код: причина» теж.
        var wrapped = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            "jobs.collectionRunReason",
            new Dictionary<string, string> { ["code"] = "ECR-INT-0502" },
            new JobProgressMessageEnvelope(CollectionFailure.AuthenticationRefusedKey)));
        Assert.True(CollectionFailure.IsAuthenticationRefusal(wrapped));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "U12")]
    public void Старе_речення_розпізнається()
        => Assert.True(CollectionFailure.IsAuthenticationRefusal(CollectionFailure.AuthenticationRefused("STACK-1")));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "U12")]
    public void Інше_не_розпізнається()
    {
        Assert.False(CollectionFailure.IsAuthenticationRefusal(null));
        Assert.False(CollectionFailure.IsAuthenticationRefusal("ECR-INT-0503: джерело недоступне"));
        Assert.False(CollectionFailure.IsAuthenticationRefusal(JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.collectionRunReason",
                new Dictionary<string, string> { ["code"] = "ECR-INT-0503" },
                new JobProgressMessageEnvelope("jobs.collectionSourceUnavailable")))));

        // Маркер у ПАРАМЕТРІ конверта (текст джерела) — не ознака: конверт
        // розпізнається лише ключем.
        Assert.False(CollectionFailure.IsAuthenticationRefusal(JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.collectionSourceError",
                new Dictionary<string, string> { ["detail"] = CollectionFailure.AuthenticationMarker }))));
    }
}
