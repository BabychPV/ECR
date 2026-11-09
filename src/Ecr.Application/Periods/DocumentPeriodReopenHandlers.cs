// src/Ecr.Application/Periods/DocumentPeriodReopenHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Periods;

/// <summary>Чому відкрити місяць для документа зараз не можна.</summary>
public enum DocumentReopenDenial
{
    /// <summary>Немає права <c>Period.Reopen</c> у проєкті документа.</summary>
    NoPermission = 1,

    /// <summary>Місяць не закритий — відкривати нічого.</summary>
    PeriodNotClosed = 2,

    /// <summary>Проєкт архівований.</summary>
    ProjectArchived = 3,

    /// <summary>Місяця немає в календарі проєкту.</summary>
    PeriodMissing = 4,
}

/// <summary>Стан «місяць відкрито для документа» для сторінки документа.</summary>
/// <param name="PeriodKey">Місяць.</param>
/// <param name="Active">Діючий виняток; <c>null</c> — немає.</param>
/// <param name="CanOpen">Чи може цей користувач відкрити місяць для документа зараз.</param>
/// <param name="Denial">Чому не може; <c>null</c> — може.</param>
/// <param name="DefaultOpenUntil">Строк за замовчуванням — кінець доби майданчика (UTC).</param>
public sealed record DocumentPeriodReopenState(
    int PeriodKey,
    DocumentPeriodReopenView? Active,
    bool CanOpen,
    DocumentReopenDenial? Denial,
    DateTime DefaultOpenUntil);

/// <summary>
/// Читає стан «закритий місяць відкрито лише для цього документа» (приймальна 08.11 (i)).
/// </summary>
/// <remarks>
/// ⚠ Невидимий документ — <c>404</c>, як і відсутній (<see cref="DocumentVisibility"/>). Право не потрібне для
/// читання: банер «Reopened for this document until …» бачить кожен, хто бачить документ.
/// </remarks>
public sealed class GetDocumentPeriodReopenHandler(
    IDocumentPeriodReopenStore store,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стан для документа й місяця.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentPeriodReopenState> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var facts = await store.GetFactsAsync(documentId, periodKey, now, ct).ConfigureAwait(false)
                    ?? throw DocumentVisibility.NotFound(documentId);
        var active = await store.FindActiveViewAsync(documentId, periodKey, now, ct).ConfigureAwait(false);
        var denial = OpenDocumentPeriodReopenHandler.DenialOf(profile, facts);

        return new DocumentPeriodReopenState(
            periodKey, active, denial is null, denial,
            ReopenPeriodHandler.EndOfSiteDay(now, facts.TimeZoneId));
    }
}

