using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Що з аркушів документа бачить читач: перелік, картка і зведення не відкривають код, назву,
/// стан і лічильники аркуша, схованого від нього (<c>Deny</c> на аркуш/таблицю/колонку або
/// звуження призначення ролі аркушами, D-214).
/// </summary>
/// <remarks>
/// ⛔ Фільтр стоїть у ОБРОБНИКУ, після сховища: сховище віддає повний склад (його читають і
/// шляхи запису), а межу видимості приймає <see cref="DocumentReadScope"/> — те саме правило, що
/// й у <c>GET /documents/{id}/validation</c> (<c>HiddenValidationIssues.CanSee</c>).
///
/// ⚠ <c>ErrorCount</c>/<c>WarningCount</c> збережено по ВСЬОМУ документу, тож для читача з
/// обмеженням вони <c>null</c> («—»): перерахунок по видимому — це окремий запит на документ.
/// Роль без обмежень (немає жодної заборони/гранта нижче проєкту, жодного звуження) проходить
/// як є й нічого не коштує: <see cref="HasRestrictions"/> — чиста функція над профілем.
/// </remarks>
public static class DocumentSheetVisibility
{
    private static readonly string[] BelowProject =
    [
        $"{ResourceKind.Sheet}:", $"{ResourceKind.Table}:", $"{ResourceKind.Column}:",
    ];

    /// <summary>Чи має профіль хоч одне обмеження нижче рівня проєкту (дешево, без бази).</summary>
    /// <param name="profile">Профіль прав.</param>
    public static bool HasRestrictions(AccessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return !profile.IsIntegrationWriter
               && (AnyBelowProject(profile.Denies) || AnyBelowProject(profile.Grants.Keys)
                   || profile.Scoped.Values.Any(s => s.Narrowed.Count > 0
                                                    || AnyBelowProject(s.Denies) || AnyBelowProject(s.Grants.Keys)));
    }

    private static bool AnyBelowProject(IEnumerable<string> keys)
        => keys.Any(k => BelowProject.Any(p => k.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>Межі читання для кожного проєкту переліку (один зворот до метаданих на проєкт).</summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="documents">Документи сторінки.</param>
    /// <param name="periodKey">Період запиту; <c>null</c> — без періоду.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyDictionary<int, DocumentReadScope>> ScopesAsync(
        IAccessDecisionService access, AccessProfile profile, IEnumerable<(int ProjectId, long DocumentId)> documents,
        int? periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(documents);

        var scopes = new Dictionary<int, DocumentReadScope>();
        foreach (var (projectId, documentId) in documents)
        {
            if (scopes.ContainsKey(projectId))
            {
                continue;
            }

            var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
            if (periodKey is { } value && new PeriodKey(value).IsValid)
            {
                scope = scope.InPeriod(new PeriodKey(value));
            }

            scopes[projectId] = scope;
        }

        return scopes;
    }

    /// <summary>
    /// Аркуші, яких читач не бачить у жодному з проєктів, — для фільтра переліку за станом.
    /// Ідентифікатор аркуша належить версії шаблону, тож плоский перелік по проєктах не плутається.
    /// </summary>
    /// <param name="samples">Порт, що дає по одному документу проєкту для побудови меж.</param>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="projects">Проєкти, документи яких перелічуються.</param>
    /// <param name="periodKey">Період запиту.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyCollection<int>?> HiddenSheetIdsAsync(
        IDocumentListSummaryStore samples, IAccessDecisionService access, AccessProfile profile,
        IReadOnlyCollection<int> projects, int periodKey, CancellationToken ct)
        => (await HiddenFilterAsync(samples, access, profile, projects, periodKey, default, ct).ConfigureAwait(false))
            .HiddenSheetDefIds;

    /// <summary>
    /// Фільтр переліку з тим, чого читач не бачить: аркуші, таблиці й колонки (R-7: пізня правка схованого
    /// не дає позначки). Читач без обмежень — <paramref name="filter"/> без змін, без запитів.
    /// </summary>
    /// <param name="samples">Порт, що дає по одному документу проєкту для побудови меж.</param>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="projects">Проєкти, документи яких перелічуються.</param>
    /// <param name="periodKey">Період запиту; <c>null</c> — без періоду.</param>
    /// <param name="filter">Фільтр, який доповнюється.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<DocumentListFilter> HiddenFilterAsync(
        IDocumentListSummaryStore samples, IAccessDecisionService access, AccessProfile profile,
        IReadOnlyCollection<int> projects, int? periodKey, DocumentListFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (!HasRestrictions(profile))
        {
            return filter;
        }

        var sample = await samples.SampleDocumentPerProjectAsync(projects, ct).ConfigureAwait(false);
        var scopes = await ScopesAsync(access, profile, sample.Select(s => (s.Key, s.Value)), periodKey, ct)
            .ConfigureAwait(false);

