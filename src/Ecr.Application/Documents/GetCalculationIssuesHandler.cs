// src/Ecr.Application/Documents/GetCalculationIssuesHandler.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Поточні проблеми розрахунку документа за період і копію аркуша: рядки без правила методології (AN-69).
/// </summary>
/// <remarks>
/// ⚠ Право — <c>Document.View</c> (аркуш показує проблему поряд з даними), невидимий документ — той самий 404, що й
/// в інших читань документа. Рядки схованих таблиць (власна чи аркуша заборона) у відповіді ВІДСУТНІ: ключ рядка
/// схованої таблиці не повинен витікати.
///
/// ✎ UAT 08.11 b3: разом із проблемами — що приймають правила таблиці (<see cref="CalculationIssueHints"/>) і поточні
/// значення рядка в тих колонках. Без джерела підказки (конструктор у старих тестах) — лише перелік, як доти.
/// </remarks>
public sealed class GetCalculationIssuesHandler(
    ICalculationIssueStore issues,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IRowStore? rowStore = null,
    ICellStore? cellStore = null,
    IMetadataCache? metadata = null,
    IPeriodStore? periods = null,
    IMethodologyStore? methodologies = null,
    IColumnPathMapper? columnMapper = null,
    IFormulaEngine? formulaEngine = null,
    ICalculationResultStore? results = null,

    // ✎ C-1 (D-475): група «Помилки формул»; `null` (старі тести) — групи немає.
    IFormulaErrorStore? formulaErrors = null)
{
    private readonly CalculationIssueHints? hints =
        rowStore is null || cellStore is null || metadata is null || periods is null || methodologies is null
            ? null
            : new CalculationIssueHints(rowStore, cellStore, metadata, periods, methodologies, columnMapper);

    // ✎ 10.11 (doc 95): причина відхилення правилом категорії — правило й колонки, що їх треба заповнити.
    private readonly CalculationRejectionReasons? reasons =
        rowStore is null || cellStore is null || metadata is null || periods is null || methodologies is null
        || formulaEngine is null
            ? null
            : new CalculationRejectionReasons(
                rowStore, cellStore, metadata, periods, methodologies, formulaEngine, columnMapper, results);

    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Віддає поточні проблеми.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CalculationIssuesDto> HandleAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, а не 403.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var rows = await issues.ListAsync(documentId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
        IReadOnlyList<FormulaErrorView> errorRows = formulaErrors is null
            ? []
            : await formulaErrors.ListAsync(documentId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
        if (rows.Count == 0 && errorRows.Count == 0)
        {
            return new CalculationIssuesDto([]);
        }

        var key = new PeriodKey(periodKey);
        var readable = key.IsValid
            ? (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key)
            : await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);

        var visible = rows.Where(r => readable.CanReadTable(r.TableDefId)).ToList();
        var names = await LookupNamesAsync(errorRows, ct).ConfigureAwait(false);
        var (formulaErrorItems, visibleErrorCount) = FormulaErrorsOf(errorRows, readable, names, currentUser.Language);

        // Підказка «що приймають правила» — лише рядкам без правила: відхиленому рядку (і рядку без входів, AN-164)
        // правило вже підійшло.
        var unmatched = visible.Where(r => !HasRuleReason(r.Kind)).ToList();
        var built = hints is null || unmatched.Count == 0 || !key.IsValid
            ? CalculationIssueHintSet.Empty
            : await hints.BuildAsync(documentId, key, sheetCopyId, unmatched, readable.CanReadColumn, ct)
                .ConfigureAwait(false);

        var rejected = visible.Where(r => HasRuleReason(r.Kind)).ToList();
        var why = reasons is null || rejected.Count == 0 || !key.IsValid
            ? new Dictionary<(int, string, int?), CalculationRejectionReason>()
            : await reasons.BuildAsync(documentId, key, sheetCopyId, rejected, readable.CanReadColumn, ct)
                .ConfigureAwait(false);

        return new CalculationIssuesDto(
            [
                .. visible.Select(r =>
                {
                    var reason = HasRuleReason(r.Kind)
                        ? why.GetValueOrDefault((r.TableDefId, r.RowKey, r.MethodologyId))
                        : null;

                    return new CalculationIssueDto(
                        r.TableCode,
                        r.SheetCopyId,
                        r.RowNumber,
                        r.RowKey,
                        r.Kind,
                        r.MethodologyCode,
                        r.DetectedAt,
                        reason is null ? built.CurrentValues.GetValueOrDefault((r.TableDefId, r.RowKey)) : null,
                        reason?.RuleCode,
                        reason is null || reason.Columns.Count == 0
                            ? null
                            : [.. reason.Columns.Select(c => new CalculationIssueColumnDto(c.Code, c.Name))]);
                }),
            ],
            built.Tables.Count == 0 ? null : built.Tables,
            formulaErrorItems,
            formulaErrorItems is null ? null : visibleErrorCount);
    }

    /// <summary>
    /// ✎ C-1 (D-475): група «Помилки формул» — лише видимі комірки; закриті входи, формула з закритим входом і закрите
    /// джерело не віддаються (S6).
    /// </summary>
    private static (IReadOnlyList<FormulaErrorIssueDto>? Items, int Total) FormulaErrorsOf(
        IReadOnlyList<FormulaErrorView> rows, DocumentReadScope readable,
        IReadOnlyDictionary<(int ColumnDefId, long EntryId), LocalizedText> names, string language)
    {
        // ✎ B78: значення довідникової колонки — назвою запису мовою користувача («Diesel», а не «37»).
        string? ValueOf(Recalculation.FormulaErrorInputRecord input)
            => input.Value is { } raw
               && long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
               && names.TryGetValue((input.ColumnDefId, id), out var name)
               && (name.Get(language) ?? name.Get("en")) is { Length: > 0 } text
                ? text
                : input.Value;

        var visible = rows
            .Where(v => readable.CanReadTable(v.Row.TableDefId) && readable.CanReadColumn(v.Row.ColumnDefId))
            .ToList();
        if (visible.Count == 0)
        {
            return (null, 0);
        }

        bool CanSee(int tableDefId, int columnDefId) => readable.CanReadTable(tableDefId) && readable.CanReadColumn(columnDefId);

        // ✎ C-1 (координатор 12.11, зручність): ОДИН запис на корінь помилки з кількістю залежних — «не оновлено» може
        // бути в кожному рядку великої таблиці, і перелік по комірці був би непридатним; залежні позначає сітка.
        // Корінь — власна помилка (Error); ключ кореня — (період, таблиця, рядок, колонка), той самий, що в джерелі
        // Blocked. Залежні, чий корінь у переліку не показаний (закритий, інший період чи копія), — один запис на джерело.
        static (int, int, string, int) RootKey(FormulaErrorRow row)
            => (row.PeriodKey, row.TableDefId, row.RowKey, row.ColumnDefId);

        static (int, int, string, int) SourceKey(FormulaErrorRow row)
            => (row.SourcePeriodKey ?? row.PeriodKey, row.SourceTableDefId ?? 0, row.SourceRowKey ?? string.Empty,
                row.SourceColumnDefId ?? 0);

        var roots = visible.Where(v => v.Row.Kind == Domain.Entities.Calculations.FormulaError.ErrorKind).ToList();
        var rootKeys = roots.Select(r => RootKey(r.Row)).ToHashSet();
        var dependents = visible
            .Where(v => v.Row.Kind != Domain.Entities.Calculations.FormulaError.ErrorKind)
            .GroupBy(v => SourceKey(v.Row))
            .ToDictionary(g => g.Key, g => g.ToList());

        var entries = new List<(FormulaErrorView View, int Dependents)>();
        entries.AddRange(roots.Select(r => (r, dependents.TryGetValue(RootKey(r.Row), out var list) ? list.Count : 0)));
        entries.AddRange(dependents
            .Where(pair => !rootKeys.Contains(pair.Key))
            .Select(pair => (pair.Value[0], pair.Value.Count)));

        // ⚠ Payload: стеля записів (коренів мало; записів «невидимих джерел» — не більше джерел).
        var shown = entries.Take(CalculationIssuesDto.MaxFormulaErrors);

        return ([.. shown.Select(entry =>
        {
            var (v, dependentCount) = entry;
            var inputs = Recalculation.FormulaErrorInputs.Parse(v.Row.InputsJson);
            var hidden = inputs.Any(i => !CanSee(i.TableDefId, i.ColumnDefId));
            var source = v.Row.SourceTableDefId is { } st && v.Row.SourceColumnDefId is { } sc && CanSee(st, sc)
                         && v.SourceTableCode is { } stc && v.SourceColumnCode is { } scc
                ? new Dto.FormulaErrorSourceDto(
                    stc, v.Row.SourceRowKey ?? string.Empty, scc,
                    v.Row.SourcePeriodKey == v.Row.PeriodKey ? null : v.Row.SourcePeriodKey)
                : null;
            var isError = v.Row.Kind == Domain.Entities.Calculations.FormulaError.ErrorKind;
            return new FormulaErrorIssueDto(
                v.TableCode, v.Row.SheetCopyId, v.Row.RowKey, v.RowOrdinal, v.RowLabelL10n, v.ColumnCode, v.ColumnHeaderL10n,
                v.Row.Kind, v.Row.ErrorCode,
                hidden ? null : v.Formula,
                isError
                    ? [.. inputs.Where(i => CanSee(i.TableDefId, i.ColumnDefId))
                        .Select(i => new FormulaErrorInputDto(
                            i.TableCode ?? string.Empty, i.RowKey, i.ColumnCode ?? string.Empty, i.PeriodOffset, ValueOf(i)))]
                    : null,
                source, v.Row.Since, dependentCount);
        })], visible.Count);
    }

    /// <summary>
    /// ✎ B78: назви записів довідника для цілочисельних входів власних помилок — один запит на відповідь; без сховища
    /// чи без таких входів — без запиту.
    /// </summary>
    private async Task<IReadOnlyDictionary<(int ColumnDefId, long EntryId), LocalizedText>> LookupNamesAsync(
        IReadOnlyList<FormulaErrorView> rows, CancellationToken ct)
    {
        var refs = new HashSet<(int ColumnDefId, long EntryId)>();
        foreach (var row in rows.Where(r => r.Row.Kind == Domain.Entities.Calculations.FormulaError.ErrorKind))
        {
            foreach (var input in Recalculation.FormulaErrorInputs.Parse(row.Row.InputsJson))
            {
                if (input.Value is { } raw
                    && long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
                {
                    refs.Add((input.ColumnDefId, id));
                }
            }
        }

        return formulaErrors is null || refs.Count == 0
            ? new Dictionary<(int ColumnDefId, long EntryId), LocalizedText>()
            : await formulaErrors.LookupNamesAsync(refs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Чи підійшло рядку правило методології — тоді причина йде правилом і колонками, які треба заповнити
    /// (<see cref="CalculationRejectionReasons"/>), а не підказкою «що приймають правила».
    /// </summary>
    private static bool HasRuleReason(string kind)
        => kind is ICalculationIssueStore.CategoryRuleRejected or ICalculationIssueStore.MissingInputs;
}