/// <summary>
/// Відкриває закритий місяць для ОДНОГО документа (приймальна 08.11 (i); слово людини 08.11: «якщо мені потрібно
/// відкрити тільки для одного документу?», квіз «Так, додати»).
/// </summary>
/// <remarks>
/// ⛔ Право — <c>Period.Reopen</c> у проєкті документа (те саме, що відкриває період цілком; дія вужча, тож окремого
/// права не заведено). Гранта Manage на проєкт, як у <see cref="ReopenPeriodHandler"/>, тут не вимагається: зміна
/// стосується даних одного документа, а не структури проєкту; документ має бути видимим (інакше <c>404</c>).
/// <para>
/// ⚠ Період лишається <c>Closed</c>: решта документів місяця закрита. Виняток діє до <c>OpenUntil</c> і після нього
/// не діє в жодному рішенні; <c>PeriodStateJob</c> позначає його закритим і пише журнал.
/// </para>
/// <para>
/// ⚠ Повторне відкриття діючого винятку не відмовляє, а ЗАМІНЮЄ його новим строком і причиною (старий закривається
/// з причиною <see cref="DocumentPeriodReopen.ClosedReplaced"/>): «продовжити ще на день» — та сама дія.
/// </para>
/// </remarks>
public sealed class OpenDocumentPeriodReopenHandler(
    IDocumentPeriodReopenStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право дії — те саме, що відкриває період цілком.</summary>
    public const string Permission = ReopenPeriodHandler.Permission;

    /// <summary>Тип сутності в <c>aud.StructureChange</c>.</summary>
    public const string AuditEntityType = "DocumentPeriodReopen";

    /// <summary>Відкриває місяць для документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="openUntil">До якого моменту; <c>null</c> — до кінця доби майданчика.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentPeriodReopenView> HandleAsync(
        long documentId, int periodKey, string reason, DateTime? openUntil, CancellationToken ct)
    {
        var profile = await ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        var userId = profile.UserId;

        // ⛔ Невидимий документ — та сама відповідь, що й відсутній: до права й до стану місяця.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var facts = await store.GetFactsAsync(documentId, periodKey, now, ct).ConfigureAwait(false)
                    ?? throw DocumentVisibility.NotFound(documentId);

        PermissionCheck.RequireIn(profile, Permission, facts.ProjectId);

        switch (DenialOf(profile, facts))
        {
            case DocumentReopenDenial.ProjectArchived:
                throw new BusinessRuleException(
                    "ECR-PRD-0409", "Проєкт архівований: місяці в ньому не відкриваються.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-0409.projectArchived",
                        ["projectCode"] = facts.ProjectCode,
                    });

            case DocumentReopenDenial.PeriodMissing:
                throw Projects.ProjectVisibility.PeriodNotFound(periodKey);

            case DocumentReopenDenial.PeriodNotClosed:
                throw new BusinessRuleException(
                    "ECR-PRD-0409", $"Місяць {periodKey} не закритий: відкривати нічого.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-0409.reopenOnlyClosed",
                        ["state"] = facts.PeriodState?.ToString(),
                    });

            default:
                break;
        }

        var deadline = openUntil ?? ReopenPeriodHandler.EndOfSiteDay(now, facts.TimeZoneId);
        DocumentPeriodReopen? opened = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⚠ Унікальний фільтрований індекс тримає «один незакритий на документ × місяць»: попередній закривається
            // ОКРЕМИМ збереженням до вставки нового, інакше порядок команд пакета міг би зіткнутися з індексом.
            var previous = await store.FindOpenAsync(documentId, periodKey, innerCt).ConfigureAwait(false);
            if (previous is not null)
            {
                var expired = !previous.IsActiveAt(now);
                previous.Close(now, expired ? null : userId,
                    expired ? DocumentPeriodReopen.ClosedExpired : DocumentPeriodReopen.ClosedReplaced);
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
                await audit.WriteStructureChangeAsync(
                    CloseRecord(
                        previous, facts.TemplateVersionId, facts.BusinessKey, now, expired ? null : userId,
                        currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);
            }

            opened = DocumentPeriodReopen.Open(documentId, periodKey, deadline, reason, userId, now);
            store.Add(opened);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    now,
                    TemplateVersionId: facts.TemplateVersionId,
                    EntityType: AuditEntityType,
                    EntityId: opened.Id,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "Reopen",
                    OldJson: JsonSerializer.Serialize(new { state = nameof(PeriodState.Closed) }),
                    NewJson: JsonSerializer.Serialize(new
                    {
                        documentId,
                        document = facts.BusinessKey,
                        periodKey,
                        openUntil = opened.OpenUntil,
                    }),
                    ChangeReason: ReopenedReason(opened, facts.TimeZoneId),
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new DocumentPeriodReopenView(
            opened!.Id, periodKey, opened.OpenUntil, opened.Reason, userId, OpenedByName: null, opened.OpenedAt);
    }

    /// <summary>
    /// Запис журналу «виняток закрито» — спільний для заміни (тут) і закінчення строку (<c>PeriodStateJob</c>).
    /// </summary>
    /// <param name="exception">Закритий виняток.</param>
    /// <param name="templateVersionId">Версія шаблону проєкту документа.</param>
    /// <param name="businessKey">Номер документа.</param>
    /// <param name="utcNow">Момент закриття.</param>
    /// <param name="userId">Хто закрив; <c>null</c> — система (<c>0</c> у журналі, як у <c>PeriodStateJob.SystemUserId</c>).</param>
    /// <param name="correlationId">Кореляція.</param>
    public static StructureChangeRecord CloseRecord(
        DocumentPeriodReopen exception, int templateVersionId, string businessKey, DateTime utcNow, int? userId,
        string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new StructureChangeRecord(
            utcNow,
            TemplateVersionId: templateVersionId,
            EntityType: AuditEntityType,
            EntityId: exception.Id,
            ChangeClass: ChangeClass.Guarded,
            Operation: "Close",
            OldJson: JsonSerializer.Serialize(new
            {
                documentId = exception.DocumentId,
                document = businessKey,
                periodKey = exception.PeriodKeyValue,
                openUntil = exception.OpenUntil,
            }),
            NewJson: JsonSerializer.Serialize(new { state = nameof(PeriodState.Closed), closedAt = exception.ClosedAt }),
            ChangeReason: CloseReasonOf(exception.CloseReason),
            ChangedByUserId: userId ?? 0,
            CorrelationId: correlationId);
    }

    /// <summary>
    /// Причина запису «Reopen» конвертом (F13-p213 i5): причина людини і строк ISO — клієнт показує строк у локалі
    /// читача (<c>structureChangeReason.ts</c>). Причина ≤ 400 символів + конверт уміщаються в <c>nvarchar(1000)</c>.
    /// </summary>
    /// <remarks>
    /// ✎ debt-1109 i4-P3: ще й пояс майданчика (<c>zone</c>, IANA) — клієнт показує строк у часі САЙТУ, як рядок
    /// вкладки Periods; у поясі читача (19:00Z) було «Nov 10 00:00» проти «Nov 9 21:00» на Periods. Старі записи без
    /// <c>zone</c> — як раніше, у поясі читача.
    /// </remarks>
    internal static string ReopenedReason(DocumentPeriodReopen exception, string timeZoneId)
        => Sources.IntegrationAuditReason.Encode(
            "document.reopen.audit.reopened",
            ("reason", exception.Reason),
            ("until", DateTime.SpecifyKind(exception.OpenUntil, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)),
            ("zone", timeZoneId));

    /// <summary>
    /// Причина запису «Close» конвертом: «строк минув» / «замінено новим» словами мовою читача, а не сире «Expired»
    /// (F13-p213 i5). Невідома причина — як є.
    /// </summary>
    internal static string? CloseReasonOf(string? closeReason) => closeReason switch
    {
        DocumentPeriodReopen.ClosedExpired => Sources.IntegrationAuditReason.Encode("document.reopen.audit.closedExpired"),
        DocumentPeriodReopen.ClosedReplaced => Sources.IntegrationAuditReason.Encode("document.reopen.audit.closedReplaced"),
        DocumentPeriodReopen.ClosedManually => Sources.IntegrationAuditReason.Encode("document.reopen.audit.closedManually"),
        _ => closeReason,
    };

    /// <summary>Чому відкрити місяць для документа не можна; <c>null</c> — можна.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="facts">Факти документа й місяця.</param>
    internal static DocumentReopenDenial? DenialOf(AccessProfile profile, DocumentPeriodFacts facts)
    {
        if (!PermissionCheck.IsGrantedIn(profile, Permission, facts.ProjectId))
        {
            return DocumentReopenDenial.NoPermission;
        }

        if (facts.ProjectStatus == ProjectStatus.Archived)
        {
            return DocumentReopenDenial.ProjectArchived;
        }

        return facts.PeriodState switch
        {
            null => DocumentReopenDenial.PeriodMissing,
            PeriodState.Closed => null,
            _ => DocumentReopenDenial.PeriodNotClosed,
        };
    }

    /// <summary>Профіль поточного користувача; анонімний — <c>401</c>.</summary>
    internal static async Task<AccessProfile> ProfileAsync(
        IAccessDecisionService access, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        return await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
    }
}
