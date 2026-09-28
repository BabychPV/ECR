// tests/Ecr.Domain.Tests/External/CollectionCoverageRegistryTests.cs
using Ecr.Domain.Entities.Integration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Рядок журналу покриття для події синку довідника
/// (<see cref="CollectionCoverage.SkippedRegistry"/>, FEATURE-REGISTRY-SYNC S5).
/// </summary>
public sealed class CollectionCoverageRegistryTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Подія_синку_без_періоду_і_без_прогону()
    {
        var row = CollectionCoverage.SkippedRegistry(7, CollectionCoverage.RegistrySourceMissing, "element=g2", Now);

        // ⛔ Довідник не живе за періодами: вигаданий період зробив би подію
        // видимою у фільтрі періоду, до якого вона не має стосунку.
        Assert.Null(row.PeriodKey);
        Assert.Null(row.CollectionRunId);
        Assert.Equal(CollectionCoverage.RegistrySourceMissing, row.Status);
        Assert.Equal(Now, row.CoveredFrom);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Статус_матеріалізації_через_фабрику_синку_відхиляється()
    {
        // ⛔ «Ручне значення в комірці» від синку довідника було б неправдою в журналі.
        Assert.Throws<ArgumentException>(
            () => CollectionCoverage.SkippedRegistry(7, CollectionCoverage.ConflictKeptManual, "x", Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Пояснення_обрізається_до_межі_стовпця()
    {
        var row = CollectionCoverage.SkippedRegistry(
            7, CollectionCoverage.RegistryDiverged, new string('x', CollectionCoverage.MaxDetailsLength + 50), Now);

        Assert.Equal(CollectionCoverage.MaxDetailsLength, row.Details!.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-8.11")]
    public void Статуси_синку_входять_у_перелік_фільтра_журналу()
    {
        // Статус поза KnownStatuses фільтр журналу відхиляє 422 — подія була б невидимою.
        Assert.All(CollectionCoverage.RegistryStatuses, s => Assert.Contains(s, CollectionCoverage.KnownStatuses));
    }
}
