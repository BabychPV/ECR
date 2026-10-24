// src/Ecr.Infrastructure/Notifications/SheetWorkflowMailStore.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Notifications;

/// <summary>
/// ✎ D-451: факти й кандидати в адресати листів «Аркуш подано» / «Аркуш повернуто» над <see cref="EcrDbContext"/>.
/// </summary>
/// <remarks>
/// ⚠ Лише передфільтр: хто з кандидатів справді отримає лист, вирішує <c>SheetWorkflowNotifier</c> рішенням про
/// доступ кожного (<see cref="ISheetWorkflowMailStore"/>). Тому тут немає ні областей ролей, ні заборон, ні строків
/// дії призначень — лише «хто взагалі може бути адресатом», з запасом.
/// </remarks>
public sealed class SheetWorkflowMailStore(EcrDbContext db, IClock clock, IConfiguration configuration)
    : ISheetWorkflowMailStore
{
    /// <summary>Ключ конфігурації: зовнішня адреса застосунку для посилань у листах (порожньо — шлях без хоста).</summary>
    public const string PublicBaseUrlKey = "Notifications:PublicBaseUrl";

    /// <inheritdoc />
    public async Task<SheetMailFacts?> GetFactsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, int actorUserId, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.ProjectId, d.BusinessKey, d.NameL10n, d.CreatedByUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var sheet = await db.SheetDefs.AsNoTracking()
            .Where(s => s.Id == sheetDefId)
            .Select(s => new { s.Code, s.NameL10n })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (sheet is null)
        {
            return null;
        }

        var period = await db.Periods.AsNoTracking()
            .Where(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey.Value)
            .Select(p => new { p.PeriodStart, p.PeriodEnd })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // ⚠ Той самий вигляд періоду, що й у листах подій періоду (`PeriodStateJob.NotifyAsync`).
        var label = period is null
            ? periodKey.ToString()
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{periodKey.Value} ({period.PeriodStart:yyyy-MM-dd} – {period.PeriodEnd:yyyy-MM-dd})");

        var actor = await db.Users.AsNoTracking()
            .Where(u => u.Id == actorUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new SheetMailFacts(
            document.ProjectId,
            document.BusinessKey,
            document.NameL10n,
            sheet.Code,
            sheet.NameL10n,
            label,
            document.CreatedByUserId,
            actor ?? actorUserId.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> RoleHoldersAsync(int roleId, CancellationToken ct)
        => await (
                from a in db.RoleAssignments.AsNoTracking()
                join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                where a.RoleId == roleId && u.IsActive && u.Email != null
                select u.Id)
            .Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ApproverRoleHoldersAsync(CancellationToken ct)
    {
        var approverRoles = db.ResourceGrants.AsNoTracking()
            .Where(g => !g.IsDeny && g.Level >= GrantLevel.Approve)
            .Select(g => g.RoleId);

        return await (
                from a in db.RoleAssignments.AsNoTracking()
                join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                where approverRoles.Contains(a.RoleId) && u.IsActive && u.Email != null
                select u.Id)
            .Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ParticipantsAsync(
        long documentId, string sheetCode, PeriodKey periodKey, IReadOnlyCollection<GrantLevel> levels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            return [];
        }

        var wanted = levels.Distinct().ToList();
        var now = clock.UtcNow;

        // ⚠ Область: надання на документ (аркуш не задано) або на ЦЕЙ аркуш (з таблицею чи без), період не задано
        // або збігається; прострочене — не кандидат. Делегатор і заборони — справа рішення про доступ.
        return await (
                from a in db.EditAssignments.AsNoTracking()
                join r in db.EditAssignmentRecipients.AsNoTracking() on a.Id equals r.AssignmentId
                where a.DocumentId == documentId
                      && wanted.Contains(a.Level)
                      && (a.SheetCode == null || a.SheetCode == sheetCode)
                      && (a.PeriodKeyValue == null || a.PeriodKeyValue == periodKey.Value)
                      && (a.ExpiresAt == null || a.ExpiresAt > now)
                select r.UserId)
            .Distinct().OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SheetMailbox>> MailboxesAsync(
        IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }

        var ids = userIds.Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsActive && u.Email != null)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, Email = u.Email! })
            .ToListAsync(ct).ConfigureAwait(false);

        var withMail = users.Where(u => !string.IsNullOrWhiteSpace(u.Email)).ToList();
        var found = withMail.Select(u => u.Id).ToList();
        var prefs = await db.UserPreferences.AsNoTracking()
            .Where(p => p.Key == "language" && found.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, p => p.ValueJson, ct).ConfigureAwait(false);

        return
        [
            .. withMail.Select(u => new SheetMailbox(
                u.Id, u.Email.Trim(), SmtpChannelSender.LanguageOf(prefs.GetValueOrDefault(u.Id)))),
        ];
    }

    /// <inheritdoc />
    public string SheetLink(long documentId, PeriodKey periodKey, string sheetCode)
    {
        // ⚠ Той самий маршрут, що й посилання черги погодження (`useApprovalQueue.ts`).
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/documents/{documentId}?periodKey={periodKey.Value}&sheet={Uri.EscapeDataString(sheetCode)}");

        var baseUrl = configuration[PublicBaseUrlKey];

        return string.IsNullOrWhiteSpace(baseUrl) ? path : baseUrl.Trim().TrimEnd('/') + path;
    }
}
