// src/Ecr.Application/Periods/ProjectDocumentReopenHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Periods;

/// <summary>Діючі винятки «місяць відкрито для документа» в проєкті — для вкладки Periods.</summary>
/// <param name="CanClose">Чи може поточний користувач закрити виняток («Close now»): право <c>Period.Reopen</c> у проєкті.</param>
/// <param name="Items">Винятки ВИДИМИХ користувачеві документів, за місяцем і номером документа.</param>
public sealed record ProjectDocumentReopens(bool CanClose, IReadOnlyList<ProjectDocumentReopenItem> Items);

/// <summary>
/// Перелік діючих винятків проєкту (приймальна i4: «January shows "Locked · 1 document reopened until …" with the
/// document name and a "Close now" action»).
/// </summary>
/// <remarks>
/// ⛔ Невидимий проєкт — <c>404</c>, як і відсутній (<see cref="ProjectVisibility"/>). Документ, якого користувач не
/// бачить, у перелік НЕ потрапляє: ні номера, ні строку — інакше рядок Periods розповідав би про схований документ.
/// Окремого права на читання немає: банер на сторінці документа теж бачить кожен, хто бачить документ.
/// </remarks>
public sealed class ListProjectDocumentReopensHandler(
    IDocumentPeriodReopenStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Діючі винятки проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ProjectDocumentReopens> HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        ProjectVisibility.RequireVisible(profile, projectId);

        var rows = await store.ListActiveForProjectAsync(projectId, clock.UtcNow, ct).ConfigureAwait(false);

        var visible = new List<ProjectDocumentReopenItem>(rows.Count);
        var decided = new Dictionary<long, bool>();
        foreach (var row in rows)
        {
            if (!decided.TryGetValue(row.DocumentId, out var allowed))
            {
                allowed = (await access.CanReadDocumentAsync(profile, row.DocumentId, ct).ConfigureAwait(false)).IsAllowed;
                decided[row.DocumentId] = allowed;
            }

            if (allowed)
            {
                visible.Add(row);
            }
        }

        return new ProjectDocumentReopens(
            PermissionCheck.IsGrantedIn(profile, OpenDocumentPeriodReopenHandler.Permission, projectId), visible);
    }
}

/// <summary>
/// Закриває діючий виняток документа до строку («Close now», приймальна i4): місяць знову закритий і для цього документа.
/// </summary>
/// <remarks>
/// ⛔ Право — те саме, що відкриває (<c>Period.Reopen</c> у проєкті документа); невидимий документ — <c>404</c> до
/// права. Нічого відкритого — нічого не робить (<c>204</c>): повторне натискання чи гонитва з <c>PeriodStateJob</c> не
/// помилка. Журнал — той самий запис «Close», що й при заміні чи закінченні строку, причина
/// <see cref="DocumentPeriodReopen.ClosedManually"/>.
/// </remarks>
public sealed class CloseDocumentPeriodReopenHandler(
    IDocumentPeriodReopenStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Закриває виняток документа за місяць.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        var userId = profile.UserId;

        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var facts = await store.GetFactsAsync(documentId, periodKey, now, ct).ConfigureAwait(false)
                    ?? throw DocumentVisibility.NotFound(documentId);

        PermissionCheck.RequireIn(profile, OpenDocumentPeriodReopenHandler.Permission, facts.ProjectId);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var open = await store.FindOpenAsync(documentId, periodKey, innerCt).ConfigureAwait(false);
            if (open is null)
            {
                return;
            }

            // Прострочений, але ще не позначений задачею станів — закривається як «строк минув», від імені системи.
            var expired = !open.IsActiveAt(now);
            int? closedBy = expired ? null : userId;
            open.Close(now, closedBy, expired ? DocumentPeriodReopen.ClosedExpired : DocumentPeriodReopen.ClosedManually);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                OpenDocumentPeriodReopenHandler.CloseRecord(
                    open, facts.TemplateVersionId, facts.BusinessKey, now, closedBy, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
