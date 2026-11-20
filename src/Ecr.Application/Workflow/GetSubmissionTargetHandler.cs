// src/Ecr.Application/Workflow/GetSubmissionTargetHandler.cs
using Ecr.Application.Documents;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Відкритий (<c>Open</c>/<c>Grace</c>) період у виборі подання.</summary>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="State">Ефективний стан періоду.</param>
/// <param name="SheetStatus">Стан аркуша за цей період.</param>
/// <param name="Submittable">Аркуш за цей період можна подати зараз (неподаний, проєкт не архівний).</param>
/// <param name="UnapprovedSourcePeriodKey">✎ D-6: місяць документа-джерела HSE400, ще не затверджений, через який сервер відхилить подання цього періоду (sourceMonthNotApproved); null — перешкоди немає.</param>
public sealed record SubmissionPeriodDto(
    int PeriodKey, PeriodState State, DocumentStatus SheetStatus, bool Submittable, int? UnapprovedSourcePeriodKey = null);

/// <summary>Статус місячної колонки (D-441): стан аркуша за період місяця колонки.</summary>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="MonthNumber">Місяць колонки 1..12.</param>
/// <param name="PeriodKey">Період, якому відповідає колонка.</param>
/// <param name="SheetStatus">Стан аркуша за цей період.</param>
/// <param name="CanDecide">✎ D-6: поточний користувач може затвердити чи відхилити аркуш за цей місяць (те саме правило, що POST approve).</param>
/// <param name="CanRecall">✎ D-6: поточний користувач може відкликати подання аркуша за цей місяць (те саме правило, що POST recall).</param>
/// <param name="CanReturn">✎ D-6: поточний користувач може повернути ЗАТВЕРДЖЕНИЙ аркуш за цей місяць у роботу (те саме правило, що POST reopen).</param>
public sealed record MonthColumnStatusDto(
    int TableDefId,
    string ColumnCode,
    byte MonthNumber,
    int PeriodKey,
    DocumentStatus SheetStatus,
    bool CanDecide = false,
    bool CanRecall = false,
    bool CanReturn = false);

/// <summary>Цільовий період подання аркуша і статуси місячних колонок (D-441).</summary>
/// <param name="TargetPeriodKey">Період, який подасть «Подати»: найраніший неподаний <c>Open</c>/<c>Grace</c>; <c>null</c> — подавати нічого.</param>
/// <param name="NextPeriodKey">Наступний неподаний після цільового — для попередження «наступний — …».</param>
/// <param name="Periods">Відкриті періоди проєкту за зростанням (вибір іншого періоду — серед <c>Submittable</c>).</param>
/// <param name="MonthColumns">Місячні колонки таблиць <c>MonthsInColumns</c> аркуша зі станом свого періоду.</param>
public sealed record SubmissionTargetDto(
    int? TargetPeriodKey,
    int? NextPeriodKey,
    IReadOnlyList<SubmissionPeriodDto> Periods,
    IReadOnlyList<MonthColumnStatusDto> MonthColumns);

