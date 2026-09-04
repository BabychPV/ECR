using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Projects;

/// <summary>Проєкт у переліку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="Status">Стан проєкту.</param>
/// <param name="TimeZoneId">Пояс майданчика.</param>
/// <param name="PeriodKind">Періодичність.</param>
/// <param name="CurrentPeriodId">Поточний період — підказка UI, не правило доступу (D-77).</param>
/// <param name="PeriodCount">Скільки періодів у календарі.</param>
public sealed record ProjectSummary(
    int Id,
    string Code,
    ProjectStatus Status,
    string TimeZoneId,
    PeriodKind PeriodKind,
    int? CurrentPeriodId,
    int PeriodCount);

/// <summary>Перелік проєктів. Право <c>Document.View</c>.</summary>
public sealed class ListProjectsHandler(
    IProjectStore projects, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду.</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає сторінку проєктів, видимих користувачу.</summary>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<ProjectSummary>> HandleAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                "ECR-CELL-0422", $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.");
        }

        var all = await projects.ListAsync(page, ct).ConfigureAwait(false);

        // ⚠ Фільтр за грантами робиться ТУТ, а не запитом: гранти вже
        // розгорнуті в профілі, і другий похід у базу за тим самим нічого не
        // додав би. Але фільтр обов'язковий: перелік проєктів, до яких немає
        // доступу, — це вже розвідка структури підприємства.
        var visible = all.Items
            .Where(p => profile.LevelFor(ResourceKind.Project, p.Id) >= GrantLevel.Read)
            .ToList();

        return new PagedResult<ProjectSummary>(visible, all.NextCursor, all.TotalCount);
    }
}

/// <summary>Створення проєкту. Право <c>Project.Manage</c>.</summary>
public sealed class CreateProjectHandler(
    IProjectStore projects,
    IPeriodStore periods,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування проєктами.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Створює проєкт на звітний рік.</summary>
    /// <param name="code">Код проєкту.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="timeZoneId">Пояс майданчика.</param>
    /// <param name="periodKind">Періодичність.</param>
    /// <param name="year">Звітний рік; <c>null</c> — поточний за <c>IClock</c>.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="periodPolicyId">Політика періодів.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        string timeZoneId,
        PeriodKind periodKind,
        int? year,
        int templateVersionId,
        int periodPolicyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        await ListTemplatesHandler.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var reportingYear = year ?? clock.UtcNow.Year;

        // ⛔ Нуль тут — не «значення за замовчуванням», а відсутність вибору.
        // Проєкт без версії шаблону не має структури, без політики періодів —
        // меж; створений таким, він виглядав би робочим до першої спроби
        // відкрити документ.
        if (templateVersionId <= 0)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0404", "Проєкт неможливо створити без версії шаблону.");
        }

        if (periodPolicyId <= 0)
        {
            throw new BusinessRuleException(
                "ECR-PRD-0422", "Проєкт неможливо створити без політики періодів.");
        }

        // ⚠ Пояс перевіряється ТУТ, при створенні: після відкриття першого
        // періоду змінити його вже не можна (ФВ-1.1a), тож невідомий
        // ідентифікатор став би вічною властивістю проєкту.
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var project = new Project(
            EcrCode.Create(code),
            new LocalizedText(name.ToDictionary(StringComparer.Ordinal)),
            new DateOnly(reportingYear, 1, 1),
            new DateOnly(reportingYear, 12, 31),
            templateVersionId,
            periodKind,
            periodPolicyId,
            timeZoneId);

        await periods.AddProjectAsync(project, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return project.Id;
    }
}
