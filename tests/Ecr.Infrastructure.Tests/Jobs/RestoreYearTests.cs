using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Розархівація року (<c>arc.usp_RestoreYear</c>): атомарність, сусідні
/// проєкти спільної партиції, повтор.
/// </summary>
/// <remarks>
/// ⚠ Кожен тест бере СВІЙ період (202705, 202709, 202710, 202711), якого не
/// торкається жоден інший тест збірки: база спільна для колекції, а
/// процедура працює з партицією цілком — чужий документ у тому самому
/// періоді зробив би асерти на кількостях залежними від порядку тестів.
/// </remarks>
[Collection("SqlServer")]
public sealed class RestoreYearTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_посередині_розархівації_зберігає_архів_і_повтор_проходить()
    {
        // ⚠ U1-03 (аудит 09.10c): копія тепер пакетами, кожен пакет — окрема
        // транзакція. Гарантія змістилася з «нічого не повернуто» на «архів
        // цілий, журнал Failed, читач іде в архів, повтор доводить повернення
        // без PK-конфлікту».
        //
        // ⛔ Z6-01 (аудит 8-го кола): читачі продукту (F-13) журнал не питають —
        // в архів вони йдуть лише за ПОРОЖНЬОЇ гарячої вибірки. Пакет 1 (комірка
        // 77) закомічено, пакет 2 (комірка 88) падає: без прибирання в `CATCH`
        // екземпляр лишався в doc.* з однією коміркою, і зріз віддавав 77 без 88.
        // Мутація: прибрати блок прибирання з `CATCH` `usp_RestoreYear` — у
        // doc.* лишається комірка, зріз без 88, червоний.
        const int period = 202705;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 77m);
        await CellAsync(doc, 1, 2, 88m);

        await ArchiveAsync(doc.ProjectId, period, period);
        var archivedInstances = await CountAsync("arc.TableInstance", period);
        var archivedRows = await CountAsync("arc.TableRow", period);
        var archivedCells = await CountAsync("arc.CellValue", period);

        // ⚠ Збій — на вставці КОМІРОК, тобто ПІСЛЯ того, як екземпляри й рядки
        // уже вставлено: саме так лягає бойовий збій (FK, місце на диску,
        // розбіжність 50011). Тригер, а не CHECK: його знімає `finally`, і він
        // не чіпає нічого, крім цього періоду.
        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestoreFailTest ON doc.CellValue AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE PeriodKey = {period} AND ValueNumeric = 88)
                    THROW 50999, N'Штучний збій вставки комірки (тест).', 1;
            END
            """);

        try
        {
            // Пакет у одну комірку: 77 і 88 — різні транзакції.
            await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(doc.ProjectId, period, period, batchSize: 1));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreFailTest;");
        }

        // ⛔ Z6-01: закомічені пакети прибрано — у гарячій схемі періоду нічого,
        // тож F-13 веде читача в архів.
        Assert.Equal(0, await CountAsync("doc.CellValue", period));
        Assert.Equal(0, await CountAsync("doc.TableRow", period));
        Assert.Equal(0, await CountAsync("doc.TableInstance", period));

        await using (var db = sql.CreateContext())
        {
            var slice = await new NormalizedCellStore(db, new ArchiveAwareCellReader(db))
                .ReadSliceAsync(doc.TableInstanceId, default);
            var values = slice.Select(c => c.Value.ValueNumeric).ToList();
            Assert.Contains(77m, values);
            Assert.Contains(88m, values);
        }

        // ⛔ Архів — на місці, до рядка: до повної звірки його не чіпають.
        Assert.Equal(archivedInstances, await CountAsync("arc.TableInstance", period));
        Assert.Equal(archivedRows, await CountAsync("arc.TableRow", period));
        Assert.Equal(archivedCells, await CountAsync("arc.CellValue", period));

        var failed = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Failed"), (failed.Direction, failed.Status));

        // Читач досі йде в архів: провалений прогін не рахується.
        await using (var db = sql.CreateContext())
        {
            Assert.True(await new ArchiveAwareCellReader(db).IsArchivedAsync(doc.ProjectId, doc.PeriodKey, default));
        }

        // Повтор після усунення причини — проходить, а не падає на PK.
        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(archivedCells, await CountAsync("doc.CellValue", period));
        Assert.Equal(archivedRows, await CountAsync("doc.TableRow", period));
        Assert.Equal(archivedInstances, await CountAsync("doc.TableInstance", period));
        Assert.Equal(0, await CountAsync("arc.CellValue", period));
        Assert.Equal(77m, await ValueAsync(doc, 0, 1));
        Assert.Equal(88m, await ValueAsync(doc, 1, 2));

        var done = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Completed"), (done.Direction, done.Status));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розархівація_спільної_партиції_пише_журнал_на_кожен_повернутий_проєкт()
    {
        const int period = 202709;

        // Два проєкти в одній партиції. Архівували за A — фізично переїхав і B
        // (`usp_ArchiveYear` журналює обох). Повертають теж за A.
        var a = await DocumentAsync(period);
        await CellAsync(a, 0, 1, 11m);
        var b = await DocumentAsync(period);
        await CellAsync(b, 0, 1, 22m);

        var since = await ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM itg.ArchiveRun");

        await ArchiveAsync(a.ProjectId, period, period);
        await RestoreAsync(a.ProjectId, period, period);

        // ⛔ Партиція повертається ЦІЛКОМ (фільтра за проєктом у неї немає, як і
        // в `usp_ArchiveYear`), тож і журнал мусить знати про B. Інакше
        // останній завершений прогін B — `ToArchive`, і читач іде в `arc.*`.
        //
        // ⚠ «Рівно ті, що повернуто» — це той самий набір, що архівація
        // записала `ToArchive`, а не «лише A і B»: база спільна, і в повному
        // прогоні інший тест збірки заводить проєкт із 40 періодами
        // 202601–202904 (перевірено запитом до `EcrTest_Infrastructure_*`), тож
        // чужий проєкт із періодом 202709 фізично переїжджає разом із нами.
        var archived = await ProjectsWithRunAsync("ToArchive", period, since);
        var restored = await ProjectsWithRunAsync("FromArchive", period, since);
        Assert.Contains(a.ProjectId, restored);
        Assert.Contains(b.ProjectId, restored);
        Assert.Equal(archived.Order(), restored.Order());

        await using (var db = sql.CreateContext())
        {
            var reader = new ArchiveAwareCellReader(db);
            Assert.False(await reader.IsArchivedAsync(b.ProjectId, b.PeriodKey, default));
            Assert.Equal(22m, Assert.Single(await reader.ReadAsync(b.ProjectId, b.TableInstanceId, b.PeriodKey, default)).Value.ValueNumeric);
        }

        // Правка B у гарячій схемі після розархівації — видима. Застарілий
        // архів замість неї — рівно той дефект, що ховався за журналом.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 23 WHERE PeriodKey = {period} "
            + $"AND TableRowId = {b.RowIds[0]} AND ColumnDefId = {b.ColumnDefIds[1]}");

        await using (var db = sql.CreateContext())
        {
            var reader = new ArchiveAwareCellReader(db);
            Assert.Equal(23m, Assert.Single(await reader.ReadAsync(b.ProjectId, b.TableInstanceId, b.PeriodKey, default)).Value.ValueNumeric);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторна_розархівація_не_падає_на_PK_і_не_дублює_рядки()
    {
        const int period = 202710;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 5.25m);

        var cells = await CountAsync("doc.CellValue", period);
        var rows = await CountAsync("doc.TableRow", period);

        await ArchiveAsync(doc.ProjectId, period, period);
        await RestoreAsync(doc.ProjectId, period, period);

        // Другий виклик — той самий діапазон. Раніше: PK_TableInstance.
        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(cells, await CountAsync("doc.CellValue", period));
        Assert.Equal(rows, await CountAsync("doc.TableRow", period));
        Assert.Equal(5.25m, await ValueAsync(doc, 0, 1));

        var run = await LastRunAsync(doc.ProjectId);
        Assert.Equal(("FromArchive", "Completed"), (run.Direction, run.Status));
    }

    /// <summary>D4 аудиту: позначка осиротілого рядка переживає архів і повернення.</summary>
    /// <remarks>
    /// Раніше `arc.TableRow` не мав `IsOrphaned`, розархівація писала 0, і
    /// осиротілий рядок переставав блокувати подання (ECR-SUB-4221).
    /// Мутаційний доказ: прибрати `IsOrphaned` з вставки `usp_ArchiveYear` —
    /// червоніє читання з архіву; повернути `0` замість `a.IsOrphaned` у
    /// `usp_RestoreYear` — червоніє перевірка після відновлення.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівація_і_розархівація_зберігають_IsOrphaned_рядка()
    {
        const int period = 202711;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 1m);

        await ExecuteAsync(
            $"UPDATE doc.TableRow SET IsOrphaned = CASE WHEN Id = {doc.RowIds[0]} THEN 1 ELSE 0 END "
            + $"WHERE PeriodKey = {period} AND TableInstanceId = {doc.TableInstanceId}");

        await ArchiveAsync(doc.ProjectId, period, period);

        // Читання з архіву показує позначку, а не жорсткий `false`.
        await using (var db = sql.CreateContext())
        {
            var flags = await new ArchiveAwareCellReader(db)
                .ReadArchivedOrphanFlagsAsync(doc.TableInstanceId, doc.PeriodKey, default);
            Assert.True(flags.Single(f => f.Id == doc.RowIds[0]).IsOrphaned);
            Assert.False(flags.Single(f => f.Id == doc.RowIds[1]).IsOrphaned);
        }

        await RestoreAsync(doc.ProjectId, period, period);

        Assert.True(await OrphanAsync(period, doc.RowIds[0]));
        Assert.False(await OrphanAsync(period, doc.RowIds[1]));
    }

    /// <summary>
    /// Feature (j): the month parts split between permits (<c>doc.CellPermitPart</c>) move to <c>arc.CellPermitPart</c> with
    /// their period and come back on restore — the per-permit attribution of an archived year is not lost.
    /// </summary>
    /// <remarks>
    /// Mutation proof (local): drop the parts INSERT from <c>usp_ArchiveYear</c> — the gap check (50014) refuses the TRUNCATE
    /// and the archive fails; drop it from <c>usp_RestoreYear</c> — the restored count is 0.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j5")]
    public async Task Архівація_і_розархівація_переносять_частини_місяця_між_дозволами()
    {
        const int period = 202712;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 12m);
        long entryId;
        await using (var db = sql.CreateContext())
        {
            var text = new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" });
            var registry = new Ecr.Domain.Entities.Configuration.RegistryDef(
                Ecr.Domain.ValueObjects.EcrCode.Create($"PRM_ARC{Guid.NewGuid():N}"[..20].ToUpperInvariant()), text, isTemporal: true);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(registry.Id, Ecr.Domain.ValueObjects.EcrCode.Create("A"), text);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            entryId = entry.Id;
        }

        await ExecuteAsync($"""
            DECLARE @entry int = {entryId};
            INSERT INTO doc.CellPermitPart
                (PeriodKey, DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, PartNo, RegistryEntryId, DayFrom, DayTo, ValueNumeric)
            VALUES ({period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 1, @entry, '2027-12-01', '2027-12-14', 5.5),
                   ({period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 2, @entry, '2027-12-15', '2027-12-31', 6.5);
            """);

        await ArchiveAsync(doc.ProjectId, period, period);

        Assert.Equal(0, await CountAsync("doc.CellPermitPart", period));
        Assert.Equal(2, await CountAsync("arc.CellPermitPart", period));

        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(2, await CountAsync("doc.CellPermitPart", period));
        Assert.Equal(0, await CountAsync("arc.CellPermitPart", period));
        Assert.Equal(12m, await ScalarAsync<decimal>(
            $"SELECT SUM(ValueNumeric) FROM doc.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}"));
    }

    /// <summary>
    /// Debt «ar»: user cell styles (<c>doc.CellStyle</c>) and manual formula overrides (<c>doc.CellFormulaOverride</c>)
    /// leave the hot schema with their period and come back identical on restore — before, <c>usp_ArchiveYear</c>
    /// never touched them and their partitions stayed in <c>DATA_HOT</c> after the year was archived.
    /// </summary>
    /// <remarks>
    /// ⚠ Period 202712 is shared with the permit-parts test above; every assertion is narrowed to this document's rows,
    /// so the order of the two tests does not matter (the procedures move the whole partition either way).
    /// Mutation proof (local): the procedures as they were before this change — the rows stay in <c>doc.*</c>, red.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "debt-1110-ar")]
    public async Task Архівація_і_розархівація_переносять_стилі_комірок_і_ручні_значення_формул()
    {
        const int period = 202712;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 7m);
        await CellAsync(doc, 1, 2, 8m);

        var rows = string.Join(", ", doc.RowIds.Take(2));
        await ExecuteAsync($"""
            INSERT INTO doc.CellStyle
                (PeriodKey, TableRowId, ColumnDefId, BackgroundHex, ForegroundHex, IsBold, UpdatedAt, UpdatedByUserId)
            VALUES ({period}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, '#FFEE00', NULL, 1, '2027-12-03T10:11:12.345', 1),
                   ({period}, {doc.RowIds[1]}, {doc.ColumnDefIds[2]}, NULL, '#0000AA', 0, '2027-12-04T01:02:03.004', 2);
            INSERT INTO doc.CellFormulaOverride
                (PeriodKey, TableRowId, ColumnDefId, TableInstanceId, Reason, SetAt, SetByUserId)
            VALUES ({period}, {doc.RowIds[1]}, {doc.ColumnDefIds[2]}, {doc.TableInstanceId}, N'Ручне значення з акту',
                    '2027-12-05T06:07:08.009', 3);
            """);

        var styles = $"""
            SELECT PeriodKey, TableRowId, ColumnDefId, BackgroundHex, ForegroundHex, IsBold, UpdatedAt, UpdatedByUserId
              FROM SCHEMA.CellStyle WHERE PeriodKey = {period} AND TableRowId IN ({rows})
             ORDER BY TableRowId, ColumnDefId FOR JSON PATH, INCLUDE_NULL_VALUES
            """;
        var overrides = $"""
            SELECT PeriodKey, TableRowId, ColumnDefId, TableInstanceId, Reason, SetAt, SetByUserId
              FROM SCHEMA.CellFormulaOverride WHERE PeriodKey = {period} AND TableRowId IN ({rows})
             ORDER BY TableRowId, ColumnDefId FOR JSON PATH, INCLUDE_NULL_VALUES
            """;
        Task<string> Json(string query, string schema)
            => ScalarAsync<string>(query.Replace("SCHEMA.", schema + ".", StringComparison.Ordinal));

        var stylesBefore = await Json(styles, "doc");
        var overridesBefore = await Json(overrides, "doc");
        Assert.Contains("#FFEE00", stylesBefore, StringComparison.Ordinal);
        Assert.Contains("Ручне значення з акту", overridesBefore, StringComparison.Ordinal);

        await ArchiveAsync(doc.ProjectId, period, period);

        Assert.Null(await Json(styles, "doc"));
        Assert.Null(await Json(overrides, "doc"));
        Assert.Equal(stylesBefore, await Json(styles, "arc"));
        Assert.Equal(overridesBefore, await Json(overrides, "arc"));

        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(stylesBefore, await Json(styles, "doc"));
        Assert.Equal(overridesBefore, await Json(overrides, "doc"));
        Assert.Null(await Json(styles, "arc"));
        Assert.Null(await Json(overrides, "arc"));
    }

    /// <summary>
    /// D-12 (аудит 2026-11): частини місяця звіряються при розархівації так само, як комірки, — за логічним ключем і
    /// ТОТОЖНИМ значенням. Гарячий рядок із тим самим ключем, але іншим значенням більше не замінює архівний мовчки.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати з кроку 2 <c>usp_RestoreYear</c> умову <c>@srcParts &lt;&gt; @dstParts</c> — розархівація
    /// проходить, архівне значення частини втрачено, тест червоний.
    /// ⚠ Період 202712 спільний з тестами вище; усі асерти звужені до рядків цього документа, і тест лишає період
    /// розархівованим (як і сусіди).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-12")]
    public async Task Розбіжність_частини_місяця_зупиняє_розархівацію_і_зберігає_архів()
    {
        const int period = 202712;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 12m);
        var entryId = await PermitEntryAsync();
        await PartsAsync(doc, period, entryId);

        await ArchiveAsync(doc.ProjectId, period, period);
        Assert.Equal(2, await PartCountAsync("arc", period, doc));

        // Гарячий рядок із тим самим логічним ключем (PartNo = 1), але іншим значенням: вставка кроку 1 його пропускає.
        await ExecuteAsync($"""
            INSERT INTO doc.CellPermitPart
                (PeriodKey, DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, PartNo, RegistryEntryId, DayFrom, DayTo, ValueNumeric)
            VALUES ({period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 1, {entryId}, '2027-12-01', '2027-12-14', 9.9);
            """);

        var error = await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(doc.ProjectId, period, period));
        Assert.Equal(50011, error.Number);

        // ⛔ Архів цілий: обидві частини з первинними значеннями.
        Assert.Equal(2, await PartCountAsync("arc", period, doc));
        Assert.Equal(12m, await ScalarAsync<decimal>(
            $"SELECT SUM(ValueNumeric) FROM arc.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}"));

        // Причину усунено — повтор проходить і повертає архівні значення.
        await ExecuteAsync(
            $"DELETE FROM doc.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}");
        await RestoreAsync(doc.ProjectId, period, period);

        Assert.Equal(2, await PartCountAsync("doc", period, doc));
        Assert.Equal(0, await PartCountAsync("arc", period, doc));
        Assert.Equal(12m, await ScalarAsync<decimal>(
            $"SELECT SUM(ValueNumeric) FROM doc.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}"));

        await DropPartsAsync(doc, period);
    }

    /// <summary>
    /// D-12 (аудит 2026-11): відкат невдалої розархівації (Z6-01) прибирає й відновлені частини місяця. Доти вони
    /// лишалися сиротами в гарячих партиціях заархівованого року.
    /// </summary>
    /// <remarks>
    /// ⚠ Збій — на вставці СТИЛІВ: вона йде ПІСЛЯ вставки частин (кожна — окремий оператор без спільної транзакції),
    /// тож на момент збою частини вже закомічено в <c>doc.*</c>.
    /// Мутація: прибрати з <c>CATCH</c> <c>usp_RestoreYear</c> оператор <c>DELETE p FROM doc.CellPermitPart</c> —
    /// у гарячій схемі лишаються дві частини, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-12")]
    public async Task Відкат_невдалої_розархівації_прибирає_відновлені_частини_місяця()
    {
        const int period = 202712;
        var doc = await DocumentAsync(period);
        await CellAsync(doc, 0, 1, 12m);
        var entryId = await PermitEntryAsync();
        await PartsAsync(doc, period, entryId);
        await ExecuteAsync($"""
            INSERT INTO doc.CellStyle
                (PeriodKey, TableRowId, ColumnDefId, BackgroundHex, ForegroundHex, IsBold, UpdatedAt, UpdatedByUserId)
            VALUES ({period}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, '#D12D12', NULL, 0, '2027-12-06T10:00:00', 1);
            """);

        await ArchiveAsync(doc.ProjectId, period, period);
        Assert.Equal(0, await PartCountAsync("doc", period, doc));

        await ExecuteAsync($"""
            CREATE TRIGGER doc.TR_RestoreFailAfterParts ON doc.CellStyle AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE PeriodKey = {period} AND TableRowId = {doc.RowIds[0]})
                    THROW 50998, N'Штучний збій після вставки частин (тест D-12).', 1;
            END
            """);

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(doc.ProjectId, period, period));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER doc.TR_RestoreFailAfterParts;");
        }

        // ⛔ D-12: частини прибрано разом з екземпляром; архів цілий.
        Assert.Equal(0, await PartCountAsync("doc", period, doc));
        Assert.Equal(2, await PartCountAsync("arc", period, doc));
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableInstance WHERE PeriodKey = {period} AND Id = {doc.TableInstanceId}"));

        // Повтор після усунення причини повертає частини.
        await RestoreAsync(doc.ProjectId, period, period);
        Assert.Equal(2, await PartCountAsync("doc", period, doc));
        Assert.Equal(0, await PartCountAsync("arc", period, doc));

        await DropPartsAsync(doc, period);
    }

    /// <summary>
    /// Прибирає частини цього документа з гарячої схеми: тест «Архівація_і_розархівація_переносять_частини…» у тому
    /// самому періоді рахує частини періоду ЦІЛКОМ, а процедури переносять партицію разом із чужими документами.
    /// </summary>
    private Task DropPartsAsync(TestDocument doc, int period)
        => ExecuteAsync($"DELETE FROM doc.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}");

    private async Task<long> PermitEntryAsync()
    {
        await using var db = sql.CreateContext();
        var text = new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" });
        var registry = new Ecr.Domain.Entities.Configuration.RegistryDef(
            Ecr.Domain.ValueObjects.EcrCode.Create($"PRM_D12{Guid.NewGuid():N}"[..20].ToUpperInvariant()), text, isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(registry.Id, Ecr.Domain.ValueObjects.EcrCode.Create("A"), text);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return entry.Id;
    }

    private Task PartsAsync(TestDocument doc, int period, long entryId)
        => ExecuteAsync($"""
            INSERT INTO doc.CellPermitPart
                (PeriodKey, DocumentId, TableDefId, SheetCopyId, RowKey, ColumnDefId, PartNo, RegistryEntryId, DayFrom, DayTo, ValueNumeric)
            VALUES ({period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 1, {entryId}, '2027-12-01', '2027-12-14', 5.5),
                   ({period}, {doc.DocumentId}, {doc.TableDefId}, NULL, N'R1', {doc.ColumnDefIds[1]}, 2, {entryId}, '2027-12-15', '2027-12-31', 6.5);
            """);

    private Task<int> PartCountAsync(string schema, int period, TestDocument doc)
        => ScalarAsync<int>(
            $"SELECT COUNT(*) FROM {schema}.CellPermitPart WHERE PeriodKey = {period} AND DocumentId = {doc.DocumentId}");

    private async Task<bool> OrphanAsync(int period, long rowId)
        => await ScalarAsync<int>(
            $"SELECT CAST(IsOrphaned AS int) FROM doc.TableRow WHERE PeriodKey = {period} AND Id = {rowId}")
            .ConfigureAwait(false) == 1;

    private async Task<TestDocument> DocumentAsync(int periodKey)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: periodKey, ct: CancellationToken.None)
            .ConfigureAwait(false);

        await ExecuteAsync(
            $"UPDATE doc.Project SET Status = 3, ClosedAt = '2020-01-01' WHERE Id = {doc.ProjectId}")
            .ConfigureAwait(false);

        return doc;
    }

    private Task CellAsync(TestDocument doc, int row, int column, decimal value)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[row]}, {doc.ColumnDefIds[column]},
                    {doc.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

    private Task<decimal> ValueAsync(TestDocument doc, int row, int column)
        => ScalarAsync<decimal>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} "
            + $"AND TableRowId = {doc.RowIds[row]} AND ColumnDefId = {doc.ColumnDefIds[column]}");

    /// <summary>Архівація так, як її виконує система (див. <c>ArchiveJobTests.ArchiveAsync</c>).</summary>
    private async Task ArchiveAsync(int projectId, int from, int to)
    {
        await ExecuteAsync(
            $"""
            UPDATE doc.Project SET Status = 4
             WHERE Id IN (SELECT DISTINCT ProjectId FROM doc.Period
                           WHERE PeriodKey BETWEEN {from} AND {to});
            """).ConfigureAwait(false);

        await ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}").ConfigureAwait(false);
    }

    private Task RestoreAsync(int projectId, int from, int to, int batchSize = 500000)
        => ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}, @BatchSize = {batchSize}");

    private Task<int> CountAsync(string table, int periodKey)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {periodKey}");

    private async Task<List<int>> ProjectsWithRunAsync(string direction, int period, long sinceRunId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT ProjectId FROM itg.ArchiveRun WHERE Direction = N'{direction}' "
            + $"AND Status = N'Completed' AND FromPeriodKey <= {period} AND ToPeriodKey >= {period} "
            + $"AND Id > {sinceRunId}";

        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private async Task<(string Direction, string Status)> LastRunAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP 1 Direction, Status FROM itg.ArchiveRun "
            + $"WHERE ProjectId = {projectId} ORDER BY StartedAt DESC, Id DESC";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));

        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
