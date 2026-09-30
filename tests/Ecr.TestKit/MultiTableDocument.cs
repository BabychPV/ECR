using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.TestKit;

/// <summary>Таблиця, додана до документа <see cref="TestDocumentBuilder"/>.</summary>
/// <param name="SheetDefId">Аркуш, якому належить таблиця.</param>
/// <param name="TableDefId">Таблиця версії шаблону.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="ColumnDefIds">Колонки (усі числові).</param>
/// <param name="ColumnCodes">Коди колонок у тому самому порядку.</param>
/// <param name="TableInstanceId">Екземпляр таблиці за період документа.</param>
/// <param name="RowIds">Рядки екземпляра.</param>
/// <param name="RowKeys">Ключі рядків у тому самому порядку.</param>
public sealed record ExtraTable(
    int SheetDefId,
    int TableDefId,
    string TableCode,
    IReadOnlyList<int> ColumnDefIds,
    IReadOnlyList<string> ColumnCodes,
    long TableInstanceId,
    IReadOnlyList<long> RowIds,
    IReadOnlyList<string> RowKeys);

/// <summary>
/// Документ із КІЛЬКОМА аркушами й таблицями — поверх ланцюга
/// <see cref="TestDocumentBuilder"/>, який дає рівно одну таблицю.
/// </summary>
/// <remarks>
/// ⚠ Потрібен тестам пакетних рішень про доступ (P8): різниця між «один зріз»
/// і «книга на N таблиць» видна лише на документі, де таблиць справді N, а
/// аркушів більше одного (стан подання — на аркуш).
///
/// ⚠ Так само доменними конструкторами через EF, як і сам будівник: ланцюг із
/// сирих <c>INSERT</c> пройшов би і на зламаному мапінгу.
/// </remarks>
public static class MultiTableDocument
{
    private static int _counter;

    /// <summary>Додає нові аркуші з таблицями до версії шаблону й документа <paramref name="doc"/>.</summary>
    /// <param name="builder">Будівник, яким створено <paramref name="doc"/>.</param>
    /// <param name="doc">Документ.</param>
    /// <param name="tablesPerNewSheet">Скільки таблиць на кожному новому аркуші.</param>
    /// <param name="columnCount">Колонок у кожній таблиці.</param>
    /// <param name="rowCount">Рядків у кожному екземплярі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Додані таблиці в порядку створення.</returns>
    public static async Task<IReadOnlyList<ExtraTable>> AddTablesAsync(
        TestDocumentBuilder builder,
        TestDocument doc,
        IReadOnlyList<int> tablesPerNewSheet,
        int columnCount = 2,
        int rowCount = 2,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(tablesPerNewSheet);

        var tag = "M" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture)
                  + "_" + Guid.NewGuid().ToString("N")[..8];
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        await using var db = builder.CreateContext();
        var loader = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

        var result = new List<ExtraTable>();
        var sheetOrder = 100;

        for (var s = 0; s < tablesPerNewSheet.Count; s++)
        {
            var sheet = new SheetDef(
                doc.TemplateVersionId, EcrCode.Create($"SH{s}_{tag}"), Name($"Sheet {s} {tag}"), ++sheetOrder);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            for (var t = 0; t < tablesPerNewSheet[s]; t++)
            {
                var tableCode = $"TB{s}_{t}_{tag}";
                var table = new TableDef(
                    sheet.Id, EcrCode.Create(tableCode), Name(tableCode), t + 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                var columns = new List<ColumnDef>();
                for (var c = 1; c <= columnCount; c++)
                {
                    var column = new ColumnDef(
                        table.Id, EcrCode.Create($"C{c}_{s}_{t}_{tag}"), Name($"Col {c}"), c, CellDataType.Decimal);
                    columns.Add(column);
                    db.ColumnDefs.Add(column);
                }

                var rowKeys = new List<string>();
                for (var r = 1; r <= rowCount; r++)
                {
                    var rowKey = $"R{r}_{s}_{t}_{tag}";
                    rowKeys.Add(rowKey);
                    db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(rowKey), r, Name($"Row {r}"), RowKind.Item));
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, ct).ConfigureAwait(false);
                var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", rowCount, ct).ConfigureAwait(false);

                db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, table.Id, now));

                var rowIds = new List<long>();
                for (var r = 0; r < rowCount; r++)
                {
                    rowIds.Add(firstRowId + r);
                    db.TableRows.Add(new TableRow(
                        doc.PeriodKey, firstRowId + r, instanceId, RowKey.Create(rowKeys[r]), r + 1, now));
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                result.Add(new ExtraTable(
                    sheet.Id, table.Id, tableCode,
                    [.. columns.Select(c => c.Id)], [.. columns.Select(c => c.Code)],
                    instanceId, rowIds, rowKeys));
            }
        }

        return result;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
