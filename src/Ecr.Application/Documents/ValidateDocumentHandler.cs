using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Повна валідація документа перед поданням (ФВ-5.1).</summary>
public sealed class ValidateDocumentHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IMetadataCache metadata,
    IValidationResultStore results,
    ValidationEngine engine,
    Domain.Abstractions.IClock clock,
    IUnitOfWork uow)
{
    /// <summary>Виконує валідацію всіх аркушів документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Повідомлення трьох рівнів; наявність <c>Error</c> блокує <c>Submit</c>.</returns>
    public async Task<IReadOnlyList<ValidationMessage>> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⚠ Екземпляри таблиць беруться ОДНИМ запитом, а не по аркушах:
        // бюджет — 3 с p95 на весь документ, і похід у базу на кожну з
        // сотні таблиць у нього не вкладається.
        var instances = await rowStore
            .GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        var messages = new List<ValidationMessage>();

        foreach (var instance in instances)
        {
            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var table = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .FirstOrDefault(t => t.Id == instance.TableDefId);

            if (table is null || table.ValidationRules.Count == 0)
            {
                continue;
            }

            var cells = await cellStore
                .ReadSliceAsync(instance.TableInstanceId, ct).ConfigureAwait(false);

            var context = new SliceContext(table, cells);

            // Рівні 1 (рядок), 2 (таблиця) і 3 (документ) — окремими проходами:
            // правило рівня таблиці бачить усі рядки, і запускати його на
            // кожному рядку означало б повторити те саме порушення N разів.
            foreach (var scope in ScopeLevels)
            {
                messages.AddRange(engine.ValidateScope(scope, table.ValidationRules, context));
            }
        }

        var summary = new ValidationSummary(
            documentId,
            periodKey.Value,
            clock.UtcNow,
            messages.Count(m => m.Severity == ValidationSeverity.Error),
            messages.Count(m => m.Severity == ValidationSeverity.Warning),
            messages.Count(m => m.Severity == ValidationSeverity.Info),
            JsonSerializer.Serialize(messages));

        // ⚠ Підсумок ЗБЕРІГАЄТЬСЯ: подання питає в нього, чи є незакриті
        // помилки (ФВ-5.19). Якби воно щоразу перевалідовувало документ,
        // «подати» коштувало б стільки ж, скільки «перевірити», і на великому
        // документі це були б ті самі три секунди в найгірший момент.
        await results.SaveAsync(summary, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return messages;
    }

    /// <summary>Рівні правил: 1 рядок, 2 таблиця, 3 документ.</summary>
    private static readonly byte[] ScopeLevels = [1, 2, 3];

    /// <summary>Значення зрізу як джерело для виразів правил.</summary>
    private sealed class SliceContext(TableDef table, IReadOnlyList<CellRecord> cells) : IValidationContext
    {
        private readonly Dictionary<(long Row, int Column), object?> _values =
            cells.ToDictionary(
                c => (c.Address.TableRowId, c.Address.ColumnDefId),
                c => (object?)(c.Value.ValueNumeric ?? (object?)c.Value.ValueString));

        /// <inheritdoc />
        public object? GetCell(string columnCode)
        {
            // Рівень таблиці й документа не мають «поточного рядка», тому
            // однойменний метод повертає перше значення колонки: правило,
            // написане без рядка, і має на увазі саме таблицю цілком.
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);
            return column is null
                ? null
                : _values.FirstOrDefault(v => v.Key.Column == column.Id).Value;
        }

        /// <inheritdoc />
        public object? GetCell(string rowKey, string columnCode)
        {
            var row = table.Rows.FirstOrDefault(r => r.RowKeyValue == rowKey);
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);

            return row is null || column is null
                ? null
                : _values.GetValueOrDefault((row.Id, column.Id));
        }
    }
}
