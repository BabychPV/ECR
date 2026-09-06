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
    /// <param name="ct">Токен скасування.</param>
    public async Task<TestDocument> BuildAsync(
        int periodKey = 202601, int columnCount = 3, int rowCount = 4, CancellationToken ct = default)
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
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
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
            // домен більше не приймає (директива ПК-1 №06 §3). Обраний саме
            // `Asia/Almaty` — це те, у що сама платформа переводить колишнє
            // значення (виміряно `TryConvertWindowsIdToIanaId`), тож жодна
            // порахована в тестах межа не зсунулася ні на секунду.
            version.Id, PeriodKind.Monthly, policyId, "Asia/Almaty");
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
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", rowCount, ct).ConfigureAwait(false);

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
            TableDefId: table.Id,
            ColumnDefIds: [.. columns.Select(c => c.Id)],
            RowDefIds: [.. rowDefs.Select(r => r.Id)],
            ProjectId: project.Id,
            PeriodKey: key,
            DocumentId: document.Id,
            TableInstanceId: instanceId,
            RowIds: [.. rows.Select(r => r.Id)]);
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
    int TableDefId,
    IReadOnlyList<int> ColumnDefIds,
    IReadOnlyList<int> RowDefIds,
    int ProjectId,
    PeriodKey PeriodKey,
    long DocumentId,
    long TableInstanceId,
    IReadOnlyList<long> RowIds);
