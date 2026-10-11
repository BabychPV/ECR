// src/Ecr.Application/Documents/EditAssignments/EditAssignmentAccess.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>
/// Спільні перевірки прав і форми відмов для керування передачами місяця (EA-C).
/// </summary>
/// <remarks>
/// ⛔ Одна відповідь «не знайдено» для невидимого документа, чужої передачі та передачі іншого
/// документа (IDOR, T7/T8): відмінність 404/403 не має розкривати існування.
/// </remarks>
internal static class EditAssignmentAccess
{
    /// <summary>Ключ каталогу: документа немає або він не видимий.</summary>
    public const string DocumentNotFoundMessageKey = "err.ECR-DOC-0404.document";

    /// <summary>Автентифікований користувач або 401.</summary>
    public static int RequireUser(Common.ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        return currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
    }

    /// <summary>Документ видимий профілю або «не знайдено».</summary>
    public static async Task<EditAssignmentDocumentRef> RequireVisibleDocumentAsync(
        IEditAssignmentManagementStore store, AccessProfile profile, long documentId, CancellationToken ct)
    {
        var document = await store.FindDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null || !profile.SeesDocumentsOf(document.ProjectId))
        {
            throw NotFound(documentId);
        }

        return document;
    }

    /// <summary>Помилка «не знайдено» (спільна для документа й передачі).</summary>
    public static NotFoundException NotFound(long documentId)
        => new(
            ErrorCodes.DocumentNotFound, $"Документ {documentId.ToString(CultureInfo.InvariantCulture)} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = DocumentNotFoundMessageKey,
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// «Може керувати передачами»: документ видимий ∧ рівень проєкту ≥ <c>Write</c> ∧ (автор ∨ рівень
    /// проєкту ≥ <c>Manage</c>) ∧ не симуляція ∧ проєкт не в архіві.
    /// </summary>
    /// <remarks>
    /// Роль, звужена аркушами/періодами (D-214), рівня проєкту не піднімає — тому не керує.
    /// </remarks>
    public static bool CanManage(AccessProfile profile, EditAssignmentDocumentRef document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return CanManage(
            profile, document.ProjectId, document.CreatedByUserId,
            document.ProjectStatus == ProjectStatus.Archived || document.IsArchiving);
    }

    /// <summary>
    /// Та сама функція над скалярами — для картки документа (GET /documents/{id}), де посилання
    /// <see cref="EditAssignmentDocumentRef"/> немає. ⛔ Єдине місце правила: картка й обробники не розходяться.
    /// </summary>
    public static bool CanManage(AccessProfile profile, int projectId, int authorUserId, bool projectReadOnly)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsSimulation || projectReadOnly || !profile.SeesDocumentsOf(projectId))
        {
            return false;
        }

        var level = profile.LevelFor(ResourceKind.Project, projectId);
        return level >= GrantLevel.Write
            && (authorUserId == profile.UserId || level >= GrantLevel.Manage);
    }

    /// <summary>Вимагає права керування або 403 <c>notEditAssignmentManager</c>.</summary>
    public static void RequireManager(AccessProfile profile, EditAssignmentDocumentRef document)
    {
        if (CanManage(profile, document))
        {
            return;
        }

        throw new AccessDeniedException(
            ErrorCodes.AccessDenied, "Керувати передачами місяця може автор документа або менеджер проєкту.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.notEditAssignmentManager",
                ["documentId"] = document.Id.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>422; <paramref name="messageKey"/> — повний ключ каталогу (сторож звіряє його із сідом).</summary>
    public static BusinessRuleException Invalid(string messageKey, string message, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var details = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                details[pair.Key] = pair.Value;
            }
        }

        return new BusinessRuleException(ErrorCodes.RequestInvalid, message, details);
    }

    /// <summary>409 «застаріла версія».</summary>
    public static ConcurrencyConflictException Stale(string actualVersion)
        => new(
            ErrorCodes.DocumentSubmitted, "Передачу змінили після того, як її відкрили.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0409.editAssignmentStale",
                ["version"] = actualVersion,
            });

    /// <summary>409 «перехід у цьому стані неможливий».</summary>
    public static ConcurrencyConflictException WrongState(string state)
        => new(
            ErrorCodes.DocumentSubmitted, "У поточному стані передачі ця дія неможлива.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0409.editAssignmentState",
                ["state"] = state,
            });

    /// <summary>Вимагає <c>baseVersion</c> і звіряє її з поточною.</summary>
    public static void RequireVersion(string? baseVersion, EditAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            throw Invalid("err.ECR-REQ-0422.editAssignmentBaseVersion", "Запит має нести baseVersion — версію передачі, яку бачив власник.");
        }

        var actual = EditAssignmentVersion.Of(assignment);
        if (!string.Equals(actual, baseVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw Stale(actual);
        }
    }

    /// <summary>Зріз із DTO або 422 <c>editAssignmentSliceInvalid</c>.</summary>
    public static EditAssignmentSlice ToSlice(EditAssignmentSliceDto? dto)
    {
        if (dto is null)
        {
            throw Invalid("err.ECR-REQ-0422.editAssignmentSliceInvalid", "Не вказано зріз передачі.");
        }

        try
        {
            return EditAssignmentSlice.Create(dto.SheetCode, dto.TableCode, dto.MonthNumber, dto.PeriodKey);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw Invalid("err.ECR-REQ-0422.editAssignmentSliceInvalid", "Зріз передачі задано некоректно.");
        }
    }

    /// <summary>Зріз як DTO.</summary>
    public static EditAssignmentSliceDto ToDto(EditAssignmentSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return new EditAssignmentSliceDto(slice.SheetCode, slice.TableCode, slice.MonthNumber, slice.PeriodKey);
    }

    /// <summary>Чи зріз зачіпає аркуш із цим кодом (для фільтра схованих від викликача аркушів).</summary>
    public static bool Matches(EditAssignment a, string sheetCode)
        => a.SheetCode is null || string.Equals(a.SheetCode, sheetCode, StringComparison.Ordinal);

    /// <summary>
    /// «У межах власних прав» (§3.3 DESIGN): для кожної колонки зрізу, яку можна передавати (не
    /// обчислювана, не readonly, місячна колонка потрібного місяця), рівень викликача без станів
    /// має бути ≥ <c>Write</c>.
    /// </summary>
    /// <param name="profile">Профіль передавача.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="snapshot">Знімок шаблону документа.</param>
    /// <param name="slice">Зріз.</param>
    /// <returns>Коди аркушів, які передавач бачить і яких не може писати, і ознака схованих аркушів зрізу.</returns>
    /// <exception cref="BusinessRuleException">Зрізу не існує чи його аркуш схований (<c>unknownSlice</c>).</exception>
    public static OwnRightsCheck SheetsBeyondOwnRights(
        AccessProfile profile, int projectId, TemplateVersionSnapshot snapshot, EditAssignmentSlice slice)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(slice);

        var scope = DocumentReadScope.For(profile, projectId, snapshot);
        var period = slice.PeriodKey is { } key ? new PeriodKey(key) : (PeriodKey?)null;

        var beyond = new SortedSet<string>(StringComparer.Ordinal);
        var matchedAnyColumn = false;
        var matchedVisibleSheet = false;
        var hiddenBeyond = false;

        foreach (var sheet in snapshot.Sheets)
        {
            if (sheet.IsDeleted
                || (slice.SheetCode is not null && !string.Equals(sheet.Code, slice.SheetCode, StringComparison.Ordinal)))
            {
                continue;
            }

            var sheetVisible = scope.CanReadSheetCode(sheet.Code);
            if (sheetVisible)
            {
                matchedVisibleSheet = true;
            }

            foreach (var table in sheet.Tables)
            {
                if (table.IsDeleted
                    || (slice.TableCode is not null && !string.Equals(table.Code, slice.TableCode, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var column in table.Columns)
                {
                    if (column.IsComputed || column.IsReadOnly)
                    {
                        continue;
                    }

                    if (slice.MonthNumber is { } month && !(column.IsMonthColumn && column.MonthNumber == month))
                    {
                        continue;
                    }

                    // Прихований аркуш не називається й не рахується збігом для користувача (R9), але
                    // й не передається: власних прав на нього немає.
                    if (!sheetVisible)
                    {
                        hiddenBeyond = true;
                        continue;
                    }

                    matchedAnyColumn = true;

                    var context = new CellAccessContext(
                        projectId, sheet.Id, table.Id, column.Id,
                        ProjectStatus.Active, IsArchiving: false, PeriodState.Open, OutOfAccessWindow: false,
                        DocumentStatus.Draft, ColumnIsComputed: false, ColumnIsReadOnly: false, RowIsReadOnly: false)
                    {
                        SheetCode = sheet.Code,
                        Period = period,
                    };

                    if (EditRules.Effective(profile, context) < GrantLevel.Write)
                    {
                        beyond.Add(sheet.Code);
                    }
                }
            }
        }

        if (!matchedVisibleSheet || !matchedAnyColumn)
        {
            throw Invalid("err.ECR-REQ-0422.editAssignmentUnknownSlice", "Зрізу з такими параметрами в документі немає.");
        }

        return new OwnRightsCheck([.. beyond], hiddenBeyond);
    }
}

/// <summary>Результат перевірки «у межах власних прав».</summary>
/// <param name="VisibleSheetCodes">Видимі передавачу аркуші, які він не може писати.</param>
/// <param name="HiddenSheetsInSlice">У зріз потрапляє аркуш, схований від передавача (його коди не називаються).</param>
internal sealed record OwnRightsCheck(IReadOnlyList<string> VisibleSheetCodes, bool HiddenSheetsInSlice)
{
    /// <summary>Передача виходить за власні права передавача.</summary>
    public bool Exceeds => VisibleSheetCodes.Count > 0 || HiddenSheetsInSlice;
}