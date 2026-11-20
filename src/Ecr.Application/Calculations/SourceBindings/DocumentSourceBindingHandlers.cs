// src/Ecr.Application/Calculations/SourceBindings/DocumentSourceBindingHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Application.Calculations.SourceBindings;

/// <summary>Зв'язка «документ-джерело → довідник» для адміністратора (B80 N1).</summary>
/// <param name="Id">Зв'язка.</param>
/// <param name="Code">Код зв'язки.</param>
/// <param name="SourceTemplateCode">Код шаблону-джерела.</param>
/// <param name="RegistryCode">Код довідника, рядки якого замінює документ.</param>
/// <param name="SourceState">Стан-ворота: <c>any</c> або <c>approvedOnly</c>.</param>
/// <param name="Policy">Політика згоди: <c>None</c>, <c>Warn</c>, <c>ConfirmWithReason</c>, <c>Block</c>.</param>
/// <param name="ReadFromDocument">Біт зв'язки «читати з документа».</param>
/// <param name="RecalculateDependents">Біт зв'язки «ставити перерахунок залежних».</param>
/// <param name="ReadFromDocumentEffective">
/// Дієве значення «читати з документа». Ключ конфігурації виведено (P-0118-31/33) — дорівнює біту зв'язки.
/// </param>
/// <param name="ReadFromDocumentSource">
/// <c>binding</c> — біт зв'язки (єдине значення в продукті); <c>configuration</c> лишається в контракті для сумісності
/// й більше не видається.
/// </param>
/// <param name="ConsumerTemplateCodes">Коди шаблонів-споживачів.</param>
/// <param name="UpdatedAt">Остання зміна стану-воріт, UTC; <c>null</c> — не змінювався.</param>
/// <param name="UpdatedByUserId">Хто змінив востаннє.</param>
public sealed record DocumentSourceBindingDto(
    int Id,
    string Code,
    string SourceTemplateCode,
    string RegistryCode,
    string SourceState,
    string Policy,
    bool ReadFromDocument,
    bool RecalculateDependents,
    bool ReadFromDocumentEffective,
    string ReadFromDocumentSource,
    IReadOnlyList<string> ConsumerTemplateCodes,
    DateTime? UpdatedAt,
    int? UpdatedByUserId);

/// <summary>Запит перемикання стану-воріт.</summary>
/// <param name="SourceState"><c>any</c> або <c>approvedOnly</c>.</param>
public sealed record SwitchDocumentSourceStateRequest(string SourceState);

/// <summary>Результат перемикання.</summary>
/// <param name="Binding">Зв'язка після перемикання.</param>
/// <param name="Changed"><c>false</c> — стан уже був таким; нічого не записано й не поставлено.</param>
/// <param name="QueuedYears">Роки, за які поставлено перерахунок залежних (злита задача на довідник × рік).</param>
/// <param name="FailedYears">
/// Роки, за які задачу НЕ вдалося поставити після коміту (черга недоступна): стан уже змінено, тож залежні за ці роки
/// адміністратор перераховує вручну; порожньо — усе поставлено.
/// </param>
public sealed record SwitchDocumentSourceStateResponse(
    DocumentSourceBindingDto Binding, bool Changed, IReadOnlyList<int> QueuedYears, IReadOnlyList<int> FailedYears);

/// <summary>Запит перемикання бітів зв'язки (B179 L5); <c>null</c> — біт не чіпати, хоч один має бути заданий.</summary>
/// <param name="ReadFromDocument">Читати склад з документа-джерела (<c>true</c>) чи з рядків довідника (<c>false</c>).</param>
/// <param name="RecalculateDependents">Ставити автоматичний перерахунок залежних (аварійний вимикач).</param>
public sealed record SwitchDocumentSourceFlagsRequest(bool? ReadFromDocument, bool? RecalculateDependents);

/// <summary>Результат перемикання бітів.</summary>
/// <param name="Binding">Зв'язка після перемикання.</param>
/// <param name="Changed"><c>false</c> — біти вже були такими; нічого не записано й не поставлено.</param>
/// <param name="QueuedYears">Роки, за які поставлено перерахунок залежних.</param>
/// <param name="FailedYears">
/// Роки, за які задачу НЕ вдалося поставити після коміту (черга недоступна): біти вже змінено, тож залежні за ці роки
/// адміністратор перераховує вручну; порожньо — усе поставлено.
/// </param>
public sealed record SwitchDocumentSourceFlagsResponse(
    DocumentSourceBindingDto Binding, bool Changed, IReadOnlyList<int> QueuedYears, IReadOnlyList<int> FailedYears);

