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

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Звіт переносу документа на нову версію шаблону (ФВ-7.5).</summary>
/// <param name="DocumentId">Документ, з якого відкрили перенос.</param>
/// <param name="ProjectId">Його проєкт — версія живе на проєкті, тож переносяться всі його документи.</param>
/// <param name="DocumentCount">Скільки документів проєкту переноситься.</param>
/// <param name="FromVersionId">Поточна версія.</param>
/// <param name="FromVersion">Її номер.</param>
/// <param name="ToVersionId">Цільова версія.</param>
/// <param name="ToVersion">Її номер.</param>
/// <param name="Mode">Режим.</param>
/// <param name="DryRun">Сухий прогін: нічого не змінено.</param>
/// <param name="Applied">Перенос виконано.</param>
/// <param name="CanApply">Режим дозволяє перенос.</param>
/// <param name="Refusals">Причини відмови: <c>structural</c>, <c>dataLoss</c>, <c>guardedWithData</c>, <c>sheetsLocked</c>, <c>projectArchived</c>.</param>
/// <param name="TransferredValues">Скільки введених значень переїде.</param>
/// <param name="LostValues">Скільки введених значень зникло б.</param>
/// <param name="GuardedValues">Скільки введених значень змінили б тлумачення.</param>
/// <param name="LockedSheets">Скільки пар «аркуш × період» подано або затверджено.</param>
/// <param name="Items">Відмінності версій, спершу ті, що зачіпають дані.</param>
/// <param name="ItemsTruncated">Перелік обрізано стелею <see cref="MigrateDocumentVersionHandler.MaxItems"/>.</param>
/// <param name="BlockedGrantCount">
/// Скільки грантів на ресурсах (аркуш/таблиця/колонка) вихідної версії не мають відповідника за кодом у новій і
/// через це блокують перенос; <c>null</c>, коли таких немає. ⛔ Лише кількість — які саме ресурси й ролі, не
/// розкривається, доки перелік не фільтрується через AccessProfile.
/// </param>
public sealed record DocumentVersionMigrationDto(
    long DocumentId,
    int ProjectId,
    int DocumentCount,
    int FromVersionId,
    string FromVersion,
    int ToVersionId,
    string ToVersion,
    VersionMigrationMode Mode,
    bool DryRun,
    bool Applied,
    bool CanApply,
    IReadOnlyList<string> Refusals,
    long TransferredValues,
    long LostValues,
    long GuardedValues,
    int LockedSheets,
    IReadOnlyList<VersionMigrationItem> Items,
    bool ItemsTruncated,
    int? BlockedGrantCount = null);

/// <summary>Куди можна перенести документ: поточна версія й опубліковані версії того самого шаблону.</summary>
/// <param name="ProjectId">Проєкт документа — переноситься весь.</param>
/// <param name="CurrentVersionId">Поточна версія проєкту.</param>
/// <param name="CurrentVersion">Її номер.</param>
/// <param name="Targets">Опубліковані версії того самого шаблону, крім поточної, від новішої.</param>
public sealed record DocumentVersionMigrationTargetsDto(
    int ProjectId,
    int CurrentVersionId,
    string CurrentVersion,
    IReadOnlyList<TemplateVersionSummary> Targets);