/// <summary>
/// Що подасть кнопка «Подати» на аркуші (D-441). Право <c>Document.View</c>.
/// </summary>
/// <remarks>
/// ⛔ Рішення ЯКИЙ період подавати — на сервері, а не в клієнті: стан періоду для подання — ЕФЕКТИВНИЙ
/// (за датами, F-08), і клієнт, який брав би збережений стан із календаря, пропонував би період, що
/// <c>EditRules.CanSubmit</c> відхилить. Саме подання (<c>POST …/submit</c>) лишається з явним
/// <c>periodKey</c>: користувач може вибрати інший відкритий період.
///
/// ⚠ Видимість — та сама, що в подання: документ (<c>404</c>), склад і схований аркуш
/// (<c>404 sheetNotInDocument</c>). Статуси місячних колонок схованої таблиці не віддаються.
/// </remarks>
public sealed class GetSubmissionTargetHandler(
    IWorkflowStore workflow,
    IDocumentStore documents,
    IMetadataCache metadata,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IClock clock,
    // ✎ D-6: місяць джерела HSE400 не затверджено — подання періоду сервер відхилить; `null` — тестові фабрики.
    ISourceIssueReader? sourceIssues = null)
{
    /// <summary>Право — читання документа (<c>Document.View</c>).</summary>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Цільовий період подання аркуша.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період, відкритий на екрані: його рік задає місяці колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetCopyId">Копія аркуша (RS): стани періодів — її власні; <c>null</c> — оригінал.</param>
    public async Task<SubmissionTargetDto> HandleAsync(
        long documentId, int sheetDefId, int periodKey, CancellationToken ct, long? sheetCopyId = null)
    {
        var key = PeriodKey.Parse(periodKey);
        var userId = EditAssignmentAccess.RequireUser(currentUser);

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        if (!await documents.HasSheetAsync(documentId, sheetDefId, ct).ConfigureAwait(false))
        {
            throw DocumentVisibility.SheetNotInDocument(documentId, sheetDefId);
        }

        await DocumentVisibility
            .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, key, ct)
            .ConfigureAwait(false);

        var calendar = await workflow
                           .GetSubmissionCalendarAsync(documentId, sheetDefId, clock.UtcNow, ct, sheetCopyId)
                           .ConfigureAwait(false)
                       ?? throw DocumentVisibility.DocumentNotFound(documentId);

        var choice = SubmissionPeriodSelector.Choose(calendar);

        var templateVersionId = await Documents.DocumentPeriodVersion.GetAsync(documents, documentId, key.Value, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId);

        IReadOnlyList<MonthColumnStatusDto> months = [];
        if (sheet is not null)
        {
            // S6: межі читання — лише для читача з обмеженнями нижче проєкту (решта не платить запитом).
            Func<int, bool> canReadTable = _ => true;
            if (DocumentSheetVisibility.HasRestrictions(profile))
            {
                var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key);
                canReadTable = readable.CanReadTable;
            }

            months = SubmissionPeriodSelector.MonthColumns(calendar, sheet, key.Year, canReadTable);

            // ✎ D-6: що САМЕ ЦЯ людина може зробити з кожним місяцем (затвердити/відхилити, відкликати, повернути) —
            // право в річному аркуші своє на кожен місяць (крок маршруту, автор подання), тож клієнт не вгадує його.
            months = await MonthDecisionRights
                .ApplyAsync(months, calendar, access, workflow, documents, profile, userId, documentId, sheetDefId, sheetCopyId, ct)
                .ConfigureAwait(false);
        }

        var periods = await SubmissionSourceGate.ApplyAsync(sourceIssues, documentId, choice.Periods, ct).ConfigureAwait(false);

        return new SubmissionTargetDto(choice.TargetPeriodKey, choice.NextPeriodKey, periods, months);
    }
}

/// <summary>
/// ✎ D-6: місяць документа-джерела HSE400 не затверджено — подання періоду сервер відхилить
/// (<see cref="SourceMonthApprovalCheck"/>, <c>sourceMonthNotApproved</c>). Відповідь «Подати» несе цей місяць для КОЖНОГО
/// періоду, який можна подати, щоб кнопка була недоступна саме тоді, коли відмовить сервер, — і для періоду цілі подання,
/// а не лише відкритого на екрані.
/// </summary>
/// <remarks>
/// ⚠ Читання — <see cref="ISourceIssueReader"/> (ті самі правила, що ворота подання; один пакет на всі періоди, за
/// <c>Any</c> — один дрібний запит). Ворота відмовляють, коли місяць джерела ЗАРАЗ не затверджено, незалежно від виду
/// рядка: тож ознака — рядок без <see cref="SourceIssueRow.SourceApprovedAt"/>. Формульні помилки сюди не входять:
/// подання рахує заново.
/// </remarks>
public static class SubmissionSourceGate
{
    /// <summary>Періоди з місяцем джерела, через який подання буде відхилено.</summary>
    /// <param name="reader">Читач проблем джерела; <c>null</c> — перевірки немає.</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periods">Відкриті періоди відповіді.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<SubmissionPeriodDto>> ApplyAsync(
        ISourceIssueReader? reader, long documentId, IReadOnlyList<SubmissionPeriodDto> periods, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var submittable = periods.Where(p => p.Submittable).Select(p => p.PeriodKey).ToList();
        if (reader is null || submittable.Count == 0)
        {
            return periods;
        }

        var rows = await reader.ListSourceIssuesAsync(documentId, submittable, ct).ConfigureAwait(false);
        var blocked = rows
            .Where(r => r.SourceApprovedAt is null)
            .GroupBy(r => r.PeriodKey)
            .ToDictionary(g => g.Key, g => g.Min(r => r.SourcePeriodKey));
        if (blocked.Count == 0)
        {
            return periods;
        }

        return
        [
            .. periods.Select(p => p.Submittable && blocked.TryGetValue(p.PeriodKey, out var source)
                ? p with { UnapprovedSourcePeriodKey = source }
                : p),
        ];
    }
}
