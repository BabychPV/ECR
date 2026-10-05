using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Validation;

/// <summary>
/// Прогін правил рівнів рядка, таблиці й документа над зрізом однієї таблиці.
/// </summary>
/// <remarks>
/// ⛔ Спільний код, а не копія в двох обробниках, і причина не в економії
/// рядків: `ФВ-5.4` вимагає, щоб ці самі рівні блокували `Submit`, — тобто
/// подання зобов'язане рахувати РІВНО те саме, що показує кнопка
/// «Перевірити». Дві реалізації того самого правила розійшлися б, і
/// розійшлися б мовчки: документ, який щойно показав «помилок немає», не
/// подавався б (директива №09 `W8` п.5, `S-28`).
/// </remarks>
internal static class TableValidation
{
    /// <summary>Рівні правил: 2 таблиця, 3 документ. Рівень рядка йде окремо.</summary>
    private static readonly byte[] AboveRowLevels = [2, 3];

    /// <summary>
    /// Виконує всі правила таблиці і повертає повідомлення з адресами.
    /// </summary>
    /// <param name="engine">Двигун правил.</param>
    /// <param name="table">Опис таблиці зі знімка версії.</param>
    /// <param name="cells">Значення зрізу.</param>
    /// <param name="rowIds">Рядки екземпляра: <c>RowKey</c> → <c>TableRow.Id</c>.</param>
    /// <param name="headers">
    /// Значення шапки документа, ключовані кодом поля — для <c>HDR.X</c> у
    /// правилах усіх трьох рівнів; порожній словник — прогін без шапки.
    /// </param>
    /// <param name="language">Мова запиту — <see cref="ValidationEngine.ValidateScope"/> (B-11).</param>
    /// <remarks>
    /// ⛔ Рівень РЯДКА виконується ПО РЯДКАХ, а кожне повідомлення отримує
    /// свій <c>RowKey</c> (директива №09 `W8` п.3, `S-19`). Доти всі три рівні
    /// йшли одним проходом через <see cref="ValidationEngine.ValidateScope"/>,
    /// який ставить <c>rowKey: null</c> завжди: список порушень приходив без
    /// жодної адреси — тобто повідомляв, ЩО не так, і не повідомляв, ДЕ.
    /// Виправити за таким списком не можна нічого.
    ///
    /// ⚠ Рівень таблиці й документа адреси рядка не мають за визначенням і
    /// лишаються з <c>null</c> — це не пропуск, а їхня природа.
    /// </remarks>
    /// <param name="registries">Сховище довідників — для знімка <c>REGFIELD</c> (D16-04).</param>
    /// <param name="snapshot">Версія шаблону таблиці — для резолвінгу посилань правил.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<ValidationMessage>> RunAsync(
        ValidationEngine engine,
        IRegistryStore registries,
        TemplateVersionSnapshot snapshot,
        TableDef table,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue> headers,
        string language,
        CancellationToken ct)
    {
        var messages = new List<ValidationMessage>();

        if (table.ValidationRules.Count == 0)
        {
            return messages;
        }

        // ⛔ D16-04: без знімка `REGFIELD` у правилі давав `#REF`, правило
        // деградувало у Warning `ECR-VAL-RULE`, і Error-правило не блокувало
        // подання. «Перевірити» і подання йдуть сюди обидва — знімок один.
        var registryFields = await LoadRegistryFieldsAsync(engine, registries, snapshot, table, cells, ct)
            .ConfigureAwait(false);

        var slice = new SliceContext(table, cells, rowIds);

        foreach (var rowKey in rowIds.Keys.Order(StringComparer.Ordinal))
        {
            messages.AddRange(engine
                .ValidateScope(scope: 1, table.ValidationRules, slice.ForRow(rowKey), headers, language, registryFields)
                .Select(m => m with { RowKey = rowKey }));
        }

        // Правило рівня таблиці бачить усі рядки, і запускати його на кожному
        // означало б повторити те саме порушення N разів.
        foreach (var scope in AboveRowLevels)
        {
            messages.AddRange(engine.ValidateScope(scope, table.ValidationRules, slice, headers, language, registryFields));
        }

        return messages;
    }

    /// <summary>
    /// Знімок полів довідника, які правила таблиці читають через <c>REGFIELD</c>,
    /// для записів, на які показують Lookup-комірки зрізу.
    /// </summary>
    /// <remarks>
    /// ⚠ Завантаження — спільний <see cref="Registries.RegistryFieldSnapshotLoader"/>,
    /// той самий, що в перерахунку формул; тут лише визначається, ЩО просити.
    /// Правила без <c>REGFIELD</c> — нуль звернень до довідника.
    ///
    /// ⚠ Беруться записи з УСІХ рядків зрізу, а не лише з тих, які правило
    /// адресує явно: правило рівня рядка читає свій рядок, таблиці — перший,
    /// а <c>[Рядок].[Колонка]</c> — названий; усе це підмножина зрізу.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>>?>
        LoadRegistryFieldsAsync(
            ValidationEngine engine,
            IRegistryStore registries,
            TemplateVersionSnapshot snapshot,
            TableDef table,
            IReadOnlyList<CellRecord> cells,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);

        var reads = engine.RegistryFieldReads(table, snapshot);
        if (reads.Count == 0)
        {
            return null;
        }

        var requests = new List<Registries.RegistryFieldRequest>();

        foreach (var (columnDefId, fieldCode) in reads)
        {
            var column = table.Columns.FirstOrDefault(c => c.Id == columnDefId && !c.IsDeleted);
            if (column?.LookupRegistryDefId is not { } registryDefId)
            {
                continue;
            }

            foreach (var cell in cells)
            {
                if (cell.Address.ColumnDefId == columnDefId
                    && cell.Value is { IsEmpty: false, ValueRegistryEntryId: { } entryId })
                {
                    requests.Add(new Registries.RegistryFieldRequest(entryId, registryDefId, fieldCode));
                }
            }
        }

        return await Registries.RegistryFieldSnapshotLoader
            .LoadAsync(registries, requests, ct).ConfigureAwait(false);
    }

    /// <summary>Значення зрізу як джерело для виразів правил.</summary>
    /// <remarks>
    /// ⛔ Рядок шукається серед РЯДКІВ ЕКЗЕМПЛЯРА (<c>doc.TableRow</c>), а не
    /// серед описів <c>cfg.RowDef</c>. Раніше тут стояло
    /// <c>table.Rows.FirstOrDefault(...).Id</c> — тобто <c>RowDef.Id</c>, — і
    /// ним індексувався словник, ключований <c>TableRow.Id</c>. Це різні
    /// послідовності: правило рівня рядка читало комірку ЧУЖОГО рядка, а
    /// частіше не знаходило нічого і мовчки бачило <c>null</c>.
    /// </remarks>
    private sealed class SliceContext(
        TableDef table,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds) : IValidationContext
    {
        // ⛔ Розгортання — СПІЛЬНЕ (`CellValueMapping.ToRuleValue`), а не
        // ad-hoc `ValueNumeric ?? ValueString` (аудит 2026-09-16, §3.2). Стара
        // форма давала `null` для КОЖНОЇ Bool- і Date-колонки, тож правила
        // рівня рядка/таблиці/документа — двигун за `ValidateDocumentHandler`,
        // тобто за повною перевіркою перед Submit (ФВ-5.1) — читали порожнечу
        // незалежно від реального значення. Рівно той дефект, від якого
        // застерігає власний коментар цього файлу: «подання зобов'язане
        // рахувати РІВНО те саме, що показує кнопка "Перевірити"».
        private readonly Dictionary<(long Row, int Column), object?> _values =
            cells.ToDictionary(
                c => (c.Address.TableRowId, c.Address.ColumnDefId),
                c => Ecr.Expressions.Evaluation.CellValueMapping.ToRuleValue(c.Value));

        private string? _currentRowKey;

        /// <summary>Той самий зріз, наведений на конкретний рядок.</summary>
        public SliceContext ForRow(string rowKey)
            => new SliceContext(table, cells, rowIds) { _currentRowKey = rowKey };

        /// <inheritdoc />
        public object? GetCell(string columnCode)
        {
            // Наведений на рядок контекст читає СВІЙ рядок. Без цього правило
            // рівня рядка бачило б перше-ліпше значення колонки в таблиці.
            if (_currentRowKey is { } current)
            {
                return GetCell(current, columnCode);
            }

            // Рівень таблиці й документа «поточного рядка» не має, тому
            // однойменний метод віддає перше значення колонки: правило,
            // написане без рядка, і має на увазі саме таблицю цілком.
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);
            return column is null
                ? null
                : _values.FirstOrDefault(v => v.Key.Column == column.Id).Value;
        }

        /// <inheritdoc />
        public object? GetCell(string rowKey, string columnCode)
        {
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);

            return column is null || !rowIds.TryGetValue(rowKey, out var rowId)
                ? null
                : _values.GetValueOrDefault((rowId, column.Id));
        }
    }

    /// <summary>
    /// Обов'язкова колонка (<c>ColumnDef.IsRequired</c>) без заповненого
    /// значення для кожного існуючого рядка екземпляра.
    /// </summary>
    /// <remarks>
    /// ⚠ «Заповнене» перевіряється як <c>!Value.IsEmpty</c>, а не як «є запис
    /// у зрізі»: явна порожнеча (R-B4) теж матеріалізується, і рядок, у якому
    /// обов'язкову клітинку колись занулили, має блокувати подання так само,
    /// як рядок, де її взагалі не було. `PatchCellsHandler`/`ColumnDef.ValidateValue`
    /// вже забороняють ЗАПИСАТИ такий стан явно (`ECR-CELL-0422`) — ця
    /// перевірка ловить рядок, що прийшов до цього стану БЕЗ жодного запису
    /// (найчастіше — просто ніхто не торкався клітинки).
    ///
    /// ⛔ L6-09: спільна для подання (<c>SubmitSheetHandler</c>) і «Перевірити»
    /// (<c>ValidateDocumentHandler</c>) — доти жила лише в поданні, і «Перевірити»
    /// казала «зауважень немає» там, де подання відмовляло саме через це.
    /// </remarks>
    public static List<ValidationMessage> MissingRequiredColumnMessages(
        Domain.Entities.Configuration.TableDef table,
        List<Domain.Entities.Configuration.ColumnDef> requiredColumns,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds,
        string language = "en")
    {
        if (requiredColumns.Count == 0 || rowIds.Count == 0)
        {
            return [];
        }

        var filled = cells
            .Where(c => !c.Value.IsEmpty)
            .Select(c => (c.Address.TableRowId, c.Address.ColumnDefId))
            .ToHashSet();

        var messages = new List<ValidationMessage>();

        // ⚠ Порядок рядків — явний (`TableRow.Id`), а не порядок словника:
        // той дорівнює порядку рядків із БД без `ORDER BY`. Див. `OrderAsOnScreen`.
        foreach (var (rowKey, rowId) in rowIds.OrderBy(kv => kv.Value))
        {
            foreach (var column in requiredColumns)
            {
                if (filled.Contains((rowId, column.Id)))
                {
                    continue;
                }

                // T2-04/T2-07: ключ + підстановки (читання локалізує мовою читача); текст — мовою запиту.
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["column"] = column.Code.ToString() };
                messages.Add(new ValidationMessage(
                    Domain.Enums.ValidationSeverity.Error,
                    "ECR-CELL-0422",
                    ValidationMessageTemplates.Render(ValidationMessageTemplates.ColumnRequired, language, parameters),
                    table.Id,
                    rowKey,
                    column.Code,
                    BlocksSave: true,
                    MessageKey: ValidationMessageTemplates.ColumnRequired,
                    Params: parameters));
            }
        }

        return messages;
    }
}
