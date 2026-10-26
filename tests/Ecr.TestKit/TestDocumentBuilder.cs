using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.TestKit;

/// <summary>
/// Мінімальний ланцюг «шаблон → проєкт → період → документ → таблиця → рядки»
/// для інтеграційних тестів.
/// </summary>
/// <remarks>
/// Будується <b>доменними конструкторами через EF</b>, а не сирими
/// <c>INSERT</c>: так тест даних заразом перевіряє конфігурації сутностей.
/// Ланцюг із сирих <c>INSERT</c> проходив би і на зламаному мапінгу.
///
/// Це доповнення до `05-skeleton.md` §1 — файла в дереві немає (`Q-043`).
/// Без нього неможливо написати жоден тест, який працює з даними, а таких у
/// модулях 1.5–1.6 більшість.
/// </remarks>
public sealed class TestDocumentBuilder(string connectionString)
{
    private static int _counter;

    /// <summary>Створює ланцюг і повертає ідентифікатори його ланок.</summary>
    /// <param name="periodKey">Ключ періоду (<c>Year*100 + Sequence</c>).</param>
    /// <param name="columnCount">Скільки колонок у таблиці.</param>
    /// <param name="rowCount">Скільки рядків створити в екземплярі.</param>
    /// <param name="rowMode">
    /// Режим рядків таблиці. За замовчуванням <c>Fixed</c> — саме його чекає
    /// решта тестів, які вже користуються будівником.
    /// </param>
    /// <param name="layoutKind">Q-17: розкладка таблиці; за замовчуванням <c>PerPeriodInstance</c>.</param>
    /// <param name="monthColumns">Q-17: скільки місячних стовпців M1..Mn додати після звичайних колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <paramref name="rowMode"/> став параметром заради тестів ЗАПИСУ:
    /// <c>CreateRowHandler</c> питає <c>TableDef.AllowsDynamicRows</c> і на
    /// <c>Fixed</c> відмовляє законно, тож наскрізний шлях «додати рядок»
    /// на типовій таблиці будівника не пройшов би взагалі.
    ///
    /// ⛔ Для такого тесту потрібен саме <c>Mixed</c>, а не <c>Dynamic</c>:
    /// будівник заводить <c>RowDef</c>-и, а <c>TableDef.AddRow</c> у режимі
    /// <c>Dynamic</c> кидає виняток — тобто <c>MetadataCache</c> не зміг би
    /// зібрати знімок структури, і кожен запит до такого документа падав би
    /// у <c>500</c> ще до перевірки прав.
    /// </remarks>
    public async Task<TestDocument> BuildAsync(
        int periodKey = 202601,
        int columnCount = 3,
        int rowCount = 4,
        TableRowMode rowMode = TableRowMode.Fixed,
        TableLayoutKind layoutKind = TableLayoutKind.PerPeriodInstance,
        int monthColumns = 0,
        CancellationToken ct = default)
    {
        // Унікальний суфікс: коди в cfg.* унікальні, а фікстура на колекцію
        // одна, тож два тести підряд інакше зіткнулися б на UQ_Template_Code.
        var tag = Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var key = new PeriodKey(periodKey);

        await using var db = CreateContext();

        var template = new Template(EcrCode.Create($"TPL{tag}"), Name($"Template {tag}"), 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, $"1.0.0.{tag}", 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SHEET{tag}"), Name($"Sheet {tag}"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TBL{tag}"), Name($"Table {tag}"), 1,
            layoutKind, rowMode);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var columns = new List<ColumnDef>();
        for (var i = 1; i <= columnCount; i++)
        {
            // Перша колонка текстова, решта числові — щоб тести могли
            // перевіряти обидва типи значень, не будуючи другу таблицю.
            var type = i == 1 ? CellDataType.String : CellDataType.Decimal;
            var column = new ColumnDef(table.Id, EcrCode.Create($"C{i}_{tag}"), Name($"Col {i}"), i, type);
            columns.Add(column);
            db.ColumnDefs.Add(column);
        }

