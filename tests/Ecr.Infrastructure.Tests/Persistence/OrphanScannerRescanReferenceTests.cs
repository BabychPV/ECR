// tests/Ecr.Infrastructure.Tests/Persistence/OrphanScannerRescanReferenceTests.cs
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
/// Точковий перерахунок <c>OrphanScanner.RescanForEntryAsync</c> зобов'язаний
/// рахувати ознаку по ВСІХ посиланнях рядка, а не лише по тому запису, заради
/// якого його викликали (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// ⛔ ДЕФЕКТ, який стереже цей тест. Вибірка кандидатів звужувалася фільтром
/// <c>cell.ValueRegistryEntryId == single</c> ДО угруповання — отже, до
/// правила «осиротілий, якщо ХОЧ ОДНЕ посилання нечинне» доходив рівно один
/// запис із рядка. Рядок, що посилається і на щойно полагоджений запис A, і на
/// досі нечинний B, після перерахунку по A діставав <c>allValid = true</c> й
/// потрапляв у <c>ToClear</c>: ознаку знімали, хоч посилання B лишалося
/// зламаним. Наслідок бачить користувач — <c>Submit</c> пропускає форму з
/// рядком, який указує на запис довідника, що більше не діє.
///
/// ⚠ Дефект НЕ видно на рядку з одним посиланням, а саме такі рядки й засівали
/// наявні тести (<c>SetEntryValidityQueryTranslationTests</c>,
/// <c>OrphanScannerCoverageTests</c>): коли посилання одне, «всі» і «те одне» —
/// це те саме. Потрібен рівно рядок із ДВОМА посиланнями різної чинності.
/// </remarks>
[Collection("SqlServer")]
public sealed class OrphanScannerRescanReferenceTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.13a")]
    public async Task Перерахунок_по_одному_запису_не_знімає_ознаку_з_рядка_з_іншим_нечинним_посиланням()
    {
        var seed = await SeedAsync(periodKey: 202605, orphaned: true);

        await using (var db = Context())
        {
            var scanner = new OrphanScanner(db, new RegistryResolver(), Clock);

            // Перераховуємо по ЧИННОМУ запису — тобто відтворюємо шлях
            // `SetEntryValidityHandler`: адміністратор полагодив вікно
            // чинності запису A і сканер іде знімати ознаку з рядків, які на
            // нього посилаються.
            var changed = await scanner.RescanForEntryAsync(seed.FreshEntryId, CancellationToken.None);

            // Знімати нема чого: рядок лишається осиротілим через B.
            Assert.Equal(0, changed);
        }

        await using var fresh = Context();
        var row = await fresh.TableRows
            .AsNoTracking()
            .SingleAsync(
                r => r.PeriodKeyValue == seed.PeriodKey && r.Id == seed.RowId,
                CancellationToken.None);

        Assert.True(
            row.IsOrphaned,
            "Ознаку знято з рядка, друге посилання якого (B) досі нечинне: "
            + "Submit перестане блокувати форму з посиланням на запис, що не діє.");
        Assert.NotNull(row.OrphanedAt);
    }

    /// <summary>
    /// Зворотний напрямок того самого правила: коли нечинних посилань не
    /// лишилося, ознака таки знімається.
    /// </summary>
    /// <remarks>
    /// ⚠ Без цього твердження перший тест проходив би й на реалізації, яка
    /// просто НІКОЛИ нічого не знімає, — тобто доводив би не те правило.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.13a")]
    public async Task Перерахунок_знімає_ознаку_коли_всі_посилання_рядка_чинні()
    {
        var seed = await SeedAsync(periodKey: 202606, orphaned: true, secondIsStale: false);

        await using (var db = Context())
        {
            var scanner = new OrphanScanner(db, new RegistryResolver(), Clock);
            var changed = await scanner.RescanForEntryAsync(seed.FreshEntryId, CancellationToken.None);

            Assert.Equal(1, changed);
        }

        await using var fresh = Context();
        var row = await fresh.TableRows
            .AsNoTracking()
            .SingleAsync(
                r => r.PeriodKeyValue == seed.PeriodKey && r.Id == seed.RowId,
                CancellationToken.None);

        Assert.False(row.IsOrphaned);
        Assert.Null(row.OrphanedAt);
    }

    /// <summary>
    /// Засіває один рядок із ДВОМА посиланнями на довідник у різних колонках.
    /// </summary>
    /// <param name="periodKey">Ключ періоду (він же ключ партиції).</param>
    /// <param name="orphaned">З якою ознакою народжується рядок.</param>
    /// <param name="secondIsStale">
    /// Чи нечинний ДРУГИЙ запис (посилання B). <c>false</c> — обидва чинні.
    /// </param>
    private async Task<SeededRow> SeedAsync(int periodKey, bool orphaned, bool secondIsStale = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: periodKey, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"RESCAN_{_tag}_{periodKey}"), Text("Rescan scratch"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        // A — чинний на дату періоду: саме по ньому йде перерахунок.
        var fresh = new RegistryEntry(
            def.Id, EcrCode.Create($"A_{_tag}_{periodKey}"), Text("Fresh"), 9, DateTime.UnixEpoch);
        fresh.SetValidity(new DateOnly(2020, 1, 1), null);
        db.RegistryEntries.Add(fresh);

        // B — друге посилання того самого рядка. Вікно чинності відкривається
        // у 2030-му, тобто на дату періоду запис НЕ діє.
        var second = new RegistryEntry(
            def.Id, EcrCode.Create($"B_{_tag}_{periodKey}"), Text("Second"), 9, DateTime.UnixEpoch);
        second.SetValidity(
            secondIsStale ? new DateOnly(2030, 1, 1) : new DateOnly(2020, 1, 1), null);
        db.RegistryEntries.Add(second);
        await db.SaveChangesAsync(CancellationToken.None);

        // Період має бути Open: інакше `OrphanScanPlan.IsScannable` відсіює
        // кандидата і тест перевіряв би ранній вихід, а не правило.
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == doc.ProjectId, CancellationToken.None);
        period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));

        var row = await db.TableRows.SingleAsync(
            r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0],
            CancellationToken.None);
        row.SetOrphaned(orphaned, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));

        // ⚠ РІЗНІ колонки: ключ `doc.CellValue` — `(PeriodKey, TableRowId,
        // ColumnDefId)`, два посилання в одній колонці існувати не можуть.
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[0]),
            doc.TableDefId,
            new CellValueData { ValueRegistryEntryId = fresh.Id }));

        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.TableDefId,
            new CellValueData { ValueRegistryEntryId = second.Id }));

        await db.SaveChangesAsync(CancellationToken.None);

        return new SeededRow(doc.PeriodKey.Value, doc.RowIds[0], fresh.Id);
    }

    /// <summary>Адреса засіяного рядка і запис, по якому йде перерахунок.</summary>
    private sealed record SeededRow(int PeriodKey, long RowId, long FreshEntryId);

    private static IClock Clock
        => new FixedClock(new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc));

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
