using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище винятків «закритий місяць відкрито для одного документа» (<c>doc.DocumentPeriodReopen</c>,
/// приймальна 08.11 (i)).
/// </summary>
public interface IDocumentPeriodReopenStore
{
    /// <summary>
    /// Факти документа й місяця для рішення: проєкт, його стан і пояс, ефективний стан періоду БЕЗ винятку.
    /// <c>null</c> — документа немає.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="utcNow">Момент для ефективного стану.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentPeriodFacts?> GetFactsAsync(long documentId, int periodKey, DateTime utcNow, CancellationToken ct);

    /// <summary>Незакритий виняток документа за місяць (відстежуваний); <c>null</c> — немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⚠ Може бути вже простроченим, але ще не позначеним закритим: рішення — <see cref="DocumentPeriodReopen.IsActiveAt"/>.</remarks>
    public Task<DocumentPeriodReopen?> FindOpenAsync(long documentId, int periodKey, CancellationToken ct);

    /// <summary>Діючий виняток із іменем того, хто відкрив; <c>null</c> — немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="utcNow">Момент.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentPeriodReopenView?> FindActiveViewAsync(
        long documentId, int periodKey, DateTime utcNow, CancellationToken ct);

    /// <summary>Додає новий виняток до одиниці роботи.</summary>
    /// <param name="exception">Виняток.</param>
    public void Add(DocumentPeriodReopen exception);

    /// <summary>
    /// Діючі винятки всіх документів проєкту (вкладка Periods, приймальна i4) — БЕЗ фільтра видимості: його накладає
    /// обробник.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="utcNow">Момент: прострочені, але ще не позначені закритими, не повертаються.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ProjectDocumentReopenItem>> ListActiveForProjectAsync(
        int projectId, DateTime utcNow, CancellationToken ct);
}

/// <summary>Діючий виняток документа проєкту — рядок вкладки Periods (приймальна i4).</summary>
/// <param name="Id">Ідентифікатор винятку.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentName">Номер документа (<c>BusinessKey</c>).</param>
/// <param name="PeriodKey">Місяць.</param>
/// <param name="OpenUntil">До якого моменту (UTC) документ відкрито.</param>
/// <param name="Reason">Причина відкриття.</param>
/// <param name="OpenedByName">Ім'я того, хто відкрив; <c>null</c> — невідоме.</param>
public sealed record ProjectDocumentReopenItem(
    int Id,
    long DocumentId,
    string DocumentName,
    int PeriodKey,
    DateTime OpenUntil,
    string Reason,
    string? OpenedByName);

/// <summary>Факти документа й місяця для відкриття винятку.</summary>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="ProjectCode">Код проєкту — для відмови «архівований».</param>
/// <param name="ProjectStatus">Стан проєкту.</param>
/// <param name="TemplateVersionId">Версія шаблону проєкту — для журналу.</param>
/// <param name="TimeZoneId">Пояс майданчика (IANA).</param>
/// <param name="BusinessKey">Номер документа — для журналу.</param>
/// <param name="PeriodState">Ефективний стан місяця БЕЗ винятку; <c>null</c> — місяця в календарі проєкту немає.</param>
/// <param name="IsArchiving">B69 (G3): триває архівація проєкту — виняток не відкривається, як і в архівованому.</param>
public sealed record DocumentPeriodFacts(
    int ProjectId,
    string ProjectCode,
    ProjectStatus ProjectStatus,
    int TemplateVersionId,
    string TimeZoneId,
    string BusinessKey,
    PeriodState? PeriodState,
    bool IsArchiving = false);

/// <summary>Діючий виняток документа за місяць.</summary>
/// <param name="Id">Ідентифікатор винятку.</param>
/// <param name="PeriodKey">Місяць.</param>
/// <param name="OpenUntil">До якого моменту (UTC) документ відкрито.</param>
/// <param name="Reason">Причина відкриття.</param>
/// <param name="OpenedByUserId">Хто відкрив.</param>
/// <param name="OpenedByName">Ім'я того, хто відкрив; <c>null</c> — невідоме.</param>
/// <param name="OpenedAt">Коли відкрито (UTC).</param>
public sealed record DocumentPeriodReopenView(
    int Id,
    int PeriodKey,
    DateTime OpenUntil,
    string Reason,
    int OpenedByUserId,
    string? OpenedByName,
    DateTime OpenedAt);
