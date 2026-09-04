using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через EF без гонки.
/// </summary>
/// <remarks>
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт уведений `Q-032`,
/// реалізація — `Q-050`.
/// </remarks>
public sealed class TemplateVersionStore(EcrDbContext db) : ITemplateVersionStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДИН statement із <c>OUTPUT</c> (<c>R-B7</c>). Послідовність
    /// «прочитати → додати одиницю → записати» під паралельними
    /// презентаційними правками дає два однакові значення ревізії, а ревізія —
    /// це ключ кешу <c>v{id}:r{rev}</c>. Два різні знімки під одним ключем
    /// означають, що частина інстансів віддає стару структуру, і знайти це
    /// потім практично неможливо.
    /// </remarks>
    public async Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } tx)
        {
            command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        }

        command.CommandText = """
            UPDATE cfg.TemplateVersion
            SET    PresentationRevision = PresentationRevision + 1
            OUTPUT inserted.PresentationRevision
            WHERE  Id = @id;
            """;
        command.Parameters.AddWithValue("@id", templateVersionId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? throw new NotFoundException("ECR-TMPL-0404", $"Версії шаблону {templateVersionId} не існує.")
            : (int)result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Питання не про кількість, а про факт: якщо на версії вже є документи,
    /// структурна правка заборонена незалежно від того, один він чи мільйон.
    /// Тому <c>AnyAsync</c>, а не <c>CountAsync</c>.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct)
        => db.Documents
             .AsNoTracking()
             .Join(db.Projects.AsNoTracking(),
                   d => d.ProjectId,
                   p => p.Id,
                   (d, p) => p.TemplateVersionId)
             .AnyAsync(id => id == templateVersionId, ct);

    /// <inheritdoc />
    public async Task<int> CreateDraftAsync(
        int templateId, string versionNumber, int userId, DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Templates.AnyAsync(t => t.Id == templateId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException("ECR-TMPL-0404", $"Шаблон {templateId} не знайдено.");
        }

        if (await db.TemplateVersions
                .AnyAsync(v => v.TemplateId == templateId && v.Version == versionNumber, ct)
                .ConfigureAwait(false))
        {
            throw new Application.Errors.BusinessRuleException(
                "ECR-TMPL-0409", $"Версія {versionNumber} у цьому шаблоні вже існує.");
        }

        var version = new TemplateVersion(templateId, versionNumber, userId, utcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return version.Id;
    }

    /// <inheritdoc />
    public async Task<int> CloneAsync(
        int sourceVersionId, string newVersion, int userId, DateTime utcNow, CancellationToken ct)
    {
        // ⚠ AsNoTracking навмисно: граф зараз перетворять на НОВІ сутності
        // скиданням ключів. Відстежувані об'єкти EF сприйняв би як зміну
        // джерела — тобто спроба клонувати перетворилася б на псування
        // оригіналу.
        var source = await db.TemplateVersions
            .AsNoTracking()
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Columns)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Rows)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.Formulas)
            .Include(v => v.Sheets).ThenInclude(sheet => sheet.Tables).ThenInclude(t => t.ValidationRules)
            .FirstOrDefaultAsync(v => v.Id == sourceVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("ECR-TMPL-0404", $"Версії шаблону {sourceVersionId} не існує.");

        var clonedFrom = source.Id;
        var (clone, links) = TemplateVersionCloner.Prepare(source, newVersion, userId, utcNow);

        db.TemplateVersions.Add(clone);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Формули посилаються на колонки і рядки ЧИСЛОМ, а не навігацією, тому
        // EF їх не перев'язує: це доводиться робити після того, як база
        // призначила нові ключі.
        TemplateVersionCloner.Relink(clone, links);
        SetClonedFrom(clone, clonedFrom);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return clone.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateVersionSummary>> ListVersionsAsync(
        int templateId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        return await db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateId == templateId)
            .OrderBy(v => v.Id)
            .Take(page.Limit)
            .Select(v => new TemplateVersionSummary(
                v.Id, v.Version, v.Status, v.PresentationRevision, v.ClonedFromVersionId, v.PublishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CreateTemplateAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        int userId,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (await db.Templates.AnyAsync(t => t.Code == code, ct).ConfigureAwait(false))
        {
            throw new Application.Errors.BusinessRuleException(
                "ECR-TMPL-0409", $"Шаблон з кодом «{code}» уже існує.");
        }

        var template = new Template(
            Domain.ValueObjects.EcrCode.Create(code),
            new Domain.ValueObjects.LocalizedText(name.ToDictionary(StringComparer.Ordinal)),
            userId,
            utcNow);

        db.Templates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return template.Id;
    }

    /// <inheritdoc />
    public async Task<PagedResult<TemplateSummary>> ListTemplatesAsync(
        CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Беремо на один більше за сторінку: так видно, чи є наступна, без
        // окремого COUNT по всій таблиці.
        var rows = await db.Templates
            .AsNoTracking()
            .Where(t => t.Id > after)
            .OrderBy(t => t.Id)
            .Take(page.Limit + 1)
            .Select(t => new TemplateSummary(
                t.Id, t.Code, db.TemplateVersions.Count(v => v.TemplateId == t.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).ToList();

        return new PagedResult<TemplateSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <summary>Проставляє <c>ClonedFromVersionId</c> — поле з приватним сетером.</summary>
    private static void SetClonedFrom(TemplateVersion clone, int sourceVersionId)
        => typeof(TemplateVersion)
            .GetProperty(nameof(TemplateVersion.ClonedFromVersionId))!
            .SetValue(clone, sourceVersionId);
}
