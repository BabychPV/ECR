// src/Ecr.Application/Documents/RequiredInputs/GetRequiredInputsHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents.RequiredInputs;

/// <summary>
/// ✎ D-577 (L6): <c>GET /documents/{id}/tables/{tableInstanceId}/required-inputs</c> — які обов'язкові входи методологій
/// порожні в рядках таблиці, одразу при відкритті (третє джерело моделі «потрібне значення» клієнта, §4.1 плану).
/// </summary>
/// <remarks>
/// ⚠ Доступ — як у зрізу (<c>GetTableSliceHandler</c>): <c>Document.View</c>, видимий документ, екземпляр цього документа,
/// видима таблиця (інакше одна й та сама 404); комірки схованих від читача колонок у переліку відсутні.
/// <para>⚠ Ціна — <see cref="RequiredInputsScanner"/>: без методологій з вимогами — без читання зрізу.</para>
/// </remarks>
public sealed class GetRequiredInputsHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    RequiredInputsScanner scanner)
{
    /// <summary>Стеля комірок у відповіді (решта — у <see cref="RequiredInputsDto.Total"/>).</summary>
    public const int MaxCells = 5000;

    /// <summary>Перелік порожніх обов'язкових входів таблиці.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RequiredInputsDto> HandleAsync(long documentId, long tableInstanceId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.View", ct).ConfigureAwait(false);

        var instance = await DocumentVisibility
            .ResolveInDocumentAsync(rowStore, documentId, tableInstanceId, ct).ConfigureAwait(false);
        var period = new Domain.ValueObjects.PeriodKey(instance.PeriodKey);
        var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(period);
        if (!readable.CanReadTable(instance.TableDefId))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId);
        var plan = table is null
            ? null
            : await scanner.PlanAsync(documentId, instance.PeriodKey, instance.TemplateVersionId, table, ct).ConfigureAwait(false);
        if (plan is null || table is null)
        {
            return new RequiredInputsDto(tableInstanceId, [], 0, Truncated: false);
        }

        var rowIds = await rowStore.GetRowIdsAsync(tableInstanceId, period, ct).ConfigureAwait(false);
        var cells = await cellStore.ReadSliceAsync(tableInstanceId, period, ct).ConfigureAwait(false);

        var columnIds = table.Columns.Where(c => !c.IsDeleted)
            .GroupBy(c => c.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var visible = RequiredInputsScanner
            .Scan(plan, table, snapshot.ColumnsById, cells, rowIds, currentUser.Language)
            .Where(m => m.ColumnCode is not null && columnIds.TryGetValue(m.ColumnCode, out var columnId) && readable.CanReadColumn(columnId))
            .ToList();

        return new RequiredInputsDto(
            tableInstanceId,
            [
                .. visible.Take(MaxCells).Select(m => new RequiredInputCellDto(
                    m.RowKey!, m.ColumnCode!, m.Severity == Domain.Enums.ValidationSeverity.Error ? RequiredInputLevels.Block : RequiredInputLevels.Warn,
                    m.Methodologies ?? [], m.Message,
                    Derived: m.Severity == Domain.Enums.ValidationSeverity.Error && !m.BlocksSave)),
            ],
            visible.Count,
            Truncated: visible.Count > MaxCells);
    }
}

/// <summary>✎ D-577 (L6): порожні обов'язкові входи методологій таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="Cells">Комірки (рядки за порядком на екрані, до <see cref="GetRequiredInputsHandler.MaxCells"/>).</param>
/// <param name="Total">Скільки всього.</param>
/// <param name="Truncated">Перелік обрізано стелею.</param>
public sealed record RequiredInputsDto(long TableInstanceId, IReadOnlyList<RequiredInputCellDto> Cells, int Total, bool Truncated);

/// <summary>Порожній обов'язковий вхід.</summary>
/// <param name="RowKey">Рядок.</param>
/// <param name="ColumnCode">Колонка.</param>
/// <param name="Level">
/// <c>block</c> — не дасть подати (Q6); <c>warn</c> — попередження. Зберегти рядок не дасть лише заведений методологом Block
/// (<paramref name="Derived"/> = <c>false</c>); виведений (D-578) блокує тільки подання.
/// </param>
/// <param name="Derived">✎ D-578: вимогу виведено з полів, які читає скрипт (а не заведено методологом).</param>
/// <param name="Methodologies">Коди методологій, що вимагають колонку.</param>
/// <param name="Message">Текст мовою читача (як у відповіді PATCH, <c>ECR-CALC-0437</c>).</param>
public sealed record RequiredInputCellDto(
    string RowKey, string ColumnCode, string Level, IReadOnlyList<string> Methodologies, string Message, bool Derived = false);

/// <summary>Рівні <see cref="RequiredInputCellDto.Level"/>.</summary>
public static class RequiredInputLevels
{
    /// <summary>Блокує подання і збереження рядка.</summary>
    public const string Block = "block";

    /// <summary>Попередження.</summary>
    public const string Warn = "warn";
}
