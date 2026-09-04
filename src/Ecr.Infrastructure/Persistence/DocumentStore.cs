using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentStore(EcrDbContext db) : IDocumentStore
{
    /// <summary>Правило «усі аркуші групи обов'язкові».</summary>
    private const byte RequiresAll = 0;

    /// <summary>Правило «хоча б один аркуш групи».</summary>
    private const byte RequiresOne = 1;

    /// <inheritdoc />
    public Task AddAsync(Document document, CancellationToken ct)
    {
        db.Documents.Add(document);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<DocumentSummary?> FindAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        var document = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var sheetCount = await db.DocumentSheets
            .CountAsync(s => s.DocumentId == documentId && s.IsIncluded, ct)
            .ConfigureAwait(false);

        return new DocumentSummary(
            document.Id, document.ProjectId, document.BusinessKey, document.CreatedAt, sheetCount,
            await StatesAsync(documentId, period, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<PagedResult<DocumentSummary>> ListAsync(
        int? projectId, PeriodKeyFilter period, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        var rows = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id > after && (projectId == null || d.ProjectId == projectId))
            .OrderBy(d => d.Id)
            .Take(page.Limit + 1)
            .Select(d => new DocumentRow(
                d.Id,
                d.ProjectId,
                d.BusinessKey,
                d.CreatedAt,
                db.DocumentSheets.Count(s => s.DocumentId == d.Id && s.IsIncluded)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var page1 = rows.Take(page.Limit).ToList();

        var items = new List<DocumentSummary>(page1.Count);
        foreach (var d in page1)
        {
            items.Add(new DocumentSummary(
                d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt, d.SheetCount,
                await StatesAsync(d.Id, period, ct).ConfigureAwait(false)));
        }

        return new PagedResult<DocumentSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
        int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetDefIds);

        var rules = await db.SheetGroupRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return [];
        }

        var groups = await db.SheetDefs
            .AsNoTracking()
            .Where(s => s.TemplateVersionId == templateVersionId && s.SheetGroup != null)
            .Select(s => new { s.Id, s.SheetGroup })
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var chosen = sheetDefIds.ToHashSet();
        var violations = new List<CompositionViolation>();

        foreach (var rule in rules)
        {
            var inGroup = groups.Where(g => g.SheetGroup == rule.SheetGroup).Select(g => g.Id).ToList();
            if (inGroup.Count == 0)
            {
                continue;
            }

            var picked = inGroup.Count(chosen.Contains);

            // RequiresAll: група або входить цілком, або не входить зовсім.
            // Половина групи — це звіт, у якому частина форм просто відсутня,
            // і виявиться це вже в регуляторі.
            if (rule.RuleKind == RequiresAll && picked > 0 && picked < inGroup.Count)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind,
                    $"обрано {picked} з {inGroup.Count}: група вимагає всі аркуші"));
            }

            if (rule.RuleKind == RequiresOne && picked == 0)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind, "група вимагає хоча б один аркуш"));
            }
        }

        return violations;
    }

    /// <inheritdoc />
    public async Task<string> NextBusinessKeyAsync(
        int projectId, int templateVersionId, CancellationToken ct)
    {
        // ⚠ Ключ будується з проєкту і порядкового номера, а не з GUID:
        // BusinessKey потрапляє в аудит і в назви експортів, і людина мусить
        // упізнавати його з першого погляду.
        var used = await db.Documents
            .AsNoTracking()
            .CountAsync(d => d.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        for (var attempt = used + 1; attempt < used + MaxKeyAttempts; attempt++)
        {
            var candidate = string.Create(
                CultureInfo.InvariantCulture, $"P{projectId}-V{templateVersionId}-{attempt:D4}");

            if (!await db.Documents
                    .AnyAsync(d => d.ProjectId == projectId && d.BusinessKey == candidate, ct)
                    .ConfigureAwait(false))
            {
                return candidate;
            }
        }

        // Унікальність тримає індекс; сюди можна дійти лише якщо хтось створює
        // документи швидше, ніж ми перебираємо номери.
        throw new Application.Errors.BusinessRuleException(
            "ECR-DOC-0409", "Не вдалося підібрати вільний бізнес-ключ документа.");
    }

    /// <summary>Стан аркушів за період; порожньо, якщо період не вказано.</summary>
    private async Task<IReadOnlyDictionary<string, string>> StatesAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var states = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.PeriodKey == periodKey)
            .Select(a => new { a.SheetDefId, a.Status })
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return states.ToDictionary(
            s => s.SheetDefId.ToString(CultureInfo.InvariantCulture),
            s => s.Status.ToString(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Рядок переліку документів.
    /// </summary>
    /// <remarks>
    /// Названий тип, а не анонімний, і це не стиль. Анонімний тип містить
    /// фігурну дужку, яка розриває вираз на два «речення» — і архітектурне
    /// правило «немає <c>ToListAsync</c> без <c>Take</c>» бачить другу
    /// половину без межі. Правило право: читати його код має бути так само
    /// легко, як людині.
    /// </remarks>
    private sealed record DocumentRow(
        long Id, int ProjectId, string BusinessKey, DateTime CreatedAt, int SheetCount);

    /// <summary>Стеля кількості правил складу в одній версії.</summary>
    private const int MaxRules = 500;

    /// <summary>Стеля кількості аркушів у версії; у чинному шаблоні їх 24.</summary>
    private const int MaxSheets = 500;

    /// <summary>Скільки номерів перебирати, шукаючи вільний ключ.</summary>
    private const int MaxKeyAttempts = 1000;
}