/// <summary>Спільне для обробників зв'язок.</summary>
public static partial class DocumentSourceBindings
{
    /// <summary>
    /// Право керування зв'язками (і читання, і перемикання): глобальне, не небезпечне — з вбудованих ролей його має
    /// лише <c>SystemAdministrator</c> (шаблон <c>%</c>), рішення координатора R4.6.
    /// </summary>
    public const string Permission = "Calculation.ManageSourceBinding";

    /// <summary>Подія журналу безпеки.</summary>
    public const string ChangedEventType = "DocumentSourceBindingChanged";

    /// <summary>Назва стану в контракті.</summary>
    /// <param name="state">Стан.</param>
    public static string StateName(DocumentSourceState state)
        => state == DocumentSourceState.ApprovedOnly ? "approvedOnly" : "any";

    /// <summary>Розбирає назву стану з контракту; невідома — <c>null</c>.</summary>
    /// <param name="value">Назва.</param>
    public static DocumentSourceState? ParseState(string? value)
        => value switch
        {
            "any" => DocumentSourceState.Any,
            "approvedOnly" => DocumentSourceState.ApprovedOnly,
            _ => null,
        };

    /// <summary>Складає DTO.</summary>
    /// <param name="view">Зв'язка.</param>
    /// <param name="overrides">Перекриття ключами.</param>
    public static DocumentSourceBindingDto ToDto(DocumentSourceBindingView view, IDocumentSourceFlagOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(overrides);
        return new DocumentSourceBindingDto(
            view.Id,
            view.Code,
            view.SourceTemplateCode,
            view.RegistryCode,
            StateName(view.SourceState),
            view.Policy.ToString(),
            view.ReadFromDocument,
            view.RecalculateDependents,
            overrides.ReadFromDocument ?? view.ReadFromDocument,
            overrides.ReadFromDocument is null ? "binding" : "configuration",
            view.ConsumerTemplateCodes,
            view.UpdatedAt,
            view.UpdatedByUserId);
    }

