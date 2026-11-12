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
        var (formulaErrorItems, visibleErrorCount) = FormulaErrorsOf(errorRows, readable);

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
        IReadOnlyList<FormulaErrorView> rows, DocumentReadScope readable)
    {
        var visible = rows
            .Where(v => readable.CanReadTable(v.Row.TableDefId) && readable.CanReadColumn(v.Row.ColumnDefId))
            .ToList();
        if (visible.Count == 0)
        {
            return (null, 0);
        }

        bool CanSee(int tableDefId, int columnDefId) => readable.CanReadTable(tableDefId) && readable.CanReadColumn(columnDefId);

        // ⚠ Payload: власні помилки (корені) — першими; «не оновлено» може бути в кожному рядку великої таблиці — стеля.
        var shown = visible
            .OrderBy(v => v.Row.Kind == Domain.Entities.Calculations.FormulaError.ErrorKind ? 0 : 1)
            .Take(CalculationIssuesDto.MaxFormulaErrors);

        return ([.. shown.Select(v =>
        {
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
                v.TableCode, v.Row.SheetCopyId, v.Row.RowKey, v.RowOrdinal, v.ColumnCode, v.ColumnHeaderL10n,
                v.Row.Kind, v.Row.ErrorCode,
                hidden ? null : v.Formula,
                isError
                    ? [.. inputs.Where(i => CanSee(i.TableDefId, i.ColumnDefId))
                        .Select(i => new FormulaErrorInputDto(
                            i.TableCode ?? string.Empty, i.RowKey, i.ColumnCode ?? string.Empty, i.PeriodOffset, i.Value))]
                    : null,
                source, v.Row.Since);
        })], visible.Count);
    }

    /// <summary>
    /// Чи підійшло рядку правило методології — тоді причина йде правилом і колонками, які треба заповнити
    /// (<see cref="CalculationRejectionReasons"/>), а не підказкою «що приймають правила».
    /// </summary>
    private static bool HasRuleReason(string kind)
        => kind is ICalculationIssueStore.CategoryRuleRejected or ICalculationIssueStore.MissingInputs;
}
