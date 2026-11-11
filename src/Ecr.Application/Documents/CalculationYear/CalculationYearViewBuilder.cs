// src/Ecr.Application/Documents/CalculationYear/CalculationYearViewBuilder.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// Будує огляд «Calculation results — усі місяці року» для профілю читача (CALC-RESULTS.md §1, §3).
/// </summary>
/// <remarks>
/// ⚠ Права тут НЕ перевіряються — це робить викликач (<see cref="GetCalculationYearHandler"/> для запиту,
/// задача експорту для замовника). Тут — лише межі читання: рядки схованих аркушів, таблиць і колонок не потрапляють
/// у відповідь узагалі (ні число, ні підпис рядка).
/// <para>
/// ⛔ Числа методологій звужуються тим самим правилом, що й <see cref="GetCalculationResultsHandler"/>: вихід
/// видно, лише коли читаються ВСІ колонки, до яких він прив'язаний у таблиці, що дала число; без прив'язки —
/// не видно (закрито за замовчуванням). Формули шаблону — за колонкою комірки в межах читання ЇЇ місяця.
/// </para>
/// </remarks>
public sealed class CalculationYearViewBuilder(
    ICalculationYearReader reader,
    IRowStore rows,
    IMetadataCache metadata,
    IMethodologyStore methodologies,
    IPeriodStore periods,
    IUnitCatalog units,
    IAccessDecisionService access)
{
    /// <summary>Розмірності, для яких сума за рік має сенс. Решта (і невідоме) — не сумується.</summary>
    public static readonly IReadOnlySet<string> AdditiveDimensions =
        new HashSet<string>(["Mass", "Energy", "Volume", "StdVolume", "Amount"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Будує огляд.</summary>
    /// <param name="profile">Профіль читача (права вже перевірено).</param>
    /// <param name="documentId">Документ (видимість уже перевірено).</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<CalculationYearViewDto> BuildAsync(
        AccessProfile profile, long documentId, int year, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var keys = Enumerable.Range(1, 12).Select(m => PeriodKey.Create(year, m)).ToList();
        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.DocumentNotFound(documentId);
        var states = (await periods.GetPeriodStatesAsync(projectId, null, ct).ConfigureAwait(false))
            .Where(p => p.PeriodKey / 100 == year)
            .ToDictionary(p => p.PeriodKey, p => p.State);

        var instances = new List<TableInstanceRef>();
        foreach (var key in keys)
        {
            instances.AddRange(await rows.GetTableInstancesAsync(documentId, key, ct).ConfigureAwait(false));
        }

        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        foreach (var versionId in instances.Select(i => i.TemplateVersionId).Distinct())
        {
            snapshots[versionId] = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);
        }

        var tableDefs = snapshots.Values
            .SelectMany(s => s.Sheets.SelectMany(sheet => sheet.Tables.Select(t => (Sheet: sheet, Table: t))))
            .GroupBy(x => x.Table.Id)
            .ToDictionary(g => g.Key, g => g.Last());
        var columns = snapshots.Values.SelectMany(s => s.ColumnsById.Values).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);

        // ⛔ Межі читання — по МІСЯЦЯХ: область без періоду не бачить заборон, звужених періодами (AppliesTo без
        // періоду їх пропускає), тож і колонки підпису, і читаність таблиць рахуються в межах місяця комірки.
        var monthScopes = keys.ToDictionary(k => k.Value, scope.InPeriod);
        var labels = new Dictionary<(int TableDefId, int PeriodKey), LabelColumns>();
        foreach (var (tableId, def) in tableDefs)
        {
            foreach (var (periodKey, monthScope) in monthScopes)
            {
                labels[(tableId, periodKey)] = LabelColumns.Of(def.Table, monthScope);
            }
        }

        var labelColumnIds = labels.Values.SelectMany(l => l.Ids).ToHashSet();
        var readableInstances = instances
            .Where(i => monthScopes.TryGetValue(i.PeriodKey, out var s) && s.CanReadTable(i.TableDefId))
            .Select(i => i.TableInstanceId)
            .ToHashSet();

        var facts = await reader
            .ReadYearAsync(documentId, [.. keys.Select(k => k.Value)], [.. instances.Select(i => i.TableInstanceId)], labelColumnIds, ct)
            .ConfigureAwait(false);

        var narrowed = DocumentSheetVisibility.HasRestrictions(profile);
        var results = narrowed ? await NarrowAsync(facts.Results, instances, monthScopes, ct).ConfigureAwait(false) : facts.Results;
        var formulaCells = facts.FormulaCells.Where(c => monthScopes[c.PeriodKey].CanReadColumn(c.ColumnDefId)).ToList();

        // ⛔ Вузькому читачу стан прогону місяця віддається лише там, де він бачить бодай одне число методології
        // (як панель одного місяця: усе сховане — порожньо, без часу прогону).
        IReadOnlySet<int>? visibleRunMonths = narrowed ? results.Select(r => r.PeriodKey).ToHashSet() : null;
        var months = await MonthsAsync(documentId, keys, states, facts.Runs, visibleRunMonths, ct).ConfigureAwait(false);
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        var context = new Pivot(
            documentId, year, keys, months, instances, readableInstances, snapshots, tableDefs, columns, labels, facts, catalog);
        return context.Build(results, formulaCells, narrowed);
    }

    /// <summary>Правило звуження чисел методологій (те саме, що в <see cref="GetCalculationResultsHandler"/>), по місяцях.</summary>
    private async Task<IReadOnlyList<YearResult>> NarrowAsync(
        IReadOnlyList<YearResult> results,
        IReadOnlyList<TableInstanceRef> instances,
        IReadOnlyDictionary<int, DocumentReadScope> monthScopes,
        CancellationToken ct)
    {
        if (results.Count == 0)
        {
            return results;
        }

        var tableIds = monthScopes.Values.SelectMany(s => s.TableIds()).Distinct().ToList();
        var bindings = await methodologies.GetColumnResultBindingsAsync(tableIds, ct).ConfigureAwait(false);
        var defByInstance = instances.ToDictionary(i => i.TableInstanceId, i => i.TableDefId);

        return [.. results.Where(r =>
        {
            var scope = monthScopes[r.PeriodKey];
            var matching = bindings.Where(b =>
                string.Equals(b.OutputCode, r.OutputCode, StringComparison.OrdinalIgnoreCase)
                && b.VersionIds.Contains(r.MethodologyVersionId)
                && (r.TableInstanceId is not { } instanceId
                    || !defByInstance.TryGetValue(instanceId, out var tableDefId)
                    || b.TableDefId == tableDefId)).ToList();
            return matching.Count > 0 && matching.All(b => scope.CanReadColumn(b.ColumnDefId));
        })];
    }

    private async Task<IReadOnlyList<CalculationYearMonthDto>> MonthsAsync(
        long documentId,
        List<PeriodKey> keys,
        Dictionary<int, PeriodState> states,
        IReadOnlyList<YearRun> runs,
        IReadOnlySet<int>? visibleRunMonths,
        CancellationToken ct)
    {
        var runOf = runs.ToDictionary(r => r.PeriodKey);
        var months = new List<CalculationYearMonthDto>(12);
        for (var m = 1; m <= 12; m++)
        {
            var key = keys[m - 1].Value;
            var periodState = states.TryGetValue(key, out var s)
                ? s switch
                {
                    PeriodState.Closed => "closed",
                    PeriodState.Open or PeriodState.Grace => "open",
                    _ => "notOpened",
                }
                : "notOpened";

            string runState;
            DateTime? calculatedAt = null;
            // ⚠ «noRun» — і тоді, коли прогін був, але не дав документу жодного числа (прогін місяця береться з чисел).
            if (runOf.TryGetValue(key, out var run) && (visibleRunMonths is null || visibleRunMonths.Contains(key)))
            {
                // ⛔ Н-2: свіжість і час прогону рахуються по ВСЬОМУ документу — вузькому читачу вони не віддаються
                // (як у панелі одного місяця: CalculatedAt = null).
                var narrowed = visibleRunMonths is not null;
                calculatedAt = narrowed ? null : run.FinishedAt;
                var stale = !narrowed && (await methodologies
                    .GetCalculationFreshnessAsync(documentId, key, tableDefIds: null, ct)
                    .ConfigureAwait(false)).IsStale;
                runState = stale ? "outOfDate" : "current";
            }
            else
            {
                runState = periodState == "notOpened" ? "notOpened" : "noRun";
            }

            months.Add(new CalculationYearMonthDto(m, key, periodState, runState, calculatedAt));
        }

        return months;
    }

    /// <summary>Колонки підпису динамічного рядка таблиці.</summary>
    /// <param name="Code">Колонка бізнес-ключа (IDCode): перша за порядком.</param>
    /// <param name="Name">Колонка назви: перша текстова або довідникова, що не є кодом.</param>
    internal sealed record LabelColumns(ColumnDef? Code, ColumnDef? Name)
    {
        public IEnumerable<int> Ids => new[] { Code?.Id, Name?.Id }.Where(id => id is not null).Select(id => id!.Value);

        /// <summary>
        /// ⚠ Окремої ознаки «колонка-підпис» у шаблоні немає: назва — перша видима текстова/довідникова колонка, код —
        /// перша колонка бізнес-ключа. Колонка, яку читач не бачить, підписом не стає.
        /// </summary>
        public static LabelColumns Of(TableDef table, DocumentReadScope scope)
        {
            var visible = table.Columns
                .Where(c => !c.IsDeleted && !c.IsHidden && scope.CanReadColumn(c.Id))
                .OrderBy(c => c.Ordinal)
                .ToList();
            var code = visible.FirstOrDefault(c => c.IsBusinessKey);
            var name = visible.FirstOrDefault(c =>
                c.Id != code?.Id && (c.DataType == CellDataType.String || c.DataType == CellDataType.Lookup));
            return new LabelColumns(code, name);
        }
    }

    /// <summary>Зведення фактів у рядки огляду.</summary>
    private sealed class Pivot(
        long documentId,
        int year,
        IReadOnlyList<PeriodKey> keys,
        IReadOnlyList<CalculationYearMonthDto> months,
        IReadOnlyList<TableInstanceRef> instances,
        IReadOnlySet<long> readableInstances,
        IReadOnlyDictionary<int, TemplateVersionSnapshot> snapshots,
        IReadOnlyDictionary<int, (SheetDef Sheet, TableDef Table)> tableDefs,
        IReadOnlyDictionary<int, ColumnDef> columns,
        IReadOnlyDictionary<(int TableDefId, int PeriodKey), LabelColumns> labels,
        CalculationYearFacts facts,
        UnitCatalogSnapshot catalog)
    {
        private readonly Dictionary<long, TableInstanceRef> instanceById = instances.ToDictionary(i => i.TableInstanceId);
        private Dictionary<(int PeriodKey, string RowKey), TableInstanceRef?>? legacyHosts;
        private readonly Dictionary<int, int> monthOfKey = keys.Select((k, i) => (k.Value, i)).ToDictionary(x => x.Value, x => x.i);
        private readonly Dictionary<(int TableDefId, long? CopyId), int> tableIndex = [];
        private readonly List<CalculationYearTableDto> tables = [];
        private readonly List<(int SheetOrdinal, int TableOrdinal)> tableOrder = [];
        private readonly Dictionary<(int Table, string? RowKey), int> equipmentIndex = [];
        private readonly List<CalculationYearEquipmentDto> equipment = [];
        private readonly List<int> equipmentOrder = [];
        private readonly Dictionary<int, int> methodologyIndex = [];
        private readonly List<CalculationYearMethodologyDto> methodologyList = [];
        private readonly Dictionary<long, int> substanceIndex = [];
        private readonly List<CalculationYearSubstanceDto> substances = [];
        private readonly Dictionary<int, int> unitIndex = [];
        private readonly List<CalculationYearUnitDto> unitList = [];
        private readonly Dictionary<int, int> columnIndex = [];
        private readonly List<CalculationYearColumnDto> columnList = [];

        public CalculationYearViewDto Build(
            IReadOnlyList<YearResult> results, IReadOnlyList<YearFormulaCell> formulaCells, bool narrowed)
        {
            var versions = facts.Versions.ToDictionary(v => v.MethodologyVersionId);
            var entries = facts.Entries.ToDictionary(e => e.Id);
            var lines = new Dictionary<string, Line>(StringComparer.Ordinal);

            // Версія методології рядка — останнього місяця з числом.
            foreach (var r in results.OrderBy(r => r.PeriodKey))
            {
                if (!versions.TryGetValue(r.MethodologyVersionId, out var version))
                {
                    continue;
                }

                var table = TableOf(r.TableInstanceId, r.PeriodKey, r.SourceRowKey);
                var eq = EquipmentOf(table, r.SourceRowKey);
                var meth = MethodologyOf(version);
                int? substance = r.SubstanceEntryId is { } sid ? SubstanceOf(sid, entries) : null;
                var unit = UnitOf(r.UnitId);
                var id = string.Join('|', "m", TableKey(table), r.SourceRowKey ?? "", version.MethodologyId.ToString(CultureInfo.InvariantCulture),
                    r.OutputCode, r.SubstanceEntryId?.ToString(CultureInfo.InvariantCulture) ?? "", r.UnitId.ToString(CultureInfo.InvariantCulture));
                if (!lines.TryGetValue(id, out var line))
                {
                    line = new Line(id, eq, meth, r.OutputCode, null, substance, unit,
                        (version.MethodologyCode, 0, r.OutputCode, substance is { } s ? substances[s].Code : ""));
                    lines.Add(id, line);
                }

                line.Add(monthOfKey[r.PeriodKey], r.Value);
            }

            foreach (var c in formulaCells)
            {
                if (!columns.TryGetValue(c.ColumnDefId, out var column))
                {
                    continue;
                }

                var table = TableOf(c.TableInstanceId, c.PeriodKey, c.RowKey);
                var eq = EquipmentOf(table, c.RowKey);
                var col = ColumnOf(column);
                int? unit = column.UnitId is { } uid ? UnitOf(uid) : null;
                var id = string.Join('|', "f", TableKey(table), c.RowKey, column.Id.ToString(CultureInfo.InvariantCulture));
                if (!lines.TryGetValue(id, out var line))
                {
                    // Формули шаблону — після методологій рядка, у порядку колонок.
                    line = new Line(id, eq, null, column.Code, col, null, unit, ("￿", column.Ordinal, column.Code, ""));
                    lines.Add(id, line);
                }

                line.Add(monthOfKey[c.PeriodKey], c.Value);
            }

            FillLabels(entries);

            var noValue = months.Select(m => m.RunState is "noRun" or "notOpened" ? 'n' : 'a').ToArray();
            var rows = lines.Values
                .OrderBy(l => tableOrder[equipment[l.Equipment].Table].SheetOrdinal)
                .ThenBy(l => tables[equipment[l.Equipment].Table].SheetCopyId ?? 0)
                .ThenBy(l => tableOrder[equipment[l.Equipment].Table].TableOrdinal)
                .ThenBy(l => equipmentOrder[l.Equipment])
                .ThenBy(l => equipment[l.Equipment].RowKey, StringComparer.Ordinal)
                .ThenBy(l => l.Sort.Methodology, StringComparer.Ordinal)
                .ThenBy(l => l.Sort.ColumnOrdinal)
                .ThenBy(l => l.Sort.Output, StringComparer.Ordinal)
                .ThenBy(l => l.Sort.Substance, StringComparer.Ordinal)
                .Select(l => l.ToDto(noValue, l.Unit is { } u && unitList[u].Additive))
                .ToList();

            return new CalculationYearViewDto(
                documentId, year, months, tables, equipment, methodologyList, substances, unitList, columnList, rows, narrowed);
        }

        private static string TableKey(int table) => table.ToString(CultureInfo.InvariantCulture);

        private int TableOf(long? instanceId, int periodKey, string? rowKey)
        {
            TableInstanceRef? instance = null;
            if (instanceId is { } id)
            {
                instanceById.TryGetValue(id, out instance);
            }
            else if (rowKey is not null)
            {
                // Число до D-401 не знає свого екземпляра: таблиця — ЄДИНА читана того ж місяця, де є такий рядок.
                // ⛔ Схована таблиця кандидатом не є (інакше її аркуш і назва потрапили б у відповідь); кілька
                // кандидатів чи жодного — число без таблиці (рядок 0), а не здогад.
                LegacyHosts().TryGetValue((periodKey, rowKey), out instance);
            }

            var key = instance is null ? (0, null) : (instance.TableDefId, instance.SheetCopyId);
            if (tableIndex.TryGetValue(key, out var index))
            {
                return index;
            }

            index = tables.Count;
            tableIndex.Add(key, index);
            if (instance is not null && tableDefs.TryGetValue(instance.TableDefId, out var def))
            {
                tables.Add(new CalculationYearTableDto(
                    def.Sheet.Id, def.Sheet.Code, def.Sheet.NameL10n, instance.SheetCopyId, def.Table.Id, def.Table.Code, def.Table.NameL10n));
                tableOrder.Add((def.Sheet.Ordinal, def.Table.Ordinal));
            }
            else
            {
                tables.Add(new CalculationYearTableDto(0, "", new LocalizedText(), null, 0, "", new LocalizedText()));
                tableOrder.Add((int.MaxValue, int.MaxValue));
            }

            return index;
        }

        /// <summary>
        /// Господар рядка за (місяць, ключ рядка) серед ЧИТАНИХ екземплярів; <c>null</c> — кілька кандидатів.
        /// Один прохід по рядках замість пошуку на кожне число.
        /// </summary>
        private Dictionary<(int PeriodKey, string RowKey), TableInstanceRef?> LegacyHosts()
        {
            if (legacyHosts is not null)
            {
                return legacyHosts;
            }

            legacyHosts = [];
            foreach (var row in facts.Rows)
            {
                if (!readableInstances.Contains(row.TableInstanceId)
                    || !instanceById.TryGetValue(row.TableInstanceId, out var instance))
                {
                    continue;
                }

                var key = (instance.PeriodKey, row.RowKey);
                if (!legacyHosts.TryGetValue(key, out var known))
                {
                    legacyHosts.Add(key, instance);
                }
                else if (known is not null && known.TableInstanceId != instance.TableInstanceId)
                {
                    legacyHosts[key] = null;
                }
            }

            return legacyHosts;
        }

        private int EquipmentOf(int table, string? rowKey)
        {
            if (equipmentIndex.TryGetValue((table, rowKey), out var index))
            {
                return index;
            }

            index = equipment.Count;
            equipmentIndex.Add((table, rowKey), index);
            equipment.Add(new CalculationYearEquipmentDto(table, rowKey, null, null, null));
            equipmentOrder.Add(rowKey is null ? -1 : int.MaxValue);
            return index;
        }

        /// <summary>
        /// Підписи рядків: фіксований — з шаблону, динамічний — з колонок назви й коду (останній місяць, де колонка
        /// підпису читається в межах ЦЬОГО місяця).
        /// </summary>
        private void FillLabels(Dictionary<long, YearEntry> entries)
        {
            var defOfTable = tables.Select(t => t.TableDefId).ToList();
            var tableOfInstance = instances.ToDictionary(i => i.TableInstanceId, i => (i.TableDefId, i.SheetCopyId));
            var rowsByKey = facts.Rows
                .Where(r => tableOfInstance.ContainsKey(r.TableInstanceId))
                .GroupBy(r => (tableOfInstance[r.TableInstanceId], r.RowKey))
                .ToDictionary(g => g.Key, g => g.Last());

            // ⛔ Комірка підпису береться лише тоді, коли її колонка — колонка підпису (назви чи коду) в межах читання
            // місяця комірки: заборона, звужена періодами, ховає підпис саме в тих місяцях.
            char? RoleOf(YearLabelCell c)
            {
                var tableDefId = tableOfInstance[c.TableInstanceId].TableDefId;
                if (!labels.TryGetValue((tableDefId, c.PeriodKey), out var label))
                {
                    return null;
                }

                return label.Name?.Id == c.ColumnDefId ? 'n' : label.Code?.Id == c.ColumnDefId ? 'c' : null;
            }

            var cellsByKey = facts.LabelCells
                .Where(c => tableOfInstance.ContainsKey(c.TableInstanceId))
                .Select(c => (Cell: c, Role: RoleOf(c)))
                .Where(x => x.Role is not null)
                .OrderBy(x => x.Cell.PeriodKey)
                .GroupBy(x => (tableOfInstance[x.Cell.TableInstanceId], x.Cell.RowKey, x.Role!.Value))
                .ToDictionary(g => g.Key, g => g.Last().Cell);
            var copyOfTable = tableIndex.ToDictionary(kv => kv.Value, kv => kv.Key);

            for (var i = 0; i < equipment.Count; i++)
            {
                var e = equipment[i];
                if (e.RowKey is null || defOfTable[e.Table] == 0)
                {
                    continue;
                }

                var tableKey = copyOfTable[e.Table];
                if (rowsByKey.TryGetValue((tableKey, e.RowKey), out var row))
                {
                    equipmentOrder[i] = row.Ordinal;
                }

                var fixedLabel = snapshots.Values
                    .Select(s => s.RowsByKey.TryGetValue((tableKey.TableDefId, e.RowKey), out var def) ? def : null)
                    .LastOrDefault(def => def is not null);
                if (fixedLabel is not null)
                {
                    equipment[i] = e with { LabelL10n = fixedLabel.LabelL10n };
                    continue;
                }

                string? name = null, code = null;
                LocalizedText? nameL10n = null;
                if (cellsByKey.TryGetValue((tableKey, e.RowKey, 'n'), out var nameCell))
                {
                    name = nameCell.Text;
                    if (nameCell.EntryId is { } entryId && entries.TryGetValue(entryId, out var entry))
                    {
                        nameL10n = entry.DisplayL10n;
                        code = entry.Code;
                    }
                }

                if (cellsByKey.TryGetValue((tableKey, e.RowKey, 'c'), out var codeCell))
                {
                    code = codeCell.Text
                           ?? (codeCell.EntryId is { } id && entries.TryGetValue(id, out var codeEntry) ? codeEntry.Code : null)
                           ?? codeCell.Number?.ToString(CultureInfo.InvariantCulture);
                }

                equipment[i] = e with { LabelL10n = nameL10n, Name = name, IdCode = code };
            }
        }

        private int MethodologyOf(YearMethodologyVersion version)
        {
            if (methodologyIndex.TryGetValue(version.MethodologyId, out var index))
            {
                // Перелік іде від раннього місяця до пізнього: версія — останнього.
                methodologyList[index] = methodologyList[index] with { Version = version.Version };
                return index;
            }

            index = methodologyList.Count;
            methodologyIndex.Add(version.MethodologyId, index);
            methodologyList.Add(new CalculationYearMethodologyDto(version.MethodologyId, version.MethodologyCode, version.NameL10n, version.Version));
            return index;
        }

        private int SubstanceOf(long id, Dictionary<long, YearEntry> entries)
        {
            if (substanceIndex.TryGetValue(id, out var index))
            {
                return index;
            }

            index = substances.Count;
            substanceIndex.Add(id, index);
            substances.Add(entries.TryGetValue(id, out var e)
                ? new CalculationYearSubstanceDto(id, e.Code, e.DisplayL10n)
                : new CalculationYearSubstanceDto(id, id.ToString(CultureInfo.InvariantCulture), new LocalizedText()));
            return index;
        }

        private int UnitOf(int id)
        {
            if (unitIndex.TryGetValue(id, out var index))
            {
                return index;
            }

            index = unitList.Count;
            unitIndex.Add(id, index);
            var unit = catalog.Units.Values.FirstOrDefault(u => u.Id == id);
            unitList.Add(unit is null
                ? new CalculationYearUnitDto(id, id.ToString(CultureInfo.InvariantCulture), null, "", Additive: false)
                : new CalculationYearUnitDto(
                    id,
                    unit.Code,
                    unit.SymbolL10n is null ? null : new LocalizedText(unit.SymbolL10n),
                    unit.DimensionCode,
                    AdditiveDimensions.Contains(unit.DimensionCode)));
            return index;
        }

        private int ColumnOf(ColumnDef column)
        {
            if (columnIndex.TryGetValue(column.Id, out var index))
            {
                return index;
            }

            index = columnList.Count;
            columnIndex.Add(column.Id, index);
            columnList.Add(new CalculationYearColumnDto(column.Id, column.Code, column.HeaderL10n));
            return index;
        }
    }

    /// <summary>Вихід у процесі зведення.</summary>
    private sealed class Line(
        string id, int equipment, int? methodology, string output, int? column, int? substance, int? unit,
        (string Methodology, int ColumnOrdinal, string Output, string Substance) sort)
    {
        private readonly decimal?[] values = new decimal?[12];

        public int Equipment => equipment;

        public int? Unit => unit;

        public (string Methodology, int ColumnOrdinal, string Output, string Substance) Sort => sort;

        /// <summary>Кілька чисел одного виходу в місяці (кілька екземплярів-копій не розрізнено) — сумуються, як у накладенні.</summary>
        public void Add(int month, decimal value) => values[month] = (values[month] ?? 0m) + value;

        public CalculationYearRowDto ToDto(char[] noValue, bool additive)
        {
            var states = new char[12];
            decimal total = 0m;
            var included = 0;
            for (var m = 0; m < 12; m++)
            {
                if (values[m] is { } v)
                {
                    states[m] = 'v';
                    total += v;
                    included++;
                }
                else
                {
                    states[m] = noValue[m];
                }
            }

            return new CalculationYearRowDto(
                id, equipment, methodology, output, column, substance, unit, values, new string(states),
                additive && included > 0 ? total : null, additive ? included : 0);
        }
    }
}
