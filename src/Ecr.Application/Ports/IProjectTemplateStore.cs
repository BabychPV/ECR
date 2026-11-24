using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>B74 P3 (D-576): шаблон, приєднаний до проєкту (<c>doc.ProjectTemplate</c>), з чинною версією.</summary>
/// <param name="ProjectTemplateId">Сурогатний ідентифікатор рядка <c>doc.ProjectTemplate</c>.</param>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="TemplateCode">Код шаблону — він же префікс бізнес-ключа документа в багатошаблонному проєкті.</param>
/// <param name="TemplateNameL10n">Назва шаблону мовами каталогу.</param>
/// <param name="CurrentTemplateVersionId">Чинна версія шаблону в цьому проєкті (за нею — нові документи цього шаблону).</param>
/// <param name="Version">Позначення чинної версії.</param>
/// <param name="Layout">Розкладка чинної версії.</param>
/// <param name="IsPrimary">Основний шаблон проєкту (шаблон <c>Project.TemplateVersionId</c> до P6).</param>
/// <param name="DocumentCount">Скільки документів цього шаблону в проєкті.</param>
/// <param name="SingleDocumentPerProject">
/// ✎ B74 (24.11): прапорець шаблону B151 (<c>cfg.Template.SingleDocumentPerProject</c>) — у проєкті щонайбільше один
/// документ цього шаблону; другий створення відхиляє <c>409 ECR-DOC-4092</c>.
/// </param>
public sealed record ProjectTemplateRow(
    int ProjectTemplateId,
    int TemplateId,
    string TemplateCode,
    LocalizedText TemplateNameL10n,
    int CurrentTemplateVersionId,
    string Version,
    TemplateLayout Layout,
    bool IsPrimary,
    int DocumentCount,
    bool SingleDocumentPerProject);

/// <summary>Стан проєкту, прочитаний під блоком його рядка (B74 P3).</summary>
/// <param name="Status">Стан.</param>
/// <param name="IsArchiving">Іде архівація.</param>
/// <param name="PeriodKind">Періодичність (одна на проєкт — ADR B74, Q1).</param>
/// <param name="PrimaryTemplateId">Основний шаблон проєкту.</param>
public sealed record ProjectTemplateLockFacts(
    ProjectStatus Status, bool IsArchiving, PeriodKind PeriodKind, int? PrimaryTemplateId);

/// <summary>
/// B74 P3 (D-576): шаблони проєкту — перелік, приєднання, від'єднання. Окреме сховище, а не розширення
/// <see cref="IDocumentStore"/>: це конфігурація проєкту, не документ.
/// </summary>
public interface IProjectTemplateStore
{
    /// <summary>Шаблони проєкту за кодом шаблону; проєкт без рядків — порожньо.</summary>
    public Task<IReadOnlyList<ProjectTemplateRow>> ListAsync(int projectId, CancellationToken ct);

    /// <summary>Один шаблон проєкту; <c>null</c> — шаблон до проєкту не приєднано.</summary>
    public Task<ProjectTemplateRow?> FindAsync(int projectId, int templateId, CancellationToken ct);

    /// <summary>
    /// Блок рядка проєкту (<c>UPDLOCK, HOLDLOCK</c>) — той самий, що бере створення документа й перенос версії; лише
    /// в транзакції. <c>null</c> — проєкту немає.
    /// </summary>
    public Task<ProjectTemplateLockFacts?> LockProjectAsync(int projectId, CancellationToken ct);

    /// <summary>Скільки документів шаблону в проєкті (основний шаблон рахує й документи з <c>TemplateId = NULL</c>).</summary>
    public Task<int> CountDocumentsAsync(int projectId, int templateId, bool isPrimary, CancellationToken ct);

    /// <summary>Чи є в області «проєкт × шаблон» власний чинний маршрут погодження (B155).</summary>
    public Task<bool> HasOwnRouteAsync(int projectId, int templateId, CancellationToken ct);

    /// <summary>Скільки шаблонів у проєкті (під блоком проєкту — точне число для вибору формату ключа).</summary>
    public Task<int> CountAsync(int projectId, CancellationToken ct);

    /// <summary>Додає рядок <c>doc.ProjectTemplate</c> (зберігає <c>IUnitOfWork.SaveChangesAsync</c>).</summary>
    public void Attach(int projectId, int templateId, int currentTemplateVersionId);

    /// <summary>Прибирає рядок пари і її шкалу версій (<c>doc.ProjectTemplateVersion</c>) — одразу, у поточній транзакції.</summary>
    public Task DetachAsync(int projectId, int templateId, CancellationToken ct);
}
