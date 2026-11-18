// src/Ecr.Application/Ports/ISourceIssueReader.cs
namespace Ecr.Application.Ports;

/// <summary>
/// B80 (N4, R3.2): місяць документа-споживача, чий місяць документа-джерела не затверджено (споживач ще не затверджений)
/// або змінився після затвердження споживача.
/// </summary>
/// <param name="Kind"><see cref="ISourceIssueReader.SourceMonthNotApproved"/> чи <see cref="ISourceIssueReader.SourceChangedAfterApproval"/>.</param>
/// <param name="PeriodKey">Період документа-споживача, якого стосується рядок.</param>
/// <param name="MonthNumber">Місяць (1–12), яким період споживача володіє і який читає з джерела.</param>
/// <param name="SourcePeriodKey">Місяць джерела <c>yyyyMM</c>.</param>
/// <param name="SourceDocumentId">Документ-джерело, який обрав би завантажувач за рік (найбільший id).</param>
/// <param name="SourceProjectCode">Код проєкту документа-джерела.</param>
/// <param name="SourceDocumentKey">Бізнес-ключ документа-джерела.</param>
/// <param name="SourceApprovedAt">Коли затверджено місяць джерела (UTC); <c>null</c> — зараз не затверджено.</param>
/// <param name="ApprovedAt">Коли затверджено місяць споживача (UTC); <c>null</c> — споживач не затверджений.</param>
public sealed record SourceIssueRow(
    string Kind,
    int PeriodKey,
    int MonthNumber,
    int SourcePeriodKey,
    long SourceDocumentId,
    string SourceProjectCode,
    string SourceDocumentKey,
    DateTime? SourceApprovedAt,
    DateTime? ApprovedAt);

/// <summary>
/// B80 (N4, R3.2): проблеми «джерела» документа-споживача зв'язки в стані <c>ApprovedOnly</c> для панелі проблем
/// розрахунку — за тими самими правилами, що й ворота подання (<see cref="ISourceMonthApprovalGate"/>).
/// </summary>
/// <remarks>
/// ⛔ За <c>Any</c> (жодної зв'язки <c>ApprovedOnly</c>), за вимкненого читання з документа, без споживача, без
/// документа-джерела за рік (A5) — порожньо; за <c>Any</c> — один дрібний запит до зв'язок і нічого більше.
/// Видимість документа-джерела вирішує викликач (сховище доступу не знає).
/// </remarks>
public interface ISourceIssueReader
{
    /// <summary>Місяць споживача не затверджено, а місяць джерела не затверджено (те саме правило, що й ворота N2b).</summary>
    public const string SourceMonthNotApproved = "SourceMonthNotApproved";

    /// <summary>
    /// Місяць споживача затверджено, але місяць джерела зараз не затверджено або затверджено ПІЗНІШЕ за споживача
    /// (консервативно: і тоді, коли числа повторного затвердження ті самі, R3.2).
    /// </summary>
    public const string SourceChangedAfterApproval = "SourceChangedAfterApproval";

    /// <summary>Проблеми джерела для документа за періоди.</summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKeys">Періоди документа (один — звичайне читання; місяці року — річна розкладка).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Рядки за зростанням періоду й місяця; порожньо — проблем немає або правило не діє.</returns>
    public Task<IReadOnlyList<SourceIssueRow>> ListSourceIssuesAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);
}
