// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeListProjectionTests.cs
using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Переліки типів і дозволів не читають текст великої чернетки (аудит, прохід A повторний, PA2-S-01): раніше кожен запит
/// переліку завантажував і розбирав <c>DraftJson</c> усіх типів, вартість росла з кількістю типів із чернеткою до 2 Mі символів.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>PermitTypeStore.ListSummariesAsync</c> віддати <c>t.DraftJson</c> без умови на розмір - червоніє
/// перший тест (великий текст у відповіді БД) і другий (великий текст розібрано, а не опубліковане зведення).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeListProjectionTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_типів_і_перелік_дозволів_не_вибирають_текст_великої_чернетки()
    {
        var recorder = new SqlTextRecorder();
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData, null, recorder);
        var (published, small) = await SeedAsync(h);
        recorder.Clear();

        await using var list = await h.NextRequestAsync();
        var page = await list.List.HandleAsync(new CursorRequest(500), null, null, _tag, default);
        await using var permits = await h.NextRequestAsync();
        _ = await permits.ListPermits.HandleAsync(new CursorRequest(50), published, null, null, null, default);

        // Відповідь у складі: чернетка до межі - зведення з чернетки; більша за межу - опубліковане зведення (3 і 1 стовпець).
        var big = Assert.Single(page.Items, i => i.Code == published);
        Assert.True(big.HasUnpublishedChanges);
        Assert.Equal([("Limits", 3), ("Places", 1)], big.Tables.OrderBy(t => t.Code).Select(t => (t.Code, t.ColumnCount)));
        var little = Assert.Single(page.Items, i => i.Code == small);
        Assert.Equal([("T", 2)], little.Tables.Select(t => (t.Code, t.ColumnCount)));

        // Текст SQL: кожна згадка DraftJson - перевірка на NULL, DATALENGTH або значення під умовою на розмір.
        var typeQueries = recorder.Commands.Where(c => c.Contains("PermitTypeDef", StringComparison.Ordinal)
                                                       && c.Contains("DraftJson", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(typeQueries);
        foreach (var query in typeQueries)
        {
            output.WriteLine(query);
            var mentions = Regex.Count(query, @"\[DraftJson\]");
            var guarded = Regex.Count(query, @"DATALENGTH\(\[\w+\]\.\[DraftJson\]\)")
                          + Regex.Count(query, @"\[\w+\]\.\[DraftJson\] IS (NOT )?NULL")
                          + Regex.Count(query, @"THEN \[\w+\]\.\[DraftJson\]");
            Assert.True(mentions == guarded && query.Contains("DATALENGTH", StringComparison.Ordinal), query);
            Assert.Matches(@"DATALENGTH\(\[\w+\]\.\[DraftJson\]\) AS int\) <= \d+\s+THEN \[\w+\]\.\[DraftJson\]", query);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимір_перелік_із_одним_і_сімома_типами_з_максимальною_чернеткою_нова_й_стара_вибірка()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var codes = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var code = $"M{i}{_tag}";
            await SaveBigDraftAsync(h, code);
            codes.Add(code);
        }

        foreach (var count in new[] { 1, 7 })
        {
            var mine = codes.Take(count).ToHashSet(StringComparer.Ordinal);

            await using var fresh = await h.NextRequestAsync();
            var watch = Stopwatch.StartNew();
            var page = await fresh.List.HandleAsync(new CursorRequest(500), null, null, _tag, default);
            var newMs = watch.ElapsedMilliseconds;

            // Стара вибірка для порівняння: уся сутність із текстом і розбір кожної чернетки (як було до PA2-S-01).
            await using var old = await h.NextRequestAsync();
            watch.Restart();
            var entities = await old.Db.PermitTypeDefs.AsNoTracking().Where(t => t.Code.EndsWith(_tag)).OrderBy(t => t.Code).Take(count).ToListAsync();
            var parsed = entities.Sum(t => PermitTypeCodec.Parse(t.DraftJson!).Tables.Count);
            var oldMs = watch.ElapsedMilliseconds;

            output.WriteLine($"типів з максимальною чернеткою: {count}; перелік (проєкція) {newMs} мс; стара вибірка+розбір {oldMs} мс (таблиць {parsed})");
            Assert.True(page.Items.Count(i => i.Code.EndsWith(_tag, StringComparison.Ordinal)) >= count);
            Assert.All(page.Items.Where(i => mine.Contains(i.Code)), i => Assert.Empty(i.Tables));
        }
    }

    // Тип з опублікованим складом і новою версією, чернетка якої більша за межу зведення; і малий тип.
    private async Task<(string Published, string Small)> SeedAsync(PermitHarness h)
    {
        var published = $"PB{_tag}";
        var small = $"SM{_tag}";

        await PermitFixtures.PublishTypeAsync(h, published);
        await using (var open = await h.NextRequestAsync())
        {
            var draft = await open.OpenVersion.HandleAsync(published, default);
            var limits = draft.Tables.Single(t => t.Code == "Limits");
            var inflated = limits with { Columns = [.. limits.Columns, BigListColumn("L", limits.Columns.Count + 1)] };
            await using var put = await h.NextRequestAsync();
            await put.Replace.HandleAsync(
                published,
                new ReplacePermitTypeRequest(draft.RowVersion, draft.NameL10n, "Air", [inflated, .. draft.Tables.Where(t => t.Code != "Limits")], []),
                default);
        }

        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(small, PermitFixtures.Name(small), "Air"), default);
        var table = new PermitTypeTableDto(
            "T", PermitFixtures.Name("T"), 1,
            [Column("K", "String", key: true, 1), Column("V", "String", key: false, 2)],
            []);
        await using var putSmall = await h.NextRequestAsync();
        await putSmall.Replace.HandleAsync(small, new ReplacePermitTypeRequest(created.RowVersion, PermitFixtures.Name(small), "Air", [table], []), default);

        return (published, small);
    }

    private static async Task SaveBigDraftAsync(PermitHarness h, string code)
    {
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(code, PermitFixtures.Name(code), "Air"), default);
        var table = new PermitTypeTableDto("T", PermitFixtures.Name("T"), 1, [Column("K", "String", key: true, 1), BigListColumn("L", 2)], []);
        await using var put = await h.NextRequestAsync();
        await put.Replace.HandleAsync(code, new ReplacePermitTypeRequest(created.RowVersion, PermitFixtures.Name(code), "Air", [table], []), default);
    }

    // 14 значень по 21 000 кириличних символів: у збереженому JSON кожен займає 6 знаків (\uXXXX) - близько 1,8 Mі символів.
    private static PermitTypeColumnDto BigListColumn(string code, int order)
    {
        var choices = Enumerable.Range(0, 14).Select(i => i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + new string('ж', 21_000)).ToList();
        return new PermitTypeColumnDto(code, PermitFixtures.Name(code), "Lookup", null, false, false, null, null, null, "TypeOnly", null, null, choices, order);
    }

    private static PermitTypeColumnDto Column(string code, string dataType, bool key, int order)
        => new(code, PermitFixtures.Name(code), dataType, null, key, key, null, null, null, null, null, null, null, order);

    private sealed class SqlTextRecorder : DbCommandInterceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _texts = new();

        public IReadOnlyList<string> Commands => [.. _texts];

        public void Clear() => _texts.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _texts.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
    }
}
