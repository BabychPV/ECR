// src/Ecr.Application/Ports/ISheetWorkflowMailStore.cs
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>✎ D-451: що лист про подання/повернення аркуша розповідає про документ, аркуш, період і дію.</summary>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="DocumentName">Назва документа мовами; <c>null</c> — назви немає.</param>
/// <param name="SheetCode">Код аркуша (для посилання й зіставлення з наданнями учасників).</param>
/// <param name="SheetName">Назва аркуша мовами.</param>
/// <param name="PeriodLabel">Період: ключ і межі, як у листах подій періоду (<c>PeriodStateJob</c>).</param>
/// <param name="OwnerUserId">Власник документа (<c>CreatedByUserId</c>, D-443).</param>
/// <param name="ActorName">Ім'я того, хто подав або повернув.</param>
public sealed record SheetMailFacts(
    int ProjectId,
    string DocumentKey,
    LocalizedText? DocumentName,
    string SheetCode,
    LocalizedText? SheetName,
    string PeriodLabel,
    int OwnerUserId,
    string ActorName);

/// <summary>✎ D-451: скринька адресата листа.</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="Email">Адреса.</param>
/// <param name="Language">Мова з налаштування <c>language</c> (інакше — мова каталогу за замовчуванням).</param>
public sealed record SheetMailbox(int UserId, string Email, string Language);

/// <summary>
/// ✎ D-451: дані для листів «Аркуш подано» / «Аркуш повернуто» — факти й КАНДИДАТИ в адресати.
/// </summary>
/// <remarks>
/// ⛔ Кандидати — лише ПЕРЕДФІЛЬТР (хто взагалі міг би бути адресатом). Остаточне рішення приймає
/// <c>SheetWorkflowNotifier</c> тим самим <c>IAccessDecisionService</c>, що й дії (<c>CanApproveAsync</c>,
/// видимість документа й аркуша): другого правила «хто погоджує» чи «хто бачить» тут немає.
/// <para>
/// ⚠ Ролі — лише ПРЯМІ призначення користувачу (<c>RoleAssignment.UserId</c>), як і адресати-ролі каналу
/// (<c>SmtpChannelSender</c>): членство в групах AD чужої людини системі поза її сесією невідоме.
/// </para>
/// </remarks>
public interface ISheetWorkflowMailStore
{
    /// <summary>Факти листа; <c>null</c> — документа, аркуша чи періоду немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="actorUserId">Хто подав / повернув.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SheetMailFacts?> GetFactsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int actorUserId, CancellationToken ct);

    /// <summary>Активні користувачі з поштою, яким роль призначено напряму.</summary>
    /// <param name="roleId">Роль (кроку маршруту).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> RoleHoldersAsync(int roleId, CancellationToken ct);

    /// <summary>
    /// Маршруту немає: активні користувачі з поштою, що мають напряму роль із бодай одним дозвільним грантом
    /// рівня <c>Approve</c> і вище (передфільтр; рішення — <c>CanApproveAsync</c>).
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> ApproverRoleHoldersAsync(CancellationToken ct);

    /// <summary>
    /// Виконавці не прострочених надань учасника документа (D-443) заданих рівнів, чия область покриває аркуш
    /// і період (надання на документ, на аркуш або на таблицю аркуша).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="levels">Рівні надань.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> ParticipantsAsync(
        long documentId, string sheetCode, PeriodKey periodKey, IReadOnlyCollection<GrantLevel> levels,
        CancellationToken ct);

    /// <summary>Скриньки АКТИВНИХ користувачів із поштою; решта в результат не потрапляє.</summary>
    /// <param name="userIds">Користувачі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<SheetMailbox>> MailboxesAsync(IReadOnlyCollection<int> userIds, CancellationToken ct);

    /// <summary>
    /// Посилання на аркуш за період (<c>/documents/{id}?periodKey=…&amp;sheet=…</c>, як у черзі погодження),
    /// абсолютне — коли задано <c>Notifications:PublicBaseUrl</c>; інакше — шлях без хоста.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCode">Код аркуша.</param>
    public string SheetLink(long documentId, PeriodKey periodKey, string sheetCode);
}
