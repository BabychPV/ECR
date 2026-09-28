// src/Ecr.Application/Documents/DocumentHeaderHandlers.cs
using System.Globalization;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Documents;

/// <summary>Поточні значення шапки документа — усі поля версії шаблону з їхнім станом.</summary>
public sealed class GetDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає шапку документа.</summary>
    /// <exception cref="NotFoundException">Документа немає або він не видимий.</exception>
    public async Task<DocumentHeaderDto> HandleAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ Той самий шлях, що ValidateDocumentHandler: право перевіряється
        // ТУТ, а не лише в контролері (A7-53) — шапка несе зміст документа.
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);

        return HeaderVersion.ToDto(snapshot, values);
    }
}

/// <summary>Збирання відповіді шапки й текст значення для журналу — спільні для <c>GET</c> і <c>PATCH</c>.</summary>
internal static class HeaderVersion
{
    /// <summary>Відповідь: усі поля версії шаблону зі значеннями.</summary>
    public static DocumentHeaderDto ToDto(
        TemplateVersionSnapshot snapshot, IReadOnlyDictionary<int, Domain.ValueObjects.DocumentHeaderValueData> values)
    {
        var fields = snapshot.HeaderFields
            .OrderBy(f => f.Ordinal)
            .Select(f => new DocumentHeaderFieldDto(
                f.Id, f.Code, f.LabelL10n, f.DataType, f.IsRequired,
                values.TryGetValue(f.Id, out var value) ? HeaderValueMapping.ToRuleValue(value) : null,
                f.LookupRegistryDefId))
            .ToList();

        return new DocumentHeaderDto(fields);
    }

