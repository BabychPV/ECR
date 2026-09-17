// tests/Ecr.Infrastructure.Tests/Persistence/OrphanScannerPartitionScopeTests.cs
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Нічний прохід <c>OrphanScanner</c> зобов'язаний писати ознаку
/// <c>IsOrphaned</c> В МЕЖАХ ПЕРІОДУ рядка — тобто нести <c>PeriodKey</c>,
/// ключ партиції <c>doc.TableRow</c>, у <c>WHERE</c> обох <c>UPDATE</c>.
/// </summary>
/// <remarks>
/// ⛔ Дефект: обидва <c>ExecuteUpdateAsync</c> фільтрували тільки за
/// <c>Id</c> (<c>decision.ToFlag.Contains(r.Id)</c>). Первинний ключ таблиці —
/// складений <c>(PeriodKey, Id)</c>, таблиця лежить на <c>ps_ByPeriodKey</c>,
/// і жодного індексу з <c>Id</c> попереду немає й бути не може:
/// <c>07-partition-tables.sql</c> вирівнює кожен індекс цих таблиць по схемі
/// партиціонування й падає (<c>THROW 50031</c>), якщо хоч один лишився поза
/// нею. Тобто прохід по таблиці, розрахованій на ~108 млн рядків на рік, ішов
/// по ВСІХ партиціях щоночі.
///
/// Причина, чому це не виправили разом із <c>RowStore.TouchRowsAsync</c>
/// (<c>fix/touchrows-index</c>): там період був відомий на місці виклику, а
/// тут <c>OrphanScanPlan</c> ніс самі <c>Id</c> — періоду на місці запису
/// ПРОСТО НЕ БУЛО. Тому фікс — не один предикат, а зміна типу рішення:
/// <c>OrphanRowRef(PeriodKeyValue, RowId)</c>.
///
/// ⚠ ЩО САМЕ ДОВОДИТЬ ЦЕЙ ТЕСТ, а що ні. Він НЕ міряє план і не доводить
/// швидкодію: тестова база не партиційована (скрипти <c>01</c>/<c>02</c>/<c>07</c>
/// у неї не подаються), тож звідси не видно ні <c>Seek</c>, ні логічних
/// читань — заміри зроблено окремо, на 4.8 млн рядків / 24 партиції, і
/// наведено в описі коміта. Тест доводить рівно те, що предикат на
/// <c>PeriodKey</c> справді стоїть в ОБОХ запитах і справді їх звужує: у
/// сусідньому періоді лежать два рядки з ТИМИ САМИМИ <c>Id</c> (складений
/// первинний ключ це дозволяє) і БЕЗ жодного посилання на довідник — тобто не
/// кандидати. Якщо прибрати <c>PeriodKey</c> з <c>WHERE</c> постановки, чужий
/// рядок отримає <c>IsOrphaned = 1</c>; якщо зі зняття — чужий позначений
/// рядок ознаку втратить. Обидві мутації тест ловить. Зв'язок зі швидкодією
/// непрямий, але однозначний: предикат, який відсікає чужу партицію в даних, —
/// це той самий предикат, який відсікає її в плані.
///
/// Він також НЕ доводить нічого про ПОКРИТТЯ проходу: <c>Take(20_000)</c> без
/// впорядкування й курсора лишається як був — це окремий дефект.
/// </remarks>
[Collection("SqlServer")]
public sealed class OrphanScannerPartitionScopeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.13a")]
    public async Task Нічний_прохід_не_виходить_за_межі_періоду_свого_рядка()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // Два документи в СУСІДНІХ періодах. Другий потрібен лише як власник
        // чужої партиції: саме в неї ми покладемо рядок-двійник.
        var own = await builder.BuildAsync(
            periodKey: 202601, rowCount: 2, ct: CancellationToken.None);
        var foreignDoc = await builder.BuildAsync(
            periodKey: 202602, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"ORPHSCOPE_{_tag}"), Text("Orphan scope scratch"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        // Запис, НЕчинний у січні: вікно починається в червні. Саме він робить
        // рядок першого документа осиротілим.
        var entry = new RegistryEntry(def.Id, EcrCode.Create($"E_{_tag}"), Text("Entry"), 9, DateTime.UnixEpoch);
        entry.SetValidity(new DateOnly(2026, 6, 1), null);
        db.RegistryEntries.Add(entry);

        // І чинний запис — для ДРУГОГО напрямку механізму (зняття ознаки).
        // Перевіряти лише постановку означало б лишити половину виправлення
        // без доказу: `ToClear` — окремий `UPDATE` з окремим предикатом.
        var validEntry = new RegistryEntry(
            def.Id, EcrCode.Create($"V_{_tag}"), Text("Valid entry"), 9, DateTime.UnixEpoch);
        validEntry.SetValidity(new DateOnly(2025, 1, 1), null);
        db.RegistryEntries.Add(validEntry);
        await db.SaveChangesAsync(CancellationToken.None);

        // Обидва періоди — Open: інакше `OrphanScanPlan.IsScannable` відсіює
        // кандидата, і тест перевіряв би ранній вихід, а не предикат.
        foreach (var projectId in new[] { own.ProjectId, foreignDoc.ProjectId })
        {
            var period = await db.Periods.SingleAsync(
                p => p.ProjectId == projectId, CancellationToken.None);
            period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var flagRowId = own.RowIds[0];
        var clearRowId = own.RowIds[1];

        db.CellValues.Add(new CellValue(
            new CellAddress(own.PeriodKey, flagRowId, own.ColumnDefIds[0]),
            own.TableDefId,
            new CellValueData { ValueRegistryEntryId = entry.Id }));

        db.CellValues.Add(new CellValue(
            new CellAddress(own.PeriodKey, clearRowId, own.ColumnDefIds[0]),
            own.TableDefId,
            new CellValueData { ValueRegistryEntryId = validEntry.Id }));

        // Другий рядок уже позначений — сканер має ознаку ЗНЯТИ.
        var seeded = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        var toClear = await db.TableRows.SingleAsync(
            r => r.PeriodKeyValue == own.PeriodKey.Value && r.Id == clearRowId, CancellationToken.None);
        toClear.SetOrphaned(true, seeded);

        // ⚠ ТІ САМІ Id у сусідньому періоді. У бойових даних Id видає спільна
        // SEQUENCE, тож такий збіг — рідкість; але первинний ключ складений,
        // база його дозволяє, і саме ці рядки відрізняють «запит засікся по
        // партиції» від «запит пройшов по всіх». Посилань на довідник у них
        // НЕМАЄ — вони не кандидати ні за яким правилом, і чіпати їх сканер не
        // має права в ЖОДЕН бік.
        db.TableRows.Add(new TableRow(
            foreignDoc.PeriodKey, flagRowId, foreignDoc.TableInstanceId,
            RowKey.Create($"TWINF_{_tag}"), ordinal: 98, seeded));

        var clearTwin = new TableRow(
            foreignDoc.PeriodKey, clearRowId, foreignDoc.TableInstanceId,
            RowKey.Create($"TWINC_{_tag}"), ordinal: 99, seeded);

        // Двійник у чужій партиції позначений — і має таким лишитися: чужий
        // `UPDATE` зі зняттям ознаки не має до нього дійти.
        clearTwin.SetOrphaned(true, seeded);
        db.TableRows.Add(clearTwin);

        await db.SaveChangesAsync(CancellationToken.None);

        await using (var scan = Context())
        {
            var scanner = new OrphanScanner(
                scan,
                new RegistryResolver(),
                new FixedClock(new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)));

            await scanner.ScanAllAsync(CancellationToken.None);
        }

        await using var check = Context();

        var flagged = await OrphanedAsync(check, own.PeriodKey.Value, flagRowId);
        var cleared = await OrphanedAsync(check, own.PeriodKey.Value, clearRowId);
        var flagTwin = await OrphanedAsync(check, foreignDoc.PeriodKey.Value, flagRowId);
        var clearTwinAfter = await OrphanedAsync(check, foreignDoc.PeriodKey.Value, clearRowId);

        // Прохід справді щось зробив, і в обидва боки: без цих двох тверджень
        // тест проходив би й тоді, коли сканер не змінив НІЧОГО, — а це не
        // доказ звуження.
        Assert.True(flagged);
        Assert.False(cleared);

        // І не зачепив чужу партицію — ані `UPDATE`-ом постановки, ані зняття.
        Assert.False(flagTwin);
        Assert.True(clearTwinAfter);
    }

    /// <summary>Збережена ознака конкретного рядка конкретного періоду.</summary>
    private static Task<bool> OrphanedAsync(EcrDbContext db, int periodKey, long rowId)
        => db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey && r.Id == rowId)
            .Select(r => r.IsOrphaned)
            .SingleAsync(CancellationToken.None);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
