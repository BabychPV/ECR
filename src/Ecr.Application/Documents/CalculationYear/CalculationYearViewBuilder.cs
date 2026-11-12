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

    /// <summary>Будує огляд усього документа зі значеннями (експорт).</summary>
    /// <param name="profile">Профіль читача (права вже перевірено).</param>
    /// <param name="documentId">Документ (видимість уже перевірено).</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Скасування.</param>
    public Task<CalculationYearViewDto> BuildAsync(AccessProfile profile, long documentId, int year, CancellationToken ct)
        => BuildAsync(profile, documentId, year, CalculationYearScope.Full, ct);

    /// <summary>Будує огляд у межах <paramref name="extent"/> (увесь документ, зведення без значень або одна таблиця).</summary>
    /// <param name="profile">Профіль читача (права вже перевірено).</param>
    /// <param name="documentId">Документ (видимість уже перевірено).</param>
    /// <param name="year">Рік.</param>
    /// <param name="extent">Що читати.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Межі читання однакові в усіх режимах: той самий будівник, ті самі звуження; режим змінює лише ОБСЯГ читання
    /// (одна таблиця, без значень), а не правила. Стан прогону місяця для вузького читача рахується з ключів чисел
    /// УСЬОГО документа (<see cref="CalculationYearFacts.ResultKeys"/>) — однаково для зведення і будь-якої сторінки.
    /// </remarks>
    public async Task<CalculationYearViewDto> BuildAsync(
        AccessProfile profile, long documentId, int year, CalculationYearScope extent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(extent);

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

        var readableInstances = instances
            .Where(i => monthScopes.TryGetValue(i.PeriodKey, out var s) && s.CanReadTable(i.TableDefId))
            .Select(i => i.TableInstanceId)
            .ToHashSet();

        // Екземпляри, з яких читаються рядки, підписи й комірки формул: таблиця сторінки або увесь документ.
        var read = extent.Table is { } slice
            ? instances.Where(i => i.TableDefId == slice.TableDefId && i.SheetCopyId == slice.SheetCopyId).ToList()
            : [.. instances];
        var readDefs = read.Select(i => i.TableDefId).ToHashSet();
        var labelColumnIds = extent.Labels
            ? labels.Where(kv => readDefs.Contains(kv.Key.TableDefId)).SelectMany(kv => kv.Value.Ids).ToHashSet()
            : [];
        var keyValues = keys.Select(k => k.Value).ToList();

        var facts = await reader
            .ReadYearAsync(
                new CalculationYearReadRequest(
                    documentId, keyValues, [.. read.Select(i => i.TableInstanceId)], labelColumnIds,
                    IncludeRows: !extent.Aggregate, SliceResults: extent.Table is not null, Aggregate: extent.Aggregate),
                ct)
            .ConfigureAwait(false);

        // Числа до D-401 (без екземпляра) шукають господаря серед УСІХ читаних таблиць місяця — у режимі однієї таблиці
        // чи зведення рядків інших таблиць у фактах немає, тож вони дочитуються (лише коли такі числа є).
        IReadOnlyList<YearRow>? hostRows = null;
        if ((extent.Aggregate || extent.Table is not null) && facts.Results.Any(r => r.TableInstanceId is null))
        {
            hostRows = await reader.ReadRowsAsync(keyValues, readableInstances, ct).ConfigureAwait(false);
        }

        var narrowed = DocumentSheetVisibility.HasRestrictions(profile);
        IReadOnlyList<YearResult> results = facts.Results;
        IReadOnlySet<int>? visibleRunMonths = null;
        if (narrowed)
        {
            var visible = await NarrowingAsync(instances, monthScopes, ct).ConfigureAwait(false);
            results = [.. results.Where(r => visible(r.PeriodKey, r.TableInstanceId, r.MethodologyVersionId, r.OutputCode))];

            // ⛔ Вузькому читачу стан прогону місяця віддається лише там, де він бачить бодай одне число методології
            // (як панель одного місяця: усе сховане — порожньо, без часу прогону). Рахується по ключах УСЬОГО
            // документа, а не по прочитаній таблиці — інакше сторінка й зведення розійшлися б у станах місяців.
            visibleRunMonths = facts.ResultKeys
                .Where(k => visible(k.PeriodKey, k.TableInstanceId, k.MethodologyVersionId, k.OutputCode))
                .Select(k => k.PeriodKey)
                .ToHashSet();
        }

        var formulaCells = facts.FormulaCells.Where(c => monthScopes[c.PeriodKey].CanReadColumn(c.ColumnDefId)).ToList();
        var months = await MonthsAsync(documentId, keys, states, facts.Runs, visibleRunMonths, ct).ConfigureAwait(false);
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        var context = new Pivot(
            documentId, year, keys, months, instances, readableInstances, snapshots, tableDefs, columns, labels, facts,
            hostRows ?? facts.Rows, catalog);
        return context.Build(results, formulaCells, narrowed);
    }

    /// <summary>
    /// Правило звуження чисел методологій (те саме, що в <see cref="GetCalculationResultsHandler"/>), по місяцях: вихід
    /// видно, лише коли читаються ВСІ колонки, до яких він прив'язаний у таблиці, що дала число; без прив'язки — ні.
    /// </summary>
    /// <remarks>
    /// Рішення залежить лише від (період, таблиця екземпляра, версія, вихід) — тож рахується раз на такий ключ, а не на
    /// кожне число (раніше — перебір прив'язок на кожне з сотень тисяч чисел).
    /// </remarks>
    private async Task<Func<int, long?, int, string, bool>> NarrowingAsync(
        IReadOnlyList<TableInstanceRef> instances,
        IReadOnlyDictionary<int, DocumentReadScope> monthScopes,
        CancellationToken ct)
    {
        var tableIds = monthScopes.Values.SelectMany(s => s.TableIds()).Distinct().ToList();
        var bindings = await methodologies.GetColumnResultBindingsAsync(tableIds, ct).ConfigureAwait(false);
        var defByInstance = instances.ToDictionary(i => i.TableInstanceId, i => i.TableDefId);
        var decided = new Dictionary<(int Period, int? TableDefId, int Version, string Output), bool>();

        return (period, instanceId, version, output) =>
        {
            int? tableDefId = instanceId is { } id && defByInstance.TryGetValue(id, out var def) ? def : null;
            var key = (period, tableDefId, version, output);
            if (decided.TryGetValue(key, out var known))
            {
                return known;
            }

            var scope = monthScopes[period];
            var matching = bindings.Where(b =>
                string.Equals(b.OutputCode, output, StringComparison.OrdinalIgnoreCase)
                && b.VersionIds.Contains(version)
                && (tableDefId is not { } t || b.TableDefId == t)).ToList();
            var visible = matching.Count > 0 && matching.All(b => scope.CanReadColumn(b.ColumnDefId));
            decided.Add(key, visible);
            return visible;
        };
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
        IReadOnlyList<YearRow> hostRows,
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
            // ⚠ Ключ виходу — кортеж, а рядок-ідентифікатор складається раз на вихід, а не на кожне число місяця (сотні
            // тисяч рядків на документ Land).
            var lines = new Dictionary<(int Table, string? Row, int Methodology, string Output, long? Substance, int Unit), Line>();

            // Версія методології рядка — останнього місяця з числом.
            foreach (var r in results.OrderBy(r => r.PeriodKey))
            {
                if (!versions.TryGetValue(r.MethodologyVersionId, out var version))
                {
                    continue;
                }

                var table = TableOf(r.TableInstanceId, r.PeriodKey, r.SourceRowKey);
                var meth = MethodologyOf(version);
                var key = (table, r.SourceRowKey, version.MethodologyId, r.OutputCode, r.SubstanceEntryId, r.UnitId);
                if (!lines.TryGetValue(key, out var line))
                {
                    var eq = EquipmentOf(table, r.SourceRowKey);
                    int? substance = r.SubstanceEntryId is { } sid ? SubstanceOf(sid, entries) : null;
                    var unit = UnitOf(r.UnitId);
                    var id = string.Join('|', "m", TableKey(table), r.SourceRowKey ?? "", version.MethodologyId.ToString(CultureInfo.InvariantCulture),
                        r.OutputCode, r.SubstanceEntryId?.ToString(CultureInfo.InvariantCulture) ?? "", r.UnitId.ToString(CultureInfo.InvariantCulture));
                    line = new Line(id, eq, meth, r.OutputCode, null, substance, unit,
                        (version.MethodologyCode, 0, r.OutputCode, substance is { } s ? substances[s].Code : ""));
                    lines.Add(key, line);
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

                // Ключ формули: «методологія» — мінус колонка (ідентифікатори методологій додатні), без одиниці в ключі.
                var key = (table, (string?)c.RowKey, -column.Id, column.Code, (long?)null, -1);
                if (!lines.TryGetValue(key, out var line))
                {
                    var eq = EquipmentOf(table, c.RowKey);
                    var col = ColumnOf(column);
                    int? unit = column.UnitId is { } uid ? UnitOf(uid) : null;
                    var id = string.Join('|', "f", TableKey(table), c.RowKey, column.Id.ToString(CultureInfo.InvariantCulture));

                    // Формули шаблону — після методологій рядка, у порядку колонок.
                    line = new Line(id, eq, null, column.Code, col, null, unit, ("￿", column.Ordinal, column.Code, ""));
                    lines.Add(key, line);
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
                // Останній ключ — ідентифікатор виходу: порядок повний, незалежний від порядку фактів із бази, тож
                // відбиток курсора сторінки (CalculationYearCursor) між запитами той самий.
                .ThenBy(l => l.Id, StringComparer.Ordinal)
                .Select(l => l.ToDto(noValue, l.Unit is { } u && unitList[u].Additive))
                .ToList();

            return new CalculationYearViewDto(
                documentId, year, months, tables, equipment, methodologyList, substances, unitList, columnList, rows, narrowed);
        }

        /// <summary>
        /// Стійкий ключ таблиці в ідентифікаторі виходу (визначення + копія, а не індекс у переліку): ідентифікатор того
        /// самого виходу однаковий у зведенні, на сторінці таблиці й в експорті.
        /// </summary>
        private string TableKey(int table) => CalculationYearPages.KeyOf(tables[table]);

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
            foreach (var row in hostRows)
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

        public string Id => id;

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
                id, equipment, methodology, output, column, substance, unit, Array.ConvertAll(values, Trim), new string(states),
                additive && included > 0 ? Trim(total) : null, additive ? included : 0);
        }

        /// <summary>
        /// Без хвостових нулів масштабу <c>decimal(34,16)</c>: «2.5», а не «2.5000000000000000» — значення те саме (D-30
        /// рядком без втрати знаків), а відповідь у кілька разів менша.
        /// </summary>
        private static decimal? Trim(decimal? value) => value / 1.0000000000000000000000000000m;
    }
}
