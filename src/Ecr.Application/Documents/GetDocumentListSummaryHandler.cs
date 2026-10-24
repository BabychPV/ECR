using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Зведення переліку документів за період (<c>BE-09</c>). Право <c>Document.View</c>.</summary>
public sealed class GetDocumentListSummaryHandler(
    IDocumentListSummaryStore summary, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Лічильники по документах, які користувач БАЧИТЬ.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі видимі.</param>
    /// <param name="periodKey">Період; без нього стан документа не визначений (<c>D-93</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentListSummaryResponse> HandleAsync(
        int? projectId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, ListDocumentsHandler.Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Межа видимості — ТА САМА функція, що й у переліку, і йде вона в
        // ЗАПИТ: смуга, порахована по всіх проєктах, розійшлася б із таблицею
        // під нею і розкрила б, скільки документів у чужих проєктах (§3.3).
        // ФВ-6.14: лише проєкти, де є і право перегляду.
        var visibleProjects = ListDocumentsHandler.ReadableProjects(profile, ListDocumentsHandler.Permission);

        var period = PeriodKey.Parse(periodKey).Value;

        // ⛔ Аркуш, схований від читача, не вносить свій стан у зведення документа: інакше
        // «Відхилено» невидимого аркуша робить документ відхиленим для того, хто його не бачить.
        var restrictions = await RestrictionsAsync(profile, projectId, visibleProjects, periodKey, ct)
            .ConfigureAwait(false);

        return await summary
            .SummarizeAsync(projectId, period, visibleProjects, restrictions, profile.UserId, ct)
            .ConfigureAwait(false);
    }

    /// <summary><c>null</c> — читач без обмежень (нуль запитів); інакше межі по кожному його проєкту.</summary>
    private async Task<SummaryRestrictions?> RestrictionsAsync(
        AccessProfile profile, int? projectId, IReadOnlyCollection<int> visibleProjects, int periodKey,
        CancellationToken ct)
    {
        if (!DocumentSheetVisibility.HasRestrictions(profile))
        {
            return null;
        }

        // ⛔ D-444: межі ПРОЄКТУ — лише за ролями (як `ListDocumentsHandler.HiddenFilterAsync`): зразок проєкту може
        // бути документом-наданням учасника, і його межі розкрили б схований аркуш в інших документах проєкту.
        var projects = visibleProjects.Where(p => projectId is null || p == projectId).ToList();
        var samples = await summary.SampleDocumentPerProjectAsync(projects, ct).ConfigureAwait(false);
        var scopes = await DocumentSheetVisibility
            .ScopesAsync(access, profile.WithoutParticipations(), samples.Select(s => (s.Key, s.Value)), periodKey, ct)
            .ConfigureAwait(false);

        var hidden = new List<(int ProjectId, string SheetCode)>();
        var narrowed = new List<int>();
        foreach (var (project, scope) in scopes)
        {
            var codes = scope.HiddenSheetCodes();
            hidden.AddRange(codes.Select(c => (project, c)));
            if (codes.Count > 0 || scope.HiddenTableIds().Count > 0 || scope.HiddenColumnIds().Count > 0)
            {
                narrowed.Add(project);
            }
        }

        return new SummaryRestrictions(
            hidden, narrowed, await ParticipantsAsync(access, profile, periodKey, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// D-444 (AN-131): документи учасника для зведення — з ВЛАСНИМИ межами кожного (схований аркуш документа-надання
    /// не вносить стан); <c>null</c> — надань немає (без ознаки профілю — без бази). Та сама множина й ті самі межі,
    /// що в переліку (<c>ListDocumentsHandler</c>, фільтр стану).
    /// </summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="periodKey">Період зведення.</param>
    /// <param name="ct">Токен скасування.</param>
    private static async Task<ParticipantSummaryScope?> ParticipantsAsync(
        IAccessDecisionService access, AccessProfile profile, int periodKey, CancellationToken ct)
    {
        var scopes = await DocumentSheetVisibility.ParticipantScopesAsync(access, profile, periodKey, ct).ConfigureAwait(false);
        if (scopes.Count == 0)
        {
            return null;
        }

        // ✎ D-446: застарілі результати — лише документи, видимі учасникові цілком (та сама умова, що виключає частково
        // схований документ-надання з фільтра `resultsStale` у переліку).
        return new ParticipantSummaryScope(
            [.. scopes.Keys],
            profile.DeniedProjectIds(),
            [.. scopes.SelectMany(s => s.Value.HiddenSheetCodes().Select(code => (s.Key, code)))],
            [.. scopes.Where(s => !DocumentSheetVisibility.IsProjectNarrowed(s.Value)).Select(s => s.Key).Order()]);
    }
}