    /// <summary>
    /// ⛔ D-557 (рецензія): постановка задач ПІСЛЯ коміту. Стан уже закомічено, тож збій черги (Quartz, `JobProgress`) не може
    /// стати 500: повтор дав би <c>changed = false</c> і задача не стала б ніколи, а решта років, скидання кешу воріт і хук
    /// джерела пропустилися б. Кожен рік ізольований: збій — Warning у журнал (рік і зв'язка) і рік у <c>failed</c>;
    /// скасування (<see cref="OperationCanceledException"/>) пробрасується як є.
    /// </summary>
    /// <param name="jobs">Планувальник задач.</param>
    /// <param name="pending">Заплановані за роками задачі.</param>
    /// <param name="userId">Хто змінив.</param>
    /// <param name="bindingCode">Код зв'язки (для журналу).</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<(List<int> Queued, List<int> Failed)> EnqueueAfterCommitAsync(
        IBackgroundJobScheduler jobs,
        IReadOnlyList<(int Year, string Target, RegistryImpactRecalculationRequest Request)> pending,
        int userId,
        string bindingCode,
        ILogger logger,
        CancellationToken ct)
    {
        var queued = new List<int>();
        var failed = new List<int>();
        foreach (var (year, target, request) in pending)
        {
            try
            {
                await jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(target, request, ct, createdByUserId: userId)
                    .ConfigureAwait(false);
                queued.Add(year);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(year);
                LogEnqueueFailed(logger, ex, bindingCode, year);
            }
        }

        return (queued, failed);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Зв'язка {BindingCode}: стан змінено й закомічено, але задачу перерахунку залежних за {Year} рік не поставлено; адміністратор перераховує залежні вручну.")]
    private static partial void LogEnqueueFailed(ILogger logger, Exception exception, string bindingCode, int year);

    /// <summary>404 зв'язки.</summary>
    /// <param name="id">Зв'язка.</param>
    internal static NotFoundException NotFound(int id)
        => new(
            ErrorCodes.MethodologyVersionNotFound,
            $"Зв'язки документа-джерела {id} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0404.sourceBinding",
                ["bindingId"] = id.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary><c>GET /api/v1/document-source-bindings</c> — перелік зв'язок. Право <see cref="DocumentSourceBindings.Permission"/>.</summary>
public sealed class ListDocumentSourceBindingsHandler(
    IDocumentSourceBindingStore store,
    IDocumentSourceFlagOverrides overrides,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право (те саме, що <see cref="DocumentSourceBindings.Permission"/>).</summary>
    public const string Permission = "Calculation.ManageSourceBinding";

    /// <summary>Перелік.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<DocumentSourceBindingDto>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var views = await store.ListAsync(ct).ConfigureAwait(false);
        return [.. views.Select(v => DocumentSourceBindings.ToDto(v, overrides))];
    }
}

/// <summary>
/// <c>PUT /api/v1/document-source-bindings/{id}/source-state</c> — явне перемикання стану-воріт (R3.3, R4.6).
/// Право <see cref="DocumentSourceBindings.Permission"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ На <c>approvedOnly</c> — лише коли документ-джерело, який завантажувач обере за ПОТОЧНИЙ рік, є і лежить у
/// МІСЯЧНОМУ проєкті («спершу перебудова», R3.0 п. 2: річний аркуш цілком не погоджується), а його версія шаблону має
/// погодження (джерело без погодження — «остаточне», RW.6, ворота для нього не вмикаються). Назад на <c>any</c> —
/// завжди.
/// </para>
/// <para>
/// ⚠ Справжня зміна: подія <see cref="DocumentSourceBindings.ChangedEventType"/> у журналі безпеки в тій самій транзакції
/// (старий і новий стан, автор) і — коли читання з документа діє — злита задача перерахунку залежних за кожен рік
/// джерела (<see cref="RegistryImpactRecalculationTarget.ForSourceChange"/>; та сама задача, що пропускає затверджені аркуші
/// Land і позначає подані). Повтор того самого стану нічого не пише.
/// </para>
/// </remarks>
public sealed class SwitchDocumentSourceStateHandler(
    IDocumentSourceBindingStore store,
    IDocumentSourceFlagOverrides overrides,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IClock clock,
    // B179 L2 perf: скидання кешу воріт хука джерела цього процесу (інші — за TTL); без нього (ручна збірка) — нічого.
    ISourceBindingGateInvalidation? gate = null,
    ILogger<SwitchDocumentSourceStateHandler>? logger = null)
{
    /// <summary>Право (те саме, що <see cref="DocumentSourceBindings.Permission"/>).</summary>
    public const string Permission = "Calculation.ManageSourceBinding";

    /// <summary>Перемикає.</summary>
    /// <param name="id">Зв'язка.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SwitchDocumentSourceStateResponse> HandleAsync(
        int id, SwitchDocumentSourceStateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var target = DocumentSourceBindings.ParseState(request.SourceState)
                     ?? throw new BusinessRuleException(
                         ErrorCodes.RequestInvalid,
                         "sourceState: очікується any або approvedOnly.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-REQ-0422.sourceStateInvalid",
                             ["field"] = "sourceState",
                         });

        var changed = false;
        var queued = new List<int>();
        var enlist = jobs.EnlistsInCallerTransaction;
        var bindingCode = id.ToString(CultureInfo.InvariantCulture);
        var pending = new List<(int Year, string Target, RegistryImpactRecalculationRequest Request)>();
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            pending.Clear();
            queued.Clear();
            var binding = await store.LockAsync(id, innerCt).ConfigureAwait(false)
                          ?? throw DocumentSourceBindings.NotFound(id);
            bindingCode = binding.Code;

            if (target == DocumentSourceState.ApprovedOnly && binding.SourceState != target)
            {
                var year = clock.UtcNow.Year;
                var chosen = await store.ChosenSourceDocumentAsync(id, year, innerCt).ConfigureAwait(false);
                if (chosen is null)
                {
                    throw Refusal(id, "err.ECR-CALC-0409.sourceBindingNoDocument", year, null);
                }

                if (chosen.PeriodKind != PeriodKind.Monthly)
                {
                    throw Refusal(id, "err.ECR-CALC-0409.sourceBindingNotMonthly", year, chosen.DocumentId);
                }

                if (chosen.WorkflowMode == WorkflowMode.None)
                {
                    throw Refusal(id, "err.ECR-CALC-0409.sourceBindingWorkflowNone", year, chosen.DocumentId);
                }
            }

            var old = binding.SourceState;
            changed = binding.SwitchState(target, userId, clock.UtcNow);
            if (!changed)
            {
                return;
            }

            await store.SaveAsync(innerCt).ConfigureAwait(false);

            if (overrides.ReadFromDocument ?? binding.ReadFromDocument)
            {
                foreach (var year in await store.SourceYearsAsync(id, innerCt).ConfigureAwait(false))
                {
                    // ✎ B179 L2: зміна джерела за зв'язкою — споживачі з графа (а не з ручного переліку/результатів), увесь рік.
                    var pendingJob = (
                        RegistryImpactRecalculationTarget.ForSourceChange(binding.Id, year, month: null),
                        new RegistryImpactRecalculationRequest(
                            binding.RegistryDefId,
                            [],
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{binding.Code}: source state {DocumentSourceBindings.StateName(old)} -> {DocumentSourceBindings.StateName(target)}"),
                            year,
                            binding.Id));
                    pending.Add((year, pendingJob.Item1, pendingJob.Item2));

                    // ✎ D-557: черга в базі приєднується до транзакції; Quartz — ні (див. нижче, після коміту).
                    if (enlist)
                    {
                        await jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                                pendingJob.Item1, pendingJob.Item2, innerCt, createdByUserId: userId)
                            .ConfigureAwait(false);
                    }

                    queued.Add(year);
                }
            }

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    DocumentSourceBindings.ChangedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        bindingId = id,
                        code = binding.Code,
                        oldState = DocumentSourceBindings.StateName(old),
                        newState = DocumentSourceBindings.StateName(target),
                        queuedYears = queued,
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ D-557 (D-3 стенда Land): задача Quartz стартує негайно, а її запис прогресу (`itg.JobProgress`, Queued) лежить
        // у ще не завершеній транзакції — за RCSI задача його не бачила, її `StartAsync` вставляв дубль ключа й падав;
        // рядок лишався `Queued` навіки, а залежні не перераховувались. Поза базою ставимо ПІСЛЯ коміту.
        // ⛔ Збій постановки не скасовує вже закомічений стан: рік іде у `FailedYears`, решта років, скидання кешу й хук — далі.
        IReadOnlyList<int> queuedYears = queued;
        IReadOnlyList<int> failedYears = [];
        if (!enlist)
        {
            var enqueued = await DocumentSourceBindings.EnqueueAfterCommitAsync(
                    jobs, pending, userId, bindingCode, logger ?? (ILogger)NullLogger.Instance, ct)
                .ConfigureAwait(false);
            queuedYears = enqueued.Queued;
            failedYears = enqueued.Failed;
        }

        // Після коміту: хук джерела бачить новий стан одразу (за ApprovedOnly мовчить, назад на Any — знову ставить).
        if (changed)
        {
            gate?.Invalidate();
        }

        var view = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(v => v.Id == id)
                   ?? throw DocumentSourceBindings.NotFound(id);
        return new SwitchDocumentSourceStateResponse(DocumentSourceBindings.ToDto(view, overrides), changed, queuedYears, failedYears);
    }

    private static DomainException Refusal(int id, string messageKey, int year, long? documentId)
    {
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = messageKey,
            ["bindingId"] = id.ToString(CultureInfo.InvariantCulture),
            ["year"] = year.ToString(CultureInfo.InvariantCulture),
        };
        if (documentId is { } doc)
        {
            details["documentId"] = doc.ToString(CultureInfo.InvariantCulture);
        }

        return new DomainException(
            ErrorCodes.MethodologyConflict,
            $"Зв'язка {id}: «лише затверджене» не вмикається ({messageKey}, рік {year}).",
            details);
    }
}

