// src/Ecr.Application/Ports/ISourceMonthApprovalGate.cs
namespace Ecr.Application.Ports;

/// <summary>Місяць документа-джерела, якого ще не затверджено (оригінал аркуша).</summary>
/// <param name="SourceDocumentId">Документ-джерело, який обрав би завантажувач за рік (найбільший id).</param>
/// <param name="SourcePeriodKey">Місяць джерела <c>yyyyMM</c>.</param>
/// <param name="SourceProjectCode">Код проєкту документа-джерела.</param>
/// <param name="SourceDocumentKey">Бізнес-ключ документа-джерела.</param>
public sealed record UnapprovedSourceMonth(
    long SourceDocumentId, int SourcePeriodKey, string SourceProjectCode, string SourceDocumentKey);

/// <summary>
/// B80 (N2b, R3.1 A3): подання і затвердження аркуша документа-споживача за місяць <c>yyyyMM</c> неможливі, поки
/// місяць документа-джерела не затверджено — коли шаблон документа є споживачем зв'язки в стані <c>ApprovedOnly</c>
/// (і читання з документа діє), а документ-джерело за рік є.
/// </summary>
/// <remarks>
/// ⛔ За <c>Any</c>, без зв'язки, без документа-джерела за рік (A5) — <c>null</c>: поведінка як до B80. Один запит
/// зв'язки на виклик; решта — лише за <c>ApprovedOnly</c>.
/// </remarks>
public interface ISourceMonthApprovalGate
{
    /// <summary>Перший незатверджений місяць джерела, якого потребує документ за період; <c>null</c> — перешкоди немає.</summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період аркуша, що подають чи затверджують.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<UnapprovedSourceMonth?> FindUnapprovedSourceMonthAsync(long documentId, int periodKey, CancellationToken ct);
}