        return filter with
        {
            HiddenSheetDefIds = [.. scopes.Values.SelectMany(s => s.HiddenSheetIds()).Distinct().Order()],
            HiddenTableDefIds = [.. scopes.Values.SelectMany(s => s.HiddenTableIds()).Distinct().Order()],
            HiddenColumnDefIds = [.. scopes.Values.SelectMany(s => s.HiddenColumnIds()).Distinct().Order()],

            // ⛔ Фільтр resultsStale не бачить проєктів зі звуженням (та сама межа, що й у зведенні): список
            // потрібен лише йому, тож решту запитів фільтр не обтяжує і не змінює.
            NarrowedProjectIds = filter.ResultsStale is null
                ? filter.NarrowedProjectIds
                : [.. scopes.Where(s => IsProjectNarrowed(s.Value)).Select(s => s.Key).Order()],
        };
    }

    /// <summary>
    /// Чи читач не бачить у проєкті хоч щось (аркуш, таблицю чи колонку) — тоді «результати застаріли» не
    /// віддається ні в рядку, ні у фільтрі, ні в лічильнику (оракул про схований вхід).
    /// </summary>
    /// <param name="scope">Межі читання проєкту.</param>
    public static bool IsProjectNarrowed(DocumentReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.HiddenSheetCodes().Count > 0 || scope.HiddenTableIds().Count > 0 || scope.HiddenColumnIds().Count > 0;
    }

    /// <summary>Межі читача як фільтр «що схованo» для одного документа (позначка пізніх правок картки).</summary>
    /// <param name="scope">Межі читання проєкту документа.</param>
    public static DocumentListFilter HiddenOf(DocumentReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return new DocumentListFilter(
            null, null, null, scope.HiddenSheetIds(), scope.HiddenTableIds(), scope.HiddenColumnIds());
    }

    /// <summary>
    /// Стани аркушів документа, яких читач не бачить, — для відмов (видалення, зміна ключа), що інакше
    /// називали б код і стан схованого аркуша. Читач без обмежень — порожньо й без запитів.
    /// </summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="states">Стани аркуш × період документа.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<Ecr.Domain.Entities.Workflow.ApprovalState>> HiddenStatesAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId,
        IReadOnlyCollection<Ecr.Domain.Entities.Workflow.ApprovalState> states, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(states);

        if (states.Count == 0 || !HasRestrictions(profile))
        {
            return [];
        }

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);

        return [.. states.Where(s =>
            !(new PeriodKey(s.PeriodKey).IsValid ? scope.InPeriod(new PeriodKey(s.PeriodKey)) : scope).CanReadSheet(s.SheetDefId))];
    }

    /// <summary>Чи є в структурі версії шаблону щось, чого межі читача не відкривають.</summary>
    /// <param name="scope">Межі читання проєкту.</param>
    /// <param name="sheetCodes">Коди аркушів структури, що перевіряються.</param>
    public static bool IsNarrowed(DocumentReadScope scope, IEnumerable<string> sheetCodes)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sheetCodes);

        return scope.HiddenTableIds().Count > 0
               || scope.HiddenColumnIds().Count > 0
               || sheetCodes.Any(c => !scope.CanReadSheetCode(c));
    }

    /// <summary>Залишає читачеві лише видимі аркуші; лічильники звуженого читача — <c>null</c>.</summary>
    /// <param name="document">Документ зі сховища.</param>
    /// <param name="scope">Межі читання його проєкту.</param>
    public static DocumentSummary For(DocumentSummary document, DocumentReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scope);

        var included = document.IncludedSheetCodes
                       ?? [.. (document.Sheets ?? []).Select(s => s.Code)];
        var visibleSheets = (document.Sheets ?? []).Where(s => scope.CanReadSheetCode(s.Code)).ToList();
        var visibleCodes = included.Count(scope.CanReadSheetCode);
        var narrowed = IsNarrowed(scope, included);

        return document with
        {
            SheetCount = visibleCodes,
            SheetStates = visibleSheets.ToDictionary(s => s.Code, s => s.State, StringComparer.Ordinal),
            Sheets = document.Sheets is null ? null : visibleSheets,
            ErrorCount = narrowed ? null : document.ErrorCount,
            WarningCount = narrowed ? null : document.WarningCount,

            // ⛔ R-7: «хто й коли правив документ» — по ВСЬОМУ документу, тож розкриває редактора схованого
            // аркуша; для звуженого читача — null («—»), як і лічильники.
            ModifiedAt = narrowed ? null : document.ModifiedAt,
            ModifiedByDisplayName = narrowed ? null : document.ModifiedByDisplayName,
            OwnerDisplayName = narrowed ? null : document.OwnerDisplayName,
            ApproverDisplayName = narrowed ? null : document.ApproverDisplayName,

            // ⛔ Застарілість рахується по ВСІХ входах документа, а видимий вихід може від схованого не залежати:
            // для читача зі звуженням у проєкті - null (і поза фільтром/лічильником), а не здогад «за видимим».
            ResultsStale = narrowed || IsProjectNarrowed(scope) ? null : document.ResultsStale,
            ResultsStaleSince = narrowed || IsProjectNarrowed(scope) ? null : document.ResultsStaleSince,
        };
    }

    /// <summary>Застосовує межі до документів сторінки; роль без обмежень — без змін і без запитів.</summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="documents">Документи зі сховища.</param>
    /// <param name="periodKey">Період запиту.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<List<DocumentSummary>> ApplyAsync(
        IAccessDecisionService access, AccessProfile profile, IReadOnlyList<DocumentSummary> documents,
        int? periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0 || !HasRestrictions(profile))
        {
            return [.. documents];
        }

        var scopes = await ScopesAsync(access, profile, documents.Select(d => (d.ProjectId, d.Id)), periodKey, ct)
            .ConfigureAwait(false);

        return [.. documents.Select(d => For(d, scopes[d.ProjectId]))];
    }
}