/// <summary>
/// <c>PUT /api/v1/document-source-bindings/{id}/flags</c> — перемикання бітів «читати з документа» і «перераховувати
/// залежних» (B179 L5). Право <see cref="DocumentSourceBindings.Permission"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Справжня зміна: подія <see cref="DocumentSourceBindings.ChangedEventType"/> у журналі безпеки в тій самій транзакції
/// (<c>kind = flags</c>, старі й нові біти, автор). Зміна «читати з документа» міняє склад, який бачать споживачі, тож —
/// коли перерахунок залежних увімкнено — ставить ту саму злиту задачу на кожен рік джерела, що й перемикання стану.
/// Зміна лише «перераховувати залежних» перерахунку не ставить (залежні документи, що застаріли за вимкнення,
/// адміністратор перераховує сам).
/// </para>
/// <para>⚠ Процесний кеш воріт хука джерела скидається після коміту.</para>
/// </remarks>
public sealed class SwitchDocumentSourceFlagsHandler(
    IDocumentSourceBindingStore store,
    IDocumentSourceFlagOverrides overrides,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IClock clock,
    ISourceBindingGateInvalidation? gate = null,
    ILogger<SwitchDocumentSourceFlagsHandler>? logger = null)
{
    /// <summary>Право (те саме, що <see cref="DocumentSourceBindings.Permission"/>).</summary>
    public const string Permission = "Calculation.ManageSourceBinding";

    /// <summary>Перемикає.</summary>
    /// <param name="id">Зв'язка.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SwitchDocumentSourceFlagsResponse> HandleAsync(
        int id, SwitchDocumentSourceFlagsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        if (request.ReadFromDocument is null && request.RecalculateDependents is null)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "readFromDocument або recalculateDependents: потрібен хоч один.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.sourceFlagsInvalid",
                    ["field"] = "readFromDocument",
                });
        }

        var changed = false;
        var queued = new List<int>();
        var enlist = jobs.EnlistsInCallerTransaction;
        var bindingCode = id.ToString(CultureInfo.InvariantCulture);
        var pending = new List<(int Year, string Target, RegistryImpactRecalculationRequest Request)>();
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            pending.Clear();
            queued.Clear();
            var binding = await store.LockAsync(id, innerCt).ConfigureAwait(false)
                          ?? throw DocumentSourceBindings.NotFound(id);
            bindingCode = binding.Code;

            var oldRead = binding.ReadFromDocument;
            var oldRecalculate = binding.RecalculateDependents;
            changed = binding.SetFlags(request.ReadFromDocument, request.RecalculateDependents, userId, clock.UtcNow);
            if (!changed)
            {
                return;
            }

            await store.SaveAsync(innerCt).ConfigureAwait(false);

            if (binding.ReadFromDocument != oldRead && binding.RecalculateDependents)
            {
                foreach (var year in await store.SourceYearsAsync(id, innerCt).ConfigureAwait(false))
                {
                    var pendingJob = (
                        RegistryImpactRecalculationTarget.ForSourceChange(binding.Id, year, month: null),
                        new RegistryImpactRecalculationRequest(
                            binding.RegistryDefId,
                            [],
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{binding.Code}: readFromDocument {oldRead} -> {binding.ReadFromDocument}"),
                            year,
                            binding.Id));
                    pending.Add((year, pendingJob.Item1, pendingJob.Item2));

                    // ✎ D-557: див. SwitchDocumentSourceStateHandler.
                    if (enlist)
                    {
                        await jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                                pendingJob.Item1, pendingJob.Item2, innerCt, createdByUserId: userId)
                            .ConfigureAwait(false);
                    }

                    queued.Add(year);
                }
            }

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    DocumentSourceBindings.ChangedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        kind = "flags",
                        bindingId = id,
                        code = binding.Code,
                        oldReadFromDocument = oldRead,
                        newReadFromDocument = binding.ReadFromDocument,
                        oldRecalculateDependents = oldRecalculate,
                        newRecalculateDependents = binding.RecalculateDependents,
                        queuedYears = queued,
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ✎ D-557: поза базою — після коміту (див. SwitchDocumentSourceStateHandler).
        IReadOnlyList<int> queuedYears = queued;
        IReadOnlyList<int> failedYears = [];
        if (!enlist)
        {
            var enqueued = await DocumentSourceBindings.EnqueueAfterCommitAsync(
                    jobs, pending, userId, bindingCode, logger ?? (ILogger)NullLogger.Instance, ct)
                .ConfigureAwait(false);
            queuedYears = enqueued.Queued;
            failedYears = enqueued.Failed;
        }

        if (changed)
        {
            gate?.Invalidate();
        }

        var view = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(v => v.Id == id)
                   ?? throw DocumentSourceBindings.NotFound(id);
        return new SwitchDocumentSourceFlagsResponse(DocumentSourceBindings.ToDto(view, overrides), changed, queuedYears, failedYears);
    }
}
