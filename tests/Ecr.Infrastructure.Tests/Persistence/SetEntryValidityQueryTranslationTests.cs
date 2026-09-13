// tests/Ecr.Infrastructure.Tests/Persistence/SetEntryValidityQueryTranslationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>POST …/entries/{id}/validity</c> на генуїнно ВАЛІДНОМУ вікні падав
/// <c>500 ECR-SYS-0500</c> — і то ЗАВЖДИ, на будь-якому вікні: єдиний робочий
/// шлях ендпоінта був відхиленням (порожнє вікно, <c>ECR-REG-0422</c>).
/// </summary>
/// <remarks>
/// ⛔ Корінь — НЕ дублікат/гонитва (клас findings 1-3): <c>OrphanScanner.RunAsync</c>
/// складає `candidateQuery` через `select new CellReference(...)` в
/// ІМЕНОВАНИЙ record, а тоді (лише для <c>RescanForEntryAsync</c>, тобто лише
/// на шляху <c>SetEntryValidityHandler</c> — <c>ScanAllAsync</c> цю гілку
/// ніколи не виконує) додає `.Where(c => c.RegistryEntryId == single)` НАД
/// уже спроєктованим record-типом. EF Core не вміє скласти такий `Where`
/// назад у SQL (працює для анонімних типів, не для іменованих records) і
/// кидає `InvalidOperationException: … could not be translated` —
/// НЕОБРОБЛЕНИЙ виняток, якого <c>ExceptionHandlingMiddleware</c> не мапить
/// ні на що, крім `500`.
///
/// Тест відтворює РІВНО умову, за якої ця гілка виконується: період у стані
/// <c>Open</c> (інакше `RunAsync` виходить раніше, на кроці 1) і хоча б одна
/// комірка з посиланням на цей запис (інакше — на кроці 2, `references.Count
/// == 0`). Обидва кроки без цього тесту лишаються незачепленими: репродукція
/// на порожній базі (без відкритого періоду й комірок) НЕ падає, бо жоден із
/// цих двох ранніх виходів не доходить до composed `Where` узагалі —
/// саме тому дефект не впіймав жоден наявний тест `SetEntryValidityHandler`.
/// </remarks>
[Collection("SqlServer")]
public sealed class SetEntryValidityQueryTranslationTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "lane1-2-4-unhandled-500-pattern")]
    public async Task Валідне_вікно_на_записі_з_посиланням_у_відкритому_періоді_не_падає_500()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Fixed, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"VALIDITY_{_tag}"), Text("Validity scratch"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(def.Id, EcrCode.Create("E1"), Text("Entry 1"), 9, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();

        // Період має бути ВІДКРИТИЙ: `OrphanScanner.RunAsync` виходить на
        // кроці 1 (`periods.Count == 0`), якщо жоден період не в
        // Open/Grace, і composed `Where` під дефектом просто не виконується.
        var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId);
        period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        // Комірка з ПОСИЛАННЯМ на цей запис: без неї крок 2 повертає
        // `references.Count == 0` раніше, ніж доходить до composed `Where`.
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[0]);
        var cell = new CellValue(address, doc.TableDefId, new CellValueData
        {
            ValueRegistryEntryId = checked((int)entry.Id),
        });
        db.CellValues.Add(cell);
        await db.SaveChangesAsync();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.EditData").Build());

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(9);
        currentUser.CorrelationId.Returns("test");

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc));

        var handler = new SetEntryValidityHandler(
            new RegistryStore(db),
            new OrphanScanner(db, new RegistryResolver(), clock),
            new UnitOfWork(db),
            new AuditWriter(db),
            access,
            currentUser,
            clock);

        // Вікно ГЕНУЇННО ВАЛІДНЕ (from < to, той самий клас, що й репродукція
        // finding 4 — 01.03.2026..01.06.2026): до фіксу тут летів
        // `InvalidOperationException` замість збереженого вікна.
        var affected = await handler.HandleAsync(
            entry.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        // Запис НЕ чинний у січні (період документа) — рядок з посиланням на
        // нього стає осиротілим: перерахунок справді відбувся, а не просто
        // "не впав".
        Assert.Equal(1, affected);

        await using var fresh = Context();
        var reread = await fresh.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        Assert.Equal(new DateOnly(2026, 3, 1), reread.ValidFrom);
        Assert.Equal(new DateOnly(2026, 6, 1), reread.ValidTo);

        var row = await fresh.TableRows.AsNoTracking().SingleAsync(r => r.Id == doc.RowIds[0]);
        Assert.True(row.IsOrphaned);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
