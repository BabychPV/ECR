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
public sealed record SubmissionPeriodDto(int PeriodKey, PeriodState State, DocumentStatus SheetStatus, bool Submittable);

/// <summary>Статус місячної колонки (D-441): стан аркуша за період місяця колонки.</summary>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="MonthNumber">Місяць колонки 1..12.</param>
/// <param name="PeriodKey">Період, якому відповідає колонка.</param>
/// <param name="SheetStatus">Стан аркуша за цей період.</param>
public sealed record MonthColumnStatusDto(
    int TableDefId, string ColumnCode, byte MonthNumber, int PeriodKey, DocumentStatus SheetStatus);

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
    IClock clock)
{
    /// <summary>Право — читання документа (<c>Document.View</c>).</summary>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Цільовий період подання аркуша.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період, відкритий на екрані: його рік задає місяці колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SubmissionTargetDto> HandleAsync(
        long documentId, int sheetDefId, int periodKey, CancellationToken ct)
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
                           .GetSubmissionCalendarAsync(documentId, sheetDefId, clock.UtcNow, ct)
                           .ConfigureAwait(false)
                       ?? throw DocumentVisibility.DocumentNotFound(documentId);

        var choice = SubmissionPeriodSelector.Choose(calendar);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
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
        }

        return new SubmissionTargetDto(choice.TargetPeriodKey, choice.NextPeriodKey, choice.Periods, months);
    }
}
