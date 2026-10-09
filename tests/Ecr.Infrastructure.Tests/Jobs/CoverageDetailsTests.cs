// tests/Ecr.Infrastructure.Tests/Jobs/CoverageDetailsTests.cs
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>CollectionCoverage.Details</c> від задач — конверт із ключем <c>coverageEvents.*</c>, а не
/// готова українська фраза (борг 2026-10-01).
/// </summary>
public sealed class CoverageDetailsTests
{
    [Fact]
    public void Details_have_envelope_shape_with_key_and_parameters()
    {
        var raw = CoverageDetails.PointCeiling("FieldA", 5000);

        Assert.True(JobProgressMessageCodec.TryDecode(raw, out var envelope));
        Assert.Equal("coverageEvents.pointCeiling", envelope.Key);
        Assert.Equal("FieldA", envelope.Params!["field"]);
        Assert.Equal("5000", envelope.Params["limit"]);
    }

    [Fact]
    public void Period_state_is_a_parameter_and_unknown_state_is_its_own_key()
    {
        Assert.True(JobProgressMessageCodec.TryDecode(CoverageDetails.PeriodNotOpen("Closed"), out var closed));
        Assert.Equal("coverageEvents.periodClosed", closed.Key);
        Assert.Equal("Closed", closed.Params!["state"]);

        Assert.True(JobProgressMessageCodec.TryDecode(CoverageDetails.PeriodNotOpen(null), out var missing));
        Assert.Equal("coverageEvents.periodMissing", missing.Key);
    }

    /// <summary>
    /// D2-02: частка покриття — параметр рядка, обрізана вниз до двох знаків (94.999 не має
    /// показуватися як 95, що виглядало б як «поріг досягнуто»).
    /// </summary>
    [Fact]
    public void Partial_coverage_and_no_data_have_their_own_keys_and_percent_is_truncated_not_rounded_up()
    {
        Assert.True(JobProgressMessageCodec.TryDecode(CoverageDetails.PartialCoverage("F", 7, 94.999m, 95m), out var partial));
        Assert.Equal("coverageEvents.partialCoverage", partial.Key);
        Assert.Equal("F", partial.Params!["field"]);
        Assert.Equal("7", partial.Params["mapId"]);
        Assert.Equal("94.99", partial.Params["percentGood"]);
        Assert.Equal("95", partial.Params["min"]);

        Assert.True(JobProgressMessageCodec.TryDecode(CoverageDetails.NoData("F", 7), out var noData));
        Assert.Equal("coverageEvents.noData", noData.Key);
        Assert.Equal("7", noData.Params!["mapId"]);
    }

    [Fact]
    public void Long_failure_reason_is_shortened_before_encoding_so_the_json_stays_valid()
    {
        var raw = CoverageDetails.EventWriteFailed(42, new string('я', 5000));

        Assert.True(raw.Length <= CollectionCoverage.MaxDetailsLength, "обрізаний JSON не розбирається");
        Assert.True(JobProgressMessageCodec.TryDecode(raw, out var envelope));
        Assert.Equal("coverageEvents.eventWriteFailed", envelope.Key);
        Assert.Equal("42", envelope.Params!["eventId"]);
    }

    /// <summary>
    /// Сторож виклику, а не регулярка по кирилиці в усьому файлі: кожен <c>new CoverageEvent(…)</c> і
    /// <c>coverage.RecordAsync(…)</c> у цих двох задачах мусить брати подробиці з
    /// <see cref="CoverageDetails"/>, а не з літерала.
    ///
    /// ⚠ Крихкість свідома й вузька: дивимось лише на текст ОДНОГО виразу (до першої крапки з комою),
    /// тож коментарі навколо не зачіпаються. МУТАЦІЙНИЙ ДОКАЗ: у виклику замінити
    /// <c>CoverageDetails.X(…)</c> літералом <c>$"Комірка {cell}…"</c> — тест червоний.
    /// </summary>
    [Theory]
    [InlineData("MaterializeCollectedDataJob.cs", 7)]
    [InlineData("SourceEventSyncJob.cs", 12)]
    public void Coverage_events_in_jobs_take_details_from_the_envelope_helper(string file, int expectedCalls)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ecr.Infrastructure", "Jobs", file));

        var statements = Regex.Matches(text, @"(new CoverageEvent\(|coverage\s*\.RecordAsync\()")
            .Select(m =>
            {
                var rest = text[m.Index..];
                return rest[..rest.IndexOf(';', StringComparison.Ordinal)];
            })
            .ToList();

        Assert.Equal(expectedCalls, statements.Count);

        foreach (var statement in statements)
        {
            Assert.Contains("CoverageDetails.", statement, StringComparison.Ordinal);
            Assert.DoesNotMatch("[А-Яа-яІіЇїЄєҐґ]", statement);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Ecr.sln не знайдено вгору від каталогу збірки.");
    }
}