/// <summary>
/// Перенос документа на нову версію шаблону — явна операція з попереднім
/// переглядом наслідків (ФВ-7.5).
/// </summary>
/// <remarks>
/// ⛔ Версія шаблону живе на ПРОЄКТІ (<c>Project.TemplateVersionId</c>): її
/// беруть усі шляхи читання — таблиці, права, збір, звіти. Документ, чия
/// версія розійшлася з версією проєкту, відкривається без аркушів (V-11).
/// Тому перенос, відкритий із документа, переносить УСІ документи його
/// проєкту однією транзакцією, і звіт це прямо каже (<c>DocumentCount</c>).
///
/// ⚠ Відповідність — за кодами, як у <c>DiffTemplateVersionsHandler</c>:
/// ідентифікатори в кожної версії свої.
///
/// ⚠ Право — <c>Template.Edit</c> І грант <c>Manage</c> на проєкт (L1-08, HU-11 Q3=A), як на редагування шаблону: перенос міняє
/// те, за якою структурою живуть дані, а не самі дані одного користувача.
/// Невидимий документ — той самий <c>404</c>, що й неіснуючий.
/// </remarks>
public sealed class MigrateDocumentVersionHandler(
    IDocumentVersionMigrationStore store,
    IPeriodStore periods,
    ITemplateVersionStore templates,
    IRepository<TemplateVersion, int> versions,
    IMetadataCache metadata,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAccessProfileInvalidator profileCache,
    ISheetEditGate structureGate,
    IBackgroundJobScheduler jobs,
    Microsoft.Extensions.Logging.ILogger<MigrateDocumentVersionHandler> log)
{
    /// <summary>
    /// Скільки активна (<c>Queued</c>/<c>Running</c>) задача переносу може не оновлюватися, щоб ще вважатися
    /// живою: залишок від краху процесу не мусить назавжди блокувати новий перенос проєкту.
    /// </summary>
    public static readonly TimeSpan ActiveJobStaleAfter = TimeSpan.FromHours(2);

    /// <summary>Скільки останніх активних задач переглядає пошук уже поставленого переносу проєкту.</summary>
    private const int ActiveJobScanLimit = 200;

    /// <summary>Стеля переліку користувачів для скидання кешу профілів; більше — скидається весь кеш.</summary>
    public const int MaxInvalidatedUsers = 100_000;

    /// <summary>Право операції.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Подія журналу безпеки.</summary>
    public const string EventType = "DocumentVersionMigrated";

    /// <summary>Стеля переліку версій шаблону, серед яких обирається ціль.</summary>
    public const int MaxTargets = 200;

    /// <summary>Стеля переліку відмінностей у звіті.</summary>
    public const int MaxItems = 500;

    /// <summary>Скільки ключів методологій на чужі колонки називає відмова (<c>methodologyKeys</c>).</summary>
    public const int MaxMethodologyKeysListed = 20;

    /// <summary>
    /// ⛔ L1-08 (HU-11 Q3=A): перенос міняє структуру ВСІХ документів проєкту, тож потрібен грант
    /// <c>Manage</c> на проєкт (раніше досить було Read + глобального <c>Template.Edit</c>). Діє і на
    /// сухий прогін, і на перелік цілей. Документ уже видимий — відмова нічого не розкриває.
    /// </summary>
    private static void RequireProjectManage(AccessProfile profile, int projectId)
    {
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                $"Немає гранта Manage на проєкт {projectId}, документи якого переніс би на іншу версію шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Будує звіт і, якщо це не сухий прогін, переносить.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="dryRun"><c>true</c> — лише звіт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Документа (або цільової версії) немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-TMPL-0422</c> — ціль не є опублікованою іншою версією того самого шаблону;
    /// <c>ECR-SCHM-0422</c> — режим не має стратегії для таких змін (втрата даних,
    /// змінене тлумачення значень, структура в режимі <c>Presentation</c>).
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>ECR-DOC-0409</c> — є подані чи затверджені аркуші, або проєкт архівовано.
    /// </exception>
    public async Task<DocumentVersionMigrationDto> HandleAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, bool dryRun, CancellationToken ct)
    {
        var request = await ResolveAsync(documentId, targetVersionId, ct).ConfigureAwait(false);

        if (dryRun)
        {
            var (dto, _, _) = await PlanAsync(
                documentId, request.ProjectId, request.SourceVersionId, request.Target, mode, request.Archived,
                dryRun: true, ct).ConfigureAwait(false);
            return dto;
        }

        return await ApplyAsync(request, documentId, mode, onProgress: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ставить перенос у ЧЕРГУ (фон) і повертає ідентифікатор задачі (D-2 RC15B).
    /// </summary>
    /// <param name="documentId">Документ, з якого відкрили перенос.</param>
    /// <param name="targetVersionId">Цільова версія того самого шаблону.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Ідентифікатор задачі. Повторна постановка того самого автора, поки перенос проєкту ще йде, повертає
    /// ТОЙ САМИЙ ідентифікатор і другого переносу не ставить.
    /// </returns>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-JOB-0409</c> — перенос цього проєкту вже йде за вказівкою ІНШОЇ людини (її задачу автор не бачить).
    /// </exception>
    /// <remarks>
    /// ⛔ Ті самі швидкі перевірки, що й у синхронного шляху: право <c>Template.Edit</c>, видимість документа
    /// (невидимий — той самий <c>404</c>), грант <c>Manage</c> на проєкт, ціль, архівний проєкт. Це швидка
    /// відмова заради людини; ПЛАН (відмови за даними) задача перераховує сама під блоком проєкту — він
    /// на мільйонах значень іде десятки секунд, і тримати заради нього HTTP-запит означало б ту саму
    /// проблему, яку фон розв'язує. Для нього є сухий прогін, який діалог показує людині перед переносом.
    /// <para>
    /// ⚠ Ціль задачі — ПРОЄКТ (<c>migrate-project{id}</c>), а не документ: переноситься весь проєкт. Постановка
    /// <c>Coalesced</c> — НЕ витісняє нічого (перенос під блоком проєкту обривати посеред пачок немає сенсу, а
    /// чужі задачі іншого типу ціль не зачіпає). Пошук уже поставленого переносу додатково перевіряє
    /// <c>Running</c>: черга в базі ставила б другий перенос ПОЗАДУ виконуваного.
    /// </para>
    /// </remarks>
    public async Task<string> EnqueueAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode, CancellationToken ct)
    {
        var request = await ResolveAsync(documentId, targetVersionId, ct).ConfigureAwait(false);

        if (request.Archived)
        {
            throw ArchivedProject();
        }

        var documentIds = (await store.ListDocumentIdsAsync(request.ProjectId, ct).ConfigureAwait(false)).ToHashSet();
        var active = await FindActiveJobAsync(documentIds, ct).ConfigureAwait(false);
        if (active is not null)
        {
            if (active.CreatedByUserId == request.Profile.UserId)
            {
                return active.JobId;
            }

            throw new BusinessRuleException(
                ErrorCodes.JobStateConflict,
                $"Перенос проєкту {request.ProjectId} уже виконується за вказівкою іншого користувача.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.migrationInProgress",
                    ["projectId"] = request.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Автор — у завданні: задача виконується поза HTTP-запитом і без нього бачила б анонімного
        // користувача (F-01, той самий прийом, що й застосування великого імпорту).
        var actor = new JobActor(
            currentUser.UserId ?? request.Profile.UserId,
            currentUser.UserName,
            currentUser.Language,
            [.. currentUser.GroupSids],
            currentUser.CorrelationId);

        return await jobs
            .EnqueueCoalescedAsync<IMigrateDocumentVersionJob>(
                TargetOf(request.ProjectId),
                new MigrateDocumentVersionTask(documentId, targetVersionId, mode.ToString(), actor),
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);
    }

    /// <summary>Ціль задачі переносу: проєкт (переноситься весь).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <returns>Ключ цілі для злиття постановок.</returns>
    public static string TargetOf(int projectId)
        => string.Create(CultureInfo.InvariantCulture, $"migrate-project{projectId}");

    /// <summary>
    /// Виконує перенос у задачі черги — від імені автора (<c>JobActorScope</c>), з прогресом.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="mode">Режим.</param>
    /// <param name="onProgress">Приймач прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт: що перенесено.</returns>
    /// <remarks>
    /// ⛔ Усе перевіряється ще раз у мить виконання (право, видимість, грант, ціль, план): між постановкою
    /// і стартом право могли відкликати, версію — перенести. Відмова даних — той самий виняток, що й
    /// синхронного шляху (<c>ECR-SCHM-0422</c>, <c>ECR-DOC-0409</c>): задача стає <c>Failed</c> з його кодом,
    /// а документи лишаються на старій версії (атомарність — одна транзакція).
    /// </remarks>
    public async Task<DocumentVersionMigrationDto> RunQueuedAsync(
        long documentId, int targetVersionId, VersionMigrationMode mode,
        VersionMigrationProgressReporter onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onProgress);

        await onProgress(StartedPercent, "jobs.migrateStarted", null, ct).ConfigureAwait(false);
        var request = await ResolveAsync(documentId, targetVersionId, ct).ConfigureAwait(false);

        return await ApplyAsync(request, documentId, mode, onProgress, ct).ConfigureAwait(false);
    }

    private const int StartedPercent = 3;
    private const int PlanningPercent = 8;
    private const int StoreFloorPercent = 15;
    private const int StoreCeilingPercent = 97;

    /// <summary>Що з'ясовують швидкі перевірки до плану: профіль, проєкт, ціль.</summary>
    private sealed record Resolved(
        AccessProfile Profile, int ProjectId, int SourceVersionId, TemplateVersion Target, bool Archived);

    private async Task<Resolved> ResolveAsync(long documentId, int targetVersionId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        var target = await RequireTargetAsync(project.TemplateVersionId, targetVersionId, ct).ConfigureAwait(false);
        var archived = project.Status == ProjectStatus.Archived || project.IsArchiving;

        return new Resolved(profile, projectId, project.TemplateVersionId, target, archived);
    }

    /// <summary>Незавершена задача переносу будь-якого документа цього проєкту; <c>null</c> — немає.</summary>
    private async Task<JobSummary?> FindActiveJobAsync(HashSet<long> projectDocumentIds, CancellationToken ct)
    {
        var freshAfter = clock.UtcNow - ActiveJobStaleAfter;

        // Код у переліку — повне ім'я типу (черга в базі) або коротке (Quartz): порівняння за хвостом.
        foreach (var state in new[] { "Running", "Queued" })
        {
            var recent = await jobs
                .ListRecentAsync(new JobListFilter(State: state), ActiveJobScanLimit, ct)
                .ConfigureAwait(false);

            var found = recent.FirstOrDefault(j =>
                j.DocumentId is { } doc
                && projectDocumentIds.Contains(doc)
                && j.UpdatedAt >= freshAfter
                && (j.JobCode.EndsWith(nameof(IMigrateDocumentVersionJob), StringComparison.Ordinal)));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static ConcurrencyConflictException ArchivedProject()
        => new(
            ErrorCodes.DocumentSubmitted,
            "Проєкт архівовано: документи архівного проєкту не переносяться.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0409.migrateProjectArchived",
                ["lockedSheets"] = "0",
                ["refusals"] = ArchivedRefusals,
            });

    private static readonly string[] ArchivedRefusals = ["projectArchived"];

    /// <summary>Ключ каталогу повідомлення стадії; ключі — літералами (сторож каталогу шукає саме їх).</summary>
    private static string StageKey(string stage) => stage switch
    {
        VersionMigrationStages.Instances => "jobs.migrateInstances",
        VersionMigrationStages.NewRows => "jobs.migrateNewRows",
        VersionMigrationStages.Header => "jobs.migrateHeader",
        VersionMigrationStages.Index => "jobs.migrateIndex",
        VersionMigrationStages.Workflow => "jobs.migrateWorkflow",
        VersionMigrationStages.Validation => "jobs.migrateValidation",
        VersionMigrationStages.Finish => "jobs.migrateFinish",
        _ => "jobs.migrateCells",
    };

    private async Task<DocumentVersionMigrationDto> ApplyAsync(
        Resolved request, long documentId, VersionMigrationMode mode,
        VersionMigrationProgressReporter? onProgress, CancellationToken ct)
    {
        var profile = request.Profile;
        var projectId = request.ProjectId;
        var target = request.Target;
        var archived = request.Archived;

        DocumentVersionMigrationDto? result = null;
        var grantedUsers = new GrantedUsers([], Overflow: false);
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ План перераховується ПІД блоком рядка проєкту: між сухим
            // прогоном і підтвердженням у колонку, яку нова версія прибирає,
            // могли ввести значення. Перевірка на знімку з минулого запиту
            // пропустила б саме ту втрату, від якої вона захищає.
            var current = await store.LockProjectVersionAsync(projectId, innerCt).ConfigureAwait(false)
                          ?? throw DocumentVisibility.NotFound(documentId);
            if (current == target.Id)
            {
                throw SameVersion(target.Id);
            }

            // ⛔ L6-02: структура КОЖНОГО документа проєкту — винятково, за
            // зростанням `DocumentId`, ДО плану. Писарі (правка, імпорт, рядок,
            // подання, шапка, перерахунок) беруть її спільно першою дією, тож
            // далі жоден із них не комітиться між планом і `ApplyAsync`: план
            // бачить усе зафіксоване, а запис, що чекав, після коміту побачить
            // нову версію і відмовить (`DocumentStructure.EnsureUnchanged`).
            // Доти блокувався лише рядок `doc.Project`, якого писарі не беруть:
            // значення, введене в колонку, яку нова версія прибирає, між
            // плануванням і `DELETE` губилося при відповіді 200.
            foreach (var id in await store.ListDocumentIdsAsync(projectId, innerCt).ConfigureAwait(false))
            {
                await structureGate.EnterStructureAsync(id, exclusive: true, innerCt).ConfigureAwait(false);
            }

            if (onProgress is not null)
            {
                await onProgress(PlanningPercent, "jobs.migratePlanning", null, innerCt).ConfigureAwait(false);
            }

            var (dto, plan, foreignKeys) = await PlanAsync(
                documentId, projectId, current, target, mode, archived, dryRun: false, innerCt).ConfigureAwait(false);
            ThrowIfRefused(dto, foreignKeys);

            grantedUsers = await store.ListUsersWithGrantsAsync(plan, MaxInvalidatedUsers, innerCt).ConfigureAwait(false);

            VersionMigrationStepReporter? onStep = null;
            if (onProgress is not null)
            {
                var denominator = Math.Max(1L, plan.TransferredValues);
                var reached = StoreFloorPercent;
                onStep = async (stage, stepsDone, steps, changedRows, stepCt) =>
                {
                    // Крок вважається виконаним на частку змінених рядків відносно кількості значень, що
                    // переїздять (оцінка: різні кроки міняють різну кількість рядків), але не більше 90 %:
                    // доки пачки йдуть, крок не «закінчений». Відсоток не повертається назад.
                    var fraction = changedRows <= 0 ? 0d : Math.Min(0.9d, (double)changedRows / denominator);
                    var span = StoreCeilingPercent - StoreFloorPercent;
                    var percent = StoreFloorPercent + (int)(span * ((stepsDone + fraction) / Math.Max(1, steps)));
                    reached = Math.Max(reached, Math.Min(percent, StoreCeilingPercent));

                    await onProgress(
                        reached,
                        StageKey(stage),
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["rows"] = changedRows.ToString(CultureInfo.InvariantCulture),
                        },
                        stepCt).ConfigureAwait(false);
                };
            }

            await store.ApplyAsync(projectId, target.Id, plan, onStep, innerCt).ConfigureAwait(false);
            result = dto with { Applied = true };
        }, ct).ConfigureAwait(false);

        // ⛔ Профіль у кеші не бачить зміни грантів, яка не рухає відбиток груп
        // (прямі ролі): без явного скидання закрита колонка лишалася б доступною
        // до TTL (60 хв). Скидаються ВСІ записи користувача (будь-який відбиток
        // груп); збій або переповнення переліку — скидання всього кешу, не 500.
        GrantProfileInvalidation.Run(profileCache, log, grantedUsers);

        var applied = result!;

        // Журнал — після коміту, як у ChangeDocumentKeyHandler: IAuditWriter пише власним підключенням.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    documentId,
                    projectId,
                    fromVersionId = applied.FromVersionId,
                    toVersionId = applied.ToVersionId,
                    mode = applied.Mode.ToString(),
                    documents = applied.DocumentCount,
                    transferredValues = applied.TransferredValues,
                }),
                profile.UserId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return applied;
    }

    /// <summary>Перелік версій, на які можна перенести документ.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentVersionMigrationTargetsDto> ListTargetsAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        RequireProjectManage(profile, projectId);

        var current = await versions.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
        var all = await templates
            .ListVersionsForTemplatesAsync([current.TemplateId], MaxTargets, ct)
            .ConfigureAwait(false);

        var targets = all.GetValueOrDefault(current.TemplateId, [])
            .Where(v => v.Id != current.Id && v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.Id)
            .ToList();

        return new DocumentVersionMigrationTargetsDto(projectId, current.Id, current.Version, targets);
    }

    private async Task<(DocumentVersionMigrationDto Dto, VersionMigrationPlan Plan, IReadOnlyList<string> ForeignKeys)> PlanAsync(
        long documentId, int projectId, int sourceVersionId, TemplateVersion target,
        VersionMigrationMode mode, bool archived, bool dryRun, CancellationToken ct)
    {
        var source = await versions.GetAsync(sourceVersionId, ct).ConfigureAwait(false);
        var from = await metadata.GetAsync(sourceVersionId, ct).ConfigureAwait(false);
        var to = await metadata.GetAsync(target.Id, ct).ConfigureAwait(false);
        var scope = await store.ReadScopeAsync(projectId, ct).ConfigureAwait(false);

        var plan = VersionMigrationPlanner.Plan(from, to, scope.Cells, scope.HeaderValues);

        var refusals = plan.RefusalsFor(mode).ToList();
        if (scope.LockedSheets > 0)
        {
            refusals.Add("sheetsLocked");
        }

        if (archived)
        {
            refusals.Add("projectArchived");
        }

        // ⛔ Грант (заборона АБО дозвіл) на ресурсі, якого в новій версії за кодом немає,
        // нікуди не копіюється. Дозвіл теж звужує доступ (Read під Write проєкту — береться
        // найдрібніший рівень). Якщо ресурс просто перейменували, новий код лишився б без
        // обмеження (fail-open) — тому перенос відмовляє, доки грант на старому
        // ресурсі свідомо не зніме адміністратор безпеки.
        var mappedColumns = plan.Columns.Select(c => c.SourceColumnDefId).ToHashSet();
        var denied = await store.CountDenyGrantsAsync(
            [.. from.Sheets.Where(s => !s.IsDeleted && !plan.Sheets.ContainsKey(s.Id)).Select(s => s.Id)],
            [.. from.Sheets.SelectMany(s => s.Tables).Where(t => !t.IsDeleted && !plan.Tables.ContainsKey(t.Id)).Select(t => t.Id)],
            [.. from.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns)
                .Where(c => !c.IsDeleted && !mappedColumns.Contains(c.Id)).Select(c => c.Id)],
            ct).ConfigureAwait(false);
        if (denied > 0)
        {
            refusals.Add("grantsNotMapped");
        }

        // ⛔ D-13: активна прив'язка методології до колонки вихідної версії, якої нема в цільовій, після
        // переносу не рахувала б нічого — колонка лишалася б порожньою без помилки. Блокуємо, доки
        // конфігуратор не перенесе прив'язку (клон версії тепер копіює їх сам) або не вимкне її.
        var unmappedBindings = await store.CountUnmappedBindingsAsync(
            sourceVersionId, plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId), ct)
            .ConfigureAwait(false);
        if (unmappedBindings > 0)
        {
            refusals.Add("bindingsNotMapped");
        }

        // ⛔ D1: правила й обов'язкові входи методології адресують колонки числовим ColumnDefId КОНКРЕТНОЇ версії
        // шаблону. Якщо методологія, прив'язана до цільової версії, має опубліковану версію з ключами на колонки
        // іншої версії цього шаблону, то після переносу предикат не збігається ніколи (прогін мовчки порожній), а
        // вимога Block блокує збереження назавжди. Блокується лише перенос, не публікація шаблону.
        // ✎ C1: ключі, чия колонка має відповідник за шляхом у цілі, перекладаються при читанні
        // (MethodologyKeyLocalizer) і сюди не потрапляють; відмова лишилась для колонок, втрачених у цілі.
        var foreignKeys = await store.ListForeignMethodologyKeysAsync(target.Id, MaxMethodologyKeysListed, ct)
            .ConfigureAwait(false);
        if (foreignKeys.Count > 0)
        {
            refusals.Add("methodologyKeysNotMapped");
        }

        // Спершу те, що зачіпає введені дані, потім решта структури, потім вигляд.
        var ordered = plan.Items
            .OrderByDescending(i => i.Values > 0)
            .ThenByDescending(i => i.ChangeClass)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        var dto = new DocumentVersionMigrationDto(
            documentId, projectId, scope.DocumentIds.Count,
            source.Id, source.Version, target.Id, target.Version,
            mode, dryRun, Applied: false, CanApply: refusals.Count == 0, refusals,
            plan.TransferredValues, plan.LostValues, plan.GuardedValues, scope.LockedSheets,
            [.. ordered.Take(MaxItems)], ordered.Count > MaxItems,
            denied > 0 ? denied : null);

        return (dto, plan, foreignKeys);
    }

    private async Task<TemplateVersion> RequireTargetAsync(int sourceVersionId, int targetVersionId, CancellationToken ct)
    {
        var target = await versions.FindAsync(targetVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                $"Версії шаблону {targetVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = targetVersionId.ToString(CultureInfo.InvariantCulture),
                });

        if (target.Id == sourceVersionId)
        {
            throw SameVersion(target.Id);
        }

        var sourceTemplate = await templates.FindTemplateOfVersionAsync(sourceVersionId, ct).ConfigureAwait(false);
        if (sourceTemplate is null || sourceTemplate.Id != target.TemplateId)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Версія {target.Version} належить іншому шаблону: документ переноситься лише між версіями свого шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.migrateOtherTemplate",
                    ["versionId"] = targetVersionId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // Чернетка ще змінюється (перенесені дані опинилися б на структурі,
        // яку завтра переправлять), виведена з обігу — вже не для нових даних.
        if (target.Status != TemplateVersionStatus.Published)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Переносити можна лише на опубліковану версію; версія {target.Version} у стані {target.Status}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.migrateTargetNotPublished",
                    ["version"] = target.Version,
                    ["status"] = target.Status.ToString(),
                });
        }

        return target;
    }

    private static BusinessRuleException SameVersion(int versionId)
        => new(
            ErrorCodes.TemplateInvalid,
            $"Документ уже на версії {versionId}: переносити нема куди.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0422.migrateSameVersion",
                ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
            });

    private static void ThrowIfRefused(DocumentVersionMigrationDto dto, IReadOnlyList<string> foreignKeys)
    {
        if (dto.Refusals.Contains("projectArchived") || dto.Refusals.Contains("sheetsLocked"))
        {
            // Стан, а не зміст: подане чи затверджене стосувалося СТАРОЇ
            // структури, і переносити його мовчки означало б видати чуже
            // затвердження за нове. Спершу повернути аркуші в роботу.
            throw new ConcurrencyConflictException(
                ErrorCodes.DocumentSubmitted,
                dto.Refusals.Contains("projectArchived")
                    ? "Проєкт архівовано: документи архівного проєкту не переносяться."
                    : $"Подано або затверджено аркушів: {dto.LockedSheets}. Спершу поверніть їх у роботу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = dto.Refusals.Contains("projectArchived")
                        ? "err.ECR-DOC-0409.migrateProjectArchived"
                        : "err.ECR-DOC-0409.migrateSheetsLocked",
                    ["lockedSheets"] = dto.LockedSheets.ToString(CultureInfo.InvariantCulture),
                    ["refusals"] = dto.Refusals,
                });
        }

        if (dto.Refusals.Count == 0)
        {
            return;
        }

        // ⛔ ФВ-7.5 + ФВ-7.3: режим — це і є стратегія міграції. Зміна, для
        // якої в обраного режиму стратегії немає, — неповна операція (422), а
        // не конфлікт стану: `ECR-SCHM-0422` саме про це.
        throw new BusinessRuleException(
            ErrorCodes.GuardedChangeWithoutStrategy,
            $"Режим {dto.Mode} не переносить ці зміни: {string.Join(", ", dto.Refusals)}. " +
            $"Втратилося б значень: {dto.LostValues}, змінило б тлумачення: {dto.GuardedValues}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = dto.Refusals.Contains("structural")
                    ? "err.ECR-SCHM-0422.migrateStructural"
                    : dto.Refusals.Contains("grantsNotMapped") && dto.LostValues == 0 && dto.GuardedValues == 0
                        ? "err.ECR-SCHM-0422.migrateGrantsNotMapped"
                        : dto.Refusals.Contains("bindingsNotMapped") && dto.LostValues == 0 && dto.GuardedValues == 0
                            ? "err.ECR-SCHM-0422.migrateBindingsNotMapped"
                            : dto.Refusals.Contains("methodologyKeysNotMapped") && dto.LostValues == 0 && dto.GuardedValues == 0
                                ? "err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped"
                                : "err.ECR-SCHM-0422.migrateDataLoss",
                ["lostValues"] = dto.LostValues.ToString(CultureInfo.InvariantCulture),
                ["guardedValues"] = dto.GuardedValues.ToString(CultureInfo.InvariantCulture),
                ["mode"] = dto.Mode.ToString(),
                ["refusals"] = dto.Refusals,
                ["blockedGrantCount"] = dto.BlockedGrantCount?.ToString(CultureInfo.InvariantCulture),
                ["methodologyKeys"] = foreignKeys.Count > 0 ? foreignKeys : null,
            });
    }
}