    /// <summary>Значення поля текстом для журналу; <c>null</c> — рядка немає або явна порожнеча.</summary>
    public static string? AuditText(Domain.ValueObjects.DocumentHeaderValueData? value)
        => HeaderValueMapping.ToRuleValue(value) switch
        {
            null => null,
            decimal n => Number(n),
            DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

    private static string Number(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);
}

/// <summary>
/// Оновлює значення полів шапки документа.
/// </summary>
/// <remarks>
/// ⚠ Судження про право (немає готового прецеденту рівно для цього рівня
/// грануляції): без окремого функціонального права, лише грант <c>Write</c>
/// на проєкт документа — той самий, грантово-орієнтований підхід (без
/// <c>PermissionCheck.RequireAsync</c>), що вже застосовує
/// <c>PatchCellsHandler</c> для звичайного редагування даних документа
/// (`02-contracts.md` §9: <c>PATCH …/cells</c> — «через
/// <c>IAccessDecisionService</c>», без права). Право
/// <c>Document.ChangeKey</c> навмисно НЕ використовується: воно означає
/// контрольовану, окремо аудитовану операцію зміни бізнес-ключа (ФВ-3.9), а
/// шапка — звичайне редагування даних, ближче до комірок, ніж до рекею.
/// В моделі доступу немає <c>ResourceKind.Document</c>, тож найближчий
/// рівень грануляції — проєктний, дослівно як у
/// <see cref="ChangeDocumentKeyHandler"/> для 404-проти-403.
///
/// ⛔ C2 (enterprise-аудит коректності). Доти грант був ЄДИНОЮ перевіркою:
/// шапку поданого чи затвердженого документа правили з <c>200</c>, у
/// журналі не лишалося нічого, формули з <c>HDR.*</c> не перераховувалися, а
/// дві правки з однієї версії мовчки затирали одна одну (останнє — окремим
/// кроком, бо потребує зміни контракту). Тепер:
/// <list type="number">
/// <item><b>Стан</b> — <see cref="EditRules.CanEdit"/>, те саме правило, що
/// для комірки. Шапка не має ні аркуша, ні періоду й належить документу
/// цілком, тому в рішення йде НАЙСУВОРІШИЙ стан серед усіх аркуш × період
/// документа (той самий принцип, що <c>DocumentKeyChange.EnsureChangeable</c>
/// для ключа, ФВ-3.9: подане вже бачили погоджувачі, а зріз подання несе й
/// шапку). Закритий період окремо шапку не замикає — закриті періоди не
/// перераховуються (ФВ-9.7), тож правка до них не доходить; замикає, лише
/// коли закрито ВСІ періоди проєкту.</item>
/// <item><b>Аудит</b> — <c>aud.SecurityEvent</c> <see cref="EventType"/> зі
/// старим і новим значенням кожного зміненого поля, у ТІЙ САМІЙ транзакції,
/// що й запис (як <c>DocumentKeyChanged</c>, але до коміту).</item>
/// <item><b>Перерахунок</b> — після коміту, повний (<c>IRecalculationJob</c>)
/// на кожен період, куди перерахунок має право писати: <c>HDR.*</c> читають і
/// формули шаблону (<c>RecalculationService</c>), і методології, а насіння
/// «змінена комірка» в шапки немає.</item>
/// </list>
/// </remarks>
public sealed class PatchDocumentHeaderHandler(
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IPeriodStore periods,
    IDocumentKeyStore documentLock,
    IDocumentDeletionStore workflowFacts,
    IUnitOfWork uow,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string EventType = "DocumentHeaderChanged";

    /// <summary>Оновлює шапку і повертає її повний, щойно збережений стан.</summary>
    /// <exception cref="NotFoundException">Документа немає, він не видимий, або код поля невідомий.</exception>
    /// <exception cref="AccessDeniedException">
    /// Анонімний запит; немає гранта на запис у проєкт документа; або стан документа
    /// правку не допускає — <c>ECR-ACCS-0403</c> з <c>reason</c> від <see cref="EditRules"/>.
    /// </exception>
    /// <exception cref="BusinessRuleException">Значення не відповідає типу чи обов'язковості поля.</exception>
    public async Task<DocumentHeaderDto> HandleAsync(
        long documentId, PatchDocumentHeaderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read)
        {
            throw new NotFoundException(
                ErrorCodes.DocumentNotFound, $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {document.ProjectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = document.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var project = await periods.FindProjectAsync(document.ProjectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.DocumentNotFound, $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });

        // ⚠ Швидка відмова ДО блокувань: симуляція, архів, закритий рік, грант.
        // Стан аркушів тут ще не відомий — його читає транзакція нижче під
        // блокуванням, і те саме правило питається вдруге вже з ним.
        EnsureEditable(profile, project, DocumentStatus.Draft);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var fieldsByCode = snapshot.HeaderFields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        var toSave = new Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData>();
        foreach (var change in request.Fields)
        {
            if (!fieldsByCode.TryGetValue(change.Code, out var field))
            {
                throw new NotFoundException(
                    ErrorCodes.HeaderFieldNotFound,
                    $"У версії шаблону {templateVersionId} немає поля шапки з кодом «{change.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0404.headerField",
                        ["templateVersionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                        ["headerFieldCode"] = change.Code,
                    });
            }

            var data = change.IsEmpty
                ? Domain.ValueObjects.DocumentHeaderValueData.Empty
                : HeaderValueReader.Read(change.Value, field) ?? Domain.ValueObjects.DocumentHeaderValueData.Empty;

            if (field.ValidateValue(data) is { } errorCode)
            {
                throw new BusinessRuleException(
                    errorCode,
                    $"Значення поля шапки «{field.Code}» не відповідає типу чи обов'язковості.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-HDR-0422.validationBlocked",
                        ["headerFieldCode"] = field.Code,
                    });
            }

            toSave[field.Id] = data;
        }

        var codeById = snapshot.HeaderFields.ToDictionary(f => f.Id, f => f.Code);
        var changed = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            changed = await PersistAsync(
                documentId, project, profile, toSave, codeById, userId, innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⚠ Після коміту й поза транзакцією — як у PatchCellsHandler: задача,
        // поставлена до коміту, під RCSI прочитала б стару шапку.
        if (changed > 0)
        {
            await EnqueueRecalculationAsync(documentId, project, userId, ct).ConfigureAwait(false);
        }

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        return HeaderVersion.ToDto(snapshot, values);
    }

    /// <summary>
    /// Тіло транзакції: блокування, стан, версія, запис, «дотик» документа й
    /// аудит. Повертає, скільки полів справді змінилося.
    /// </summary>
    /// <remarks>
    /// ⛔ Порядок блокувань — той самий, що в <see cref="ChangeDocumentKeyHandler"/>:
    /// спершу рядок документа (<c>UPDLOCK</c>), потім стани аркушів
    /// (<c>UPDLOCK, HOLDLOCK</c> по <c>DocumentId</c>). Перше серіалізує дві
    /// правки шапки між собою; друге не дає
    /// паралельному поданню вставити чи змінити стан аркуша, доки правка не
    /// зафіксована, — тобто подання або бачить уже нову шапку, або правка бачить
    /// уже поданий аркуш і відмовляє. Правкам комірок ні те, ні те не заважає:
    /// вони не пишуть <c>wf.ApprovalState</c>, а рядок документа «торкають»
    /// коротко.
    /// </remarks>
    private async Task<int> PersistAsync(
        long documentId,
        Domain.Entities.Documents.Project project,
        AccessProfile profile,
        Dictionary<int, Domain.ValueObjects.DocumentHeaderValueData> requested,
        Dictionary<int, string> codeById,
        int userId,
        CancellationToken ct)
    {
        _ = await documentLock.FindForUpdateAsync(documentId, ct).ConfigureAwait(false);
        var facts = await workflowFacts.LockWorkflowFactsAsync(documentId, ct).ConfigureAwait(false);

        EnsureEditable(profile, project, Strictest(facts.SheetStates));

        // ⚠ Читання ПІСЛЯ блокування: під RCSI знімок береться на початку
        // оператора, тож цей оператор бачить правку, яка зафіксувалася, поки
        // ми чекали на UPDLOCK, — і «старе значення» в журналі правдиве.
        var current = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);

        // ⚠ Лише справді змінені поля: запис «того самого» не лишає ні рядка
        // в журналі, ні перерахунку, ні нової дати зміни документа.
        var changes = requested
            .Where(pair => !Equals(current.GetValueOrDefault(pair.Key), pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        if (changes.Count == 0)
        {
            return 0;
        }

        var now = clock.UtcNow;
        await headers.SaveValuesAsync(documentId, changes, ct).ConfigureAwait(false);
        await documents.TouchAsync(documentId, userId, now, ct).ConfigureAwait(false);

        var fields = changes
            .OrderBy(pair => codeById.GetValueOrDefault(pair.Key, string.Empty), StringComparer.Ordinal)
            .Select(pair => new
            {
                code = codeById.GetValueOrDefault(pair.Key, string.Empty),
                oldValue = HeaderVersion.AuditText(current.GetValueOrDefault(pair.Key)),
                newValue = HeaderVersion.AuditText(pair.Value),
            })
            .ToList();

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now, EventType, TargetUserId: null, TargetRoleId: null,
                System.Text.Json.JsonSerializer.Serialize(new { documentId, projectId = project.Id, fields }),
                userId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return changes.Count;
    }

    /// <summary>
    /// Рішення «чи можна правити шапку» — <see cref="EditRules.CanEdit"/> на
    /// рівні проєкту (без аркуша, таблиці й колонки).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="project">Проєкт документа разом із періодами.</param>
    /// <param name="sheetStatus">Найсуворіший стан аркуша × період документа.</param>
    private static void EnsureEditable(
        AccessProfile profile, Domain.Entities.Documents.Project project, DocumentStatus sheetStatus)
    {
        // ⚠ Шапка не належить періоду: закритий рік (усі періоди Closed)
        // замикає її, окремий закритий місяць — ні (див. remarks класу).
        var periodState = project.Periods.Count > 0 && project.Periods.All(p => p.State == PeriodState.Closed)
            ? PeriodState.Closed
            : PeriodState.Open;

        var decision = EditRules.CanEdit(profile, new CellAccessContext(
            project.Id, SheetDefId: 0, TableDefId: 0, ColumnDefId: 0,
            project.Status, project.IsArchiving, periodState, OutOfAccessWindow: false,
            sheetStatus, ColumnIsComputed: false, ColumnIsReadOnly: false, RowIsReadOnly: false));

        if (decision.IsAllowed)
        {
            return;
        }

        if (decision.Reason is EditDenyReason.NoGrant or EditDenyReason.InsufficientGrantLevel)
        {
            // ⚠ Той самий код і ключ, що й перевірка гранта вище: для
            // користувача «немає гранта» — одна відмова, хоч би хто її спіймав.
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {project.Id}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = project.Id.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Код — той самий, що для комірок поданого аркуша (`ECR-ACCS-0403` із
        // `reason`); відрізняє лише ключ тексту: «комірок у батчі» тут немає.
        throw new AccessDeniedException(
            "ECR-ACCS-0403",
            $"Шапку документа не можна змінити: {decision.Reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.headerLocked",
                ["reason"] = decision.Reason.ToString(),
            });
    }

    /// <summary>Найсуворіший стан серед аркуш × період: <c>Approved</c> над <c>Submitted</c> над рештою.</summary>
    private static DocumentStatus Strictest(IEnumerable<Domain.Entities.Workflow.ApprovalState> states)
    {
        var result = DocumentStatus.Draft;
        foreach (var state in states)
        {
            if (state.Status == DocumentStatus.Approved)
            {
                return DocumentStatus.Approved;
            }

            if (state.Status == DocumentStatus.Submitted)
            {
                result = DocumentStatus.Submitted;
            }
        }

        return result;
    }

    /// <summary>
    /// Повний перерахунок документа за кожен період, куди перерахунок має право
    /// писати (<see cref="Calculations.RecalculationWritePolicy"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ Поданих аркушів тут немає за побудовою — інакше правку відхилило б
    /// <see cref="EnsureEditable"/>; закриті періоди відсіює правило (ФВ-9.7), а
    /// <c>Scheduled</c> пропускається, бо даних там ще немає. Сама задача
    /// перевіряє те саме правило вдруге на шляху запису.
    ///
    /// ⚠ Ціль витіснення — та сама пара «документ × період», що в
    /// <see cref="RecalculateDocumentHandler"/>: дві задачі над тим самим
    /// перемикали б актуальність прогону навперегін.
    /// </remarks>
    private async Task EnqueueRecalculationAsync(
        long documentId, Domain.Entities.Documents.Project project, int userId, CancellationToken ct)
    {
        foreach (var period in project.Periods.OrderBy(p => p.PeriodKeyValue))
        {
            if (period.State == PeriodState.Scheduled
                || Calculations.RecalculationWritePolicy.Check(
                    period.State, hasSubmittedSheets: false, hasClosedPeriodApproval: false)
                != Calculations.RecalculationWriteDenial.None)
            {
                continue;
            }

            await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                    RecalculateDocumentHandler.TargetOf(documentId, period.Key),
                    new
                    {
                        DocumentId = documentId,
                        PeriodKey = period.PeriodKeyValue,
                        TriggeredByUserId = (int?)userId,
                        SheetDefId = (int?)null,
                    },
                    ct,
                    userId)
                .ConfigureAwait(false);
        }
    }
}