        // Q-17: місячні стовпці M1..M{monthColumns} (IsMonthColumn, MonthNumber = m) після звичайних.
        var monthColumnDefs = new List<ColumnDef>();
        for (var m = 1; m <= monthColumns; m++)
        {
            var column = new ColumnDef(
                table.Id, EcrCode.Create($"M{m}_{tag}"), Name($"Month {m}"), columnCount + m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            monthColumnDefs.Add(column);
            columns.Add(column);
            db.ColumnDefs.Add(column);
        }

        var rowDefs = new List<RowDef>();
        for (var i = 1; i <= rowCount; i++)
        {
            var rowDef = new RowDef(
                table.Id, RowKey.Create($"R{i}_{tag}"), i, Name($"Row {i}"), RowKind.Item);
            rowDefs.Add(rowDef);
            db.RowDefs.Add(rowDef);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync(ct).ConfigureAwait(false);

        var project = new Project(
            EcrCode.Create($"PRJ{tag}"), Name($"Project {tag}"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            // IANA, а не `Central Asia Standard Time`: Windows-ідентифікатор
            // домен більше не приймає (директива ПК-1 №06 §3). Обраний
            // `Asia/Atyrau` (майданчик NCOC, +05:00 з 2004 року без переходів):
            // його зсув не залежить від редакції бази поясів машини. Раніше
            // тут стояв `Asia/Almaty`, зсув якого змінився 2024-03-01 (+06:00
            // → +05:00), тож межі в тестах залежали від того, чи оновлено
            // tzdata/Windows (F-4).
            version.Id, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var period = new Period(
            project.Id, key, (byte)key.Sequence,
            new DateOnly(key.Year, key.Sequence, 1),
            new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence)));
        db.Periods.Add(period);

        var document = new Document(project.Id, $"DOC{tag}", 1, now);
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Id для TableInstance і TableRow — із SEQUENCE, а не IDENTITY:
        // значення потрібні ДО вставки (B02 §2.3). Саме так їх бере і
        // BulkCellLoader, тож фікстура повторює бойовий шлях.
        var loader = new BulkCellLoader(connectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, ct).ConfigureAwait(false);
        // ⚠ rowCount = 0 — динамічна таблиця без жодного рядка (HSE301 A5b): резервувати нічого,
        // а `ReserveIdsAsync` нуль відхиляє.
        var firstRowId = rowCount > 0
            ? await loader.ReserveIdsAsync("doc.TableRowSeq", rowCount, ct).ConfigureAwait(false)
            : 0;

        var instance = new TableInstance(key, instanceId, document.Id, table.Id, now);
        db.TableInstances.Add(instance);

        var rows = new List<TableRow>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = new TableRow(key, firstRowId + i, instanceId, RowKey.Create($"R{i + 1}_{tag}"), i + 1, now);
            rows.Add(row);
            db.TableRows.Add(row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new TestDocument(
            TemplateId: template.Id,
            TemplateVersionId: version.Id,
            SheetDefId: sheet.Id,
            SheetCode: sheet.Code,
            TableDefId: table.Id,
            ColumnDefIds: [.. columns.Select(c => c.Id)],
            RowDefIds: [.. rowDefs.Select(r => r.Id)],
            ProjectId: project.Id,
            PeriodKey: key,
            DocumentId: document.Id,
            TableInstanceId: instanceId,
            RowIds: [.. rows.Select(r => r.Id)])
        {
            MonthColumnDefIds = [.. monthColumnDefs.Select(c => c.Id)],
        };
    }

    /// <summary>
    /// Q-17: ще одна таблиця на тому самому аркуші документа — з екземпляром за період документа і рядками з
    /// ключами, унікальними в таблиці. Для тестів пакетного читання «багато таблиць у документі».
    /// </summary>
    /// <param name="document">Ланцюг, створений <see cref="BuildAsync"/>.</param>
    /// <param name="layoutKind">Розкладка нової таблиці.</param>
    /// <param name="monthColumns">Скільки місячних стовпців M1..Mn (0 — жодного).</param>
    /// <param name="columnCount">Скільки звичайних колонок.</param>
    /// <param name="rowCount">Скільки рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Копія <paramref name="document"/> з таблицею, колонками, екземпляром і рядками нової таблиці.</returns>
    public async Task<TestDocument> AddTableAsync(
        TestDocument document,
        TableLayoutKind layoutKind,
        int monthColumns,
        int columnCount = 2,
        int rowCount = 3,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var tag = Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        await using var db = CreateContext();

        var table = new TableDef(
            document.SheetDefId, EcrCode.Create($"TBX{tag}"), Name($"Table {tag}"), 1000 + int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture),
            layoutKind, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var columns = new List<ColumnDef>();
        for (var i = 1; i <= columnCount; i++)
        {
            var type = i == 1 ? CellDataType.String : CellDataType.Decimal;
            columns.Add(new ColumnDef(table.Id, EcrCode.Create($"C{i}_{tag}"), Name($"Col {i}"), i, type));
        }

        var monthDefs = new List<ColumnDef>();
        for (var m = 1; m <= monthColumns; m++)
        {
            var column = new ColumnDef(
                table.Id, EcrCode.Create($"M{m}_{tag}"), Name($"Month {m}"), columnCount + m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            monthDefs.Add(column);
            columns.Add(column);
        }

        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var loader = new BulkCellLoader(connectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, ct).ConfigureAwait(false);
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", rowCount, ct).ConfigureAwait(false);

        db.TableInstances.Add(new TableInstance(document.PeriodKey, instanceId, document.DocumentId, table.Id, now));
        var rows = new List<TableRow>();
        for (var i = 0; i < rowCount; i++)
        {
            var row = new TableRow(document.PeriodKey, firstRowId + i, instanceId, RowKey.Create($"R{i + 1}_{tag}"), i + 1, now);
            rows.Add(row);
            db.TableRows.Add(row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return document with
        {
            TableDefId = table.Id,
            ColumnDefIds = [.. columns.Select(c => c.Id)],
            RowDefIds = [],
            TableInstanceId = instanceId,
            RowIds = [.. rows.Select(r => r.Id)],
            MonthColumnDefIds = [.. monthDefs.Select(c => c.Id)],
        };
    }

    /// <summary>
    /// Q-17: додає проєкту документа рядок періоду <paramref name="periodKey"/> (календар), якщо його ще немає.
    /// </summary>
    /// <param name="document">Ланцюг, створений <see cref="BuildAsync"/>.</param>
    /// <param name="periodKey">Місячний ключ періоду.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task AddPeriodAsync(TestDocument document, PeriodKey periodKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var db = CreateContext();
        if (await db.Periods
                .AnyAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey.Value, ct)
                .ConfigureAwait(false))
        {
            return;
        }

        db.Periods.Add(new Period(
            document.ProjectId, periodKey, (byte)periodKey.Sequence,
            new DateOnly(periodKey.Year, periodKey.Sequence, 1),
            new DateOnly(periodKey.Year, periodKey.Sequence, DateTime.DaysInMonth(periodKey.Year, periodKey.Sequence))));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Q-17: екземпляр тієї самої таблиці документа за інший період — з рядками, що мають ті самі <c>RowKey</c> і
    /// <c>Ordinal</c>, що й екземпляр <see cref="TestDocument.TableInstanceId"/>. Рядок періоду в календарі
    /// додається, якщо його немає.
    /// </summary>
    /// <param name="document">Ланцюг, створений <see cref="BuildAsync"/>.</param>
    /// <param name="periodKey">Період нового екземпляра.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<TestPeriodInstance> AddPeriodInstanceAsync(
        TestDocument document, PeriodKey periodKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await AddPeriodAsync(document, periodKey, ct).ConfigureAwait(false);

        await using var db = CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var template = await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == document.PeriodKey.Value && r.TableInstanceId == document.TableInstanceId)
            .OrderBy(r => r.Ordinal)
            .Select(r => new { r.RowKeyValue, r.Ordinal })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var loader = new BulkCellLoader(connectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, ct).ConfigureAwait(false);
        var firstRowId = template.Count > 0
            ? await loader.ReserveIdsAsync("doc.TableRowSeq", template.Count, ct).ConfigureAwait(false)
            : 0;

        db.TableInstances.Add(new TableInstance(periodKey, instanceId, document.DocumentId, document.TableDefId, now));

        var rowIds = new List<long>();
        for (var i = 0; i < template.Count; i++)
        {
            var id = firstRowId + i;
            rowIds.Add(id);
            db.TableRows.Add(new TableRow(
                periodKey, id, instanceId, RowKey.Create(template[i].RowKeyValue), template[i].Ordinal, now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new TestPeriodInstance(periodKey, instanceId, rowIds, [.. template.Select(r => r.RowKeyValue)]);
    }

    /// <summary>
    /// Переводить період документа зі <c>Scheduled</c> (стан будівника за замовчуванням) в <c>Open</c>.
    /// </summary>
    /// <param name="document">Ланцюг, створений <see cref="BuildAsync"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ D-441 (AN-128): <c>EditRules.CanSubmit</c> відхиляє ще не відкритий період
    /// (<c>PeriodNotOpenYet</c>), тож тест, що подає аркуш через справжню службу доступу, відкриває
    /// період явно. Проєкт будівника — чернетка, і рішення бере ЗБЕРЕЖЕНИЙ стан, не дати.
    /// </remarks>
    public async Task OpenPeriodAsync(TestDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var db = CreateContext();
        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value, ct)
            .ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Дає користувачеві нову роль із грантом <paramref name="level"/> на проєкт (роль + призначення + грант).
    /// </summary>
    /// <remarks>
    /// ⛔ P2-2 (аудит 25.10): надання учасника діє, лише поки ДЕЛЕГАТОР має право його видавати (Manage проєкту або
    /// автор з Write). Тест, що кладе передачу прямо в базу, мусить дати делегатору права цим методом — інакше
    /// надання не діє, і тест перевіряв би вже не те.
    /// </remarks>
    public async Task GrantProjectAsync(int userId, int projectId, GrantLevel level, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        var role = new Ecr.Domain.Entities.Security.Role(
            EcrCode.Create($"TKG_{Guid.NewGuid():N}"[..20]), Name("Test project grant"));
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, userId, null));
        db.ResourceGrants.Add(new Ecr.Domain.Entities.Security.ResourceGrant(role.Id, ResourceKind.Project, projectId, level));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Контекст на ту саму базу — для тестів, яким потрібен EF напряму.</summary>
    public EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}

/// <summary>Ідентифікатори створеного ланцюга.</summary>
public sealed record TestDocument(
    int TemplateId,
    int TemplateVersionId,
    int SheetDefId,
    string SheetCode,
    int TableDefId,
    IReadOnlyList<int> ColumnDefIds,
    IReadOnlyList<int> RowDefIds,
    int ProjectId,
    PeriodKey PeriodKey,
    long DocumentId,
    long TableInstanceId,
    IReadOnlyList<long> RowIds)
{
    /// <summary>Q-17: ідентифікатори місячних стовпців M1..Mn (порожньо, якщо їх не просили).</summary>
    public IReadOnlyList<int> MonthColumnDefIds { get; init; } = [];
}

/// <summary>Q-17: екземпляр таблиці документа за інший період (<see cref="TestDocumentBuilder.AddPeriodInstanceAsync"/>).</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="RowIds">Рядки в порядку <c>RowKeys</c>.</param>
/// <param name="RowKeys">Ключі рядків — ті самі, що в екземплярі-зразку.</param>
public sealed record TestPeriodInstance(
    PeriodKey PeriodKey, long TableInstanceId, IReadOnlyList<long> RowIds, IReadOnlyList<string> RowKeys);
