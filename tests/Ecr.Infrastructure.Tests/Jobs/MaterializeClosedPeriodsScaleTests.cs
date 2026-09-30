// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeClosedPeriodsScaleTests.cs
using System.Collections.Concurrent;
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Фільтр «закритий період без сирих точок задачі не отримує» на масштабі
/// активації проєкту з багатьма минулими періодами.
/// </summary>
/// <remarks>
/// ⛔ Ліміт SQL Server — 2100 параметрів на команду. Запит, у якому число
/// параметрів росте як «сутності × періоди», падає рівно на активації великого
/// проєкту — і валить її разом із переходами.
/// <para>
/// ⚠ Проєкт тут навмисно лишається <c>Draft</c>: <c>PeriodStateJob</c> обходить
/// лише активні, тож 40 закритих періодів цього тесту іншим тестам спільної
/// бази не трапляться. Планувальник кличеться напряму — стан проєкту він не
/// читає.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeClosedPeriodsScaleTests(SqlServerFixture sql)
{
    private const int PeriodCount = 40;
    private const int EntityCount = 60;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Сорок_закритих_періодів_на_шістдесят_сутностей_один_запит_фільтра_з_фіксованими_параметрами()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        var (entities, keys) = await ArrangeAsync(builder, chain);

        // Сутність k має одну точку посеред періоду k % 40, і лише в ньому.
        var expected = entities
            .Select((id, k) => (id, keys[k % PeriodCount]))
            .ToHashSet();

        var commands = new CommandLog();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(commands)
            .Options);

        var jobs = Substitute.For<IBackgroundJobScheduler>();

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в `KeepClosedWithRawPointsAsync` форму
        // «UNION ALL гілок EXISTS на кожен період» (c16c131e) → команда фільтра
        // несе 200 параметрів (5 на закритий період) замість одного.
        await new MaterializationScheduler(db, jobs)
            .EnqueueAfterTransitionAsync(chain.ProjectId, keys, CancellationToken.None);

        var tasks = jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueAsync))
            .Select(c => (MaterializeTask)c.GetArguments()[0]!)
            .ToList();

        Assert.Equal(expected.Count, tasks.Count);
        Assert.Equal(expected, tasks.Select(t => (t.SourceEntityId, t.PeriodKey)).ToHashSet());

        // Фільтр — ОДНА команда, і її параметри не залежать від масштабу.
        var filter = Assert.Single(commands.Seen, c => c.Text.Contains("RawDataPoint", StringComparison.Ordinal));
        Assert.True(
            filter.Parameters == 1,
            $"Команда фільтра несе {filter.Parameters} параметрів, а не один — число росте з масштабом.");
    }

    /// <summary>40 закритих періодів з екземплярами таблиці, 60 сутностей з мапінгом і точками.</summary>
    private async Task<(List<int> Entities, int[] Keys)> ArrangeAsync(
        TestDocumentBuilder builder, TestDocument chain)
    {
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using var db = builder.CreateContext();

        var keys = Enumerable.Range(0, PeriodCount)
            .Select(i => (2026 + (i / 12)) * 100 + (i % 12) + 1)
            .ToArray();

        // Періоди: перший створив будівник, решту — тут; усі закриті.
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var firstInstance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", PeriodCount - 1, CancellationToken.None);

        for (var i = 1; i < PeriodCount; i++)
        {
            var key = new PeriodKey(keys[i]);
            db.Periods.Add(new Period(
                chain.ProjectId, key, (byte)key.Sequence,
                new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
            db.TableInstances.Add(new TableInstance(key, firstInstance + i - 1, chain.DocumentId, chain.TableDefId, now));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var periods = await db.Periods.Where(p => p.ProjectId == chain.ProjectId).ToListAsync();
        Assert.Equal(PeriodCount, periods.Count);
        foreach (var period in periods)
        {
            period.AdvanceTo(PeriodState.Grace, now);
            period.TransitionTo(PeriodState.Closed, now);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "scale" }),
            ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивні НАВМИСНО: активна сутність без завершеного збору робить
        // `SourcesHealthCheck` жовтим (як у `MaterializeOnPeriodOpenTests`).
        var sourceEntities = Enumerable.Range(0, EntityCount)
            .Select(k =>
            {
                var entity = new SourceEntity(dataSource.Id, $"Ent{tag}_{k}", RegistrySourceKind.External);
                entity.Deactivate();
                return entity;
            })
            .ToList();
        db.SourceEntities.AddRange(sourceEntities);
        await db.SaveChangesAsync(CancellationToken.None);

        var rowKey = await db.TableRows
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKey)
            .SingleAsync();

        var field = $"Scale_{tag}";
        foreach (var entity in sourceEntities)
        {
            var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
            map.SetMaterialization(rowKey.Value, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(now));
        for (var k = 0; k < EntityCount; k++)
        {
            var key = new PeriodKey(keys[k % PeriodCount]);
            var at = new DateTime(key.Year, key.Sequence, 15, 0, 0, 0, DateTimeKind.Utc);
            var runId = await store.StartRunAsync(
                sourceEntities[k].Id, at, at.AddDays(1), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);
            await store.UpsertRawPointsAsync(
                runId, sourceEntities[k].Id, [new SourceDataPoint(field, at, 1m, null, null, "Good")], CancellationToken.None);
        }

        return ([.. sourceEntities.Select(e => e.Id)], keys);
    }

    /// <summary>Текст і число параметрів кожної команди EF.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Text, int Parameters)> _seen = new();

        public IReadOnlyList<(string Text, int Parameters)> Seen => [.. _seen];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _seen.Enqueue((command.CommandText, command.Parameters.Count));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
