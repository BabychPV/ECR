// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncOptionsTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-464: налаштування синку подій — стеля сторінок (<c>Collection:MaxEventPages</c>), нарізка читання
/// (<c>PiSqlClient:EventQueryChunk</c>) і самі шматки вікна.
/// </summary>
public sealed class SourceEventSyncOptionsTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    [InlineData(null, null, 10, SourceEventQueryChunk.None)]
    [InlineData("25", "Month", 25, SourceEventQueryChunk.Month)]
    [InlineData("25", "month", 25, SourceEventQueryChunk.Month)]
    [InlineData("0", "None", 10, SourceEventQueryChunk.None)]
    [InlineData("x", "Mnth", 10, SourceEventQueryChunk.None)]
    public void З_конфігурації(string? pages, string? chunk, int expectedPages, SourceEventQueryChunk expectedChunk)
    {
        var values = new Dictionary<string, string?>
        {
            [SourceEventSyncOptions.MaxEventPagesKey] = pages,
            [SourceEventSyncOptions.EventQueryChunkKey] = chunk,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(new SourceEventSyncOptions(expectedPages, expectedChunk), SourceEventSyncOptions.From(configuration));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Помісячні_шматки_покривають_вікно_без_дірок_і_перекриттів()
    {
        var from = new DateTime(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);

        var chunks = SourceEventSyncJob.Chunks(from, to, SourceEventQueryChunk.Month).ToList();

        Assert.Equal(
            [
                (from, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
                (new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
                (new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), to),
            ],
            chunks);
        Assert.Equal([(from, to)], SourceEventSyncJob.Chunks(from, to, SourceEventQueryChunk.None));
    }
}
