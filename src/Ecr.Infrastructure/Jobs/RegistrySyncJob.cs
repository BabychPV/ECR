// src/Ecr.Infrastructure/Jobs/RegistrySyncJob.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration.RegistrySync;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rules;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Синхронізація довідника із зовнішнім джерелом для сутності збору з
/// <c>RegistryDefId</c> (<c>ФВ-8.10</c>, <c>ФВ-8.11</c>; FEATURE-REGISTRY-SYNC S5, S7; <c>D-212</c>).
/// </summary>
/// <remarks>
/// Окремий інтерфейс, а не маркер черги: задачу ставить <see cref="CollectionJob"/>
/// тим самим розкладом і тією самою кнопкою <c>POST /sources/{id}/collect</c>, тож
/// окремого коду в черзі Quartz вона не потребує.
/// </remarks>
public interface IRegistrySyncJob
{
    /// <summary>Звіряє довідник сутності з джерелом і (для <c>External</c>/<c>Hybrid</c>) застосовує план.</summary>
    /// <param name="sourceEntityId">Сутність джерела, прив'язана до довідника.</param>
    /// <param name="ct">Скасування.</param>
    public Task ExecuteAsync(int sourceEntityId, CancellationToken ct);
}

/// <summary>
/// Читає джерело й довідник, будує план <see cref="RegistrySyncPlanner"/>, для
/// <c>External</c>/<c>Hybrid</c> виконує його ПОВНІСТЮ (<c>D-212</c> PR-6), а події пише в
/// журнал покриття.
/// </summary>
/// <remarks>
/// ⛔ У <c>dic.RegistryEntry</c>/<c>dic.RegistryValue</c> задача пише ЛИШЕ через
/// <see cref="RegistryEntryWriter"/> від <c>svc-integration</c>: автостворення —
/// <see cref="RegistryEntryWriter.WriteAsync"/> з <see cref="RegistryEntryWriteBatch.CreateOnly"/>,
/// оновлення й увімкнення/вимкнення — <see cref="RegistryEntryWriter.UpdateAsync"/> (адресація за Id,
/// <see cref="RegistryEntryUpdate.IsActive"/>). Ревізія даних, ключі, аудит <c>RegistryValueChanged</c>
/// і автор <c>ChangedByUserId</c> (RT-04) — ті самі, що в ручного запису.
/// <para>
/// ⛔ Одна СПРОБА — один DI-scope і ОДНА транзакція: створення записів + їхні
/// <c>dic.RegistryExternalKey</c>, оновлення, вимкнення/увімкнення, операції над ключами
/// (перепривʼязка з рядком <c>aud.StructureChange</c>, позначки зникнення, шляхи) і
/// <see cref="IRegistryRuleEngine.EvaluateAsync"/> на записаних записах. Збій будь-де в спробі —
/// відкат усього, подій дій немає; не «дані» (не <c>BusinessRule</c>/<c>Domain</c>/<c>Concurrency</c>) —
/// падіння прогону (<c>Failed</c> у черзі), журнал не пишеться зовсім.
/// </para>
/// <para>
/// ⚠ Правила довідника — ОЦІНКА, не відмова (рішення координатора): <c>ThrowIfErrors</c> не
/// викликається, джерело — правда, рядок записано, а кожне порушення — подія
/// <see cref="CollectionCoverage.RegistryRuleViolation"/>.
/// </para>
/// <para>
/// ⚠ «Все або нічого» writer'а (рішення S7): відмова рядків → ці записи йдуть подією (оновлення —
/// <see cref="CollectionCoverage.RegistryValueRejected"/>, створення —
/// <see cref="CollectionCoverage.RegistryElementUnlinked"/> з кодом відмови), решта пакета — ОДНИМ
/// повтором; відмова на весь пакет (<c>ECR-REG-4092</c>, зокрема <c>keyTakenConcurrently</c>) або
/// невдалий повтор → операції над ключами окремою спробою, далі кожен запис поштучно, у власній
/// спробі. Один поганий запис не блокує довідник, а кількість спроб обмежена.
/// </para>
/// <para>
/// ⚠ Події ДІЙ (<c>AutoCreated</c>, <c>ExternalKeyRelinked</c>, <c>Deactivated</c>,
/// <c>Reactivated</c>, <c>RuleViolation</c>) пишуться лише ПІСЛЯ коміту спроби, що їх виконала.
/// Журнал — окремим збереженням у контексті задачі після всіх спроб: збій між комітом спроби й
/// журналом втратить подію, а не дані (журнал відновиться наступною зміною).
/// </para>
/// <para>
/// ⚠ Елемент, чий GUID уже прив'язаний у цьому джерелі до запису ІНШОГО довідника
/// (<c>UQ_RegistryExternalKey</c>), у план не йде зовсім — ні створення, ні перепривʼязки: лише
/// подія <see cref="CollectionCoverage.RegistryElementUnlinked"/> з
/// <c>err.ECR-REG-0409.externalKeyTaken</c>, без Id чужого запису.
/// </para>
/// <para>
/// ⚠ Поле <c>Lookup</c> з довідником-ціллю (<c>RegistryFieldDef.RefRegistryDefId</c>) приходить КОДОМ
/// запису (<c>D-212</c> (5)): коди розв'язуються в Id одним запитом на довідник до планування.
/// </para>
/// <para>
/// ⚠ <c>Local</c> — лише звірка (<c>D-49</c>): у <c>dic.*</c> не пишеться нічого, зміна шляху
/// журналюється подією <see cref="CollectionCoverage.RegistryPendingUpdate"/>.
/// </para>
/// <para>
/// ⚠ Дедуп подій: у <c>Details</c> — ключ <c>key=&lt;предмет&gt;:&lt;значення&gt;</c>. Подія не
/// пишеться, якщо ОСТАННЯ подія того самого предмета цієї сутності має те саме значення (один
/// запит на прогін). Для зникнення (<c>SourceMissing</c>, <c>Deactivated</c>, <c>Reactivated</c>)
/// значення містить <c>since=</c> — момент <c>MissingInSourceSince</c>: той самий епізод
/// зникнення — одна подія, новий епізод (елемент повернувся й зник знову) — нова. Без позначки
/// (<c>Ignore</c>, <c>Local</c>) <c>since=</c> немає, і обмеження лишається: зникла й повернулась —
/// повторно не пишеться.
/// </para>
/// <para>
/// ⚠ «Останній автор — людина» (<c>D-118</c>) — лише <c>Hybrid</c>: автор є і це не
/// <c>svc-integration</c>, АБО автор невідомий (<c>null</c> — значення до RT-04).
/// </para>
/// <para>
/// ⚠ Приведення одиниць на межі (<c>D-173</c>) ще не існує в коді: значення
/// йде в планувальник в одиниці джерела.
/// </para>
/// <para>
/// ⚠ Вікно дії (<c>D-212</c> (8), PR-7) — лише для ТЕМПОРАЛЬНОГО довідника й лише за атрибутами
/// політики сутності (<c>ValidFromAttribute</c>/<c>ValidToAttribute</c>; дефолт — вимкнено). Пояс
/// AF — <see cref="RegistrySyncValidity.TimeZoneKey"/> (порожньо — UTC). Зміна вікна наявного запису
/// перераховує <c>IsOrphaned</c> (<see cref="IOrphanScanner.RescanForEntryAsync"/>) у ТІЙ САМІЙ
/// транзакції спроби — так само, як ручна зміна вікна (<c>SetEntryValidityHandler</c>).
/// Конфігурація (останній параметр) <c>null</c> — UTC: так будують задачу руками тести;
/// контейнер хоста підставляє свою завжди.
/// </para>
/// </remarks>
public sealed class RegistrySyncJob(
    EcrDbContext db,
    IEnumerable<IExternalDataSource> sources,
    IntegrationActor actor,
    IClock clock,
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null) : IRegistrySyncJob
{
    /// <summary>Префікс ключа дедупу в <c>Details</c> події.</summary>
    public const string DedupKeyPrefix = "; key=";

    /// <summary>Ключ каталогу: GUID елемента вже прив'язаний у цьому джерелі до іншого запису.</summary>
    public const string ExternalKeyTakenKey = "err.ECR-REG-0409.externalKeyTaken";

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Роздільник атрибута й елемента в шляху PI AF (<c>ProbeSourcePathHandler.SplitPath</c>).</summary>
    private const char AttributeSeparator = '|';

    /// <summary>Скільки ідентифікаторів іде в один запит <c>IN (…)</c>.</summary>
    private const int ChunkSize = 1_000;

    /// <inheritdoc />
    public async Task ExecuteAsync(int sourceEntityId, CancellationToken ct)
    {
        // ⛔ Автор задачі — svc-integration, як у матеріалізації: «хто змінив поле»
        // мусить мати відповідь. Запис іде в окремому scope — там автор свій, той самий.
        using var author = await actor.EnterAsync(ct).ConfigureAwait(false);

        var entity = await db.SourceEntities
                         .AsNoTracking()
                         .FirstOrDefaultAsync(e => e.Id == sourceEntityId && e.IsActive, ct)
                         .ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Сутність джерела {sourceEntityId} не існує або вимкнена: звіряти нічого.",
                         "err.ECR-INT-0503.sourceEntityUnavailable",
                         ("sourceEntityId", sourceEntityId.ToString(CultureInfo.InvariantCulture)));

        var registryDefId = entity.RegistryDefId
                            ?? throw new InvalidOperationException(
                                $"Сутність джерела {sourceEntityId} не прив'язана до довідника: синк довідника їй не належить.");

        var dataSource = await db.DataSources
                             .AsNoTracking()
                             .FirstOrDefaultAsync(s => s.Id == entity.DataSourceId && s.IsActive, ct)
                             .ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Джерело {entity.DataSourceId} не існує або вимкнене.",
                             "err.ECR-INT-0503.sourceMissing",
                             ("dataSourceId", entity.DataSourceId.ToString(CultureInfo.InvariantCulture)));

        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Транспорт {dataSource.Transport} не зареєстровано.",
                          "err.ECR-INT-0503.transportNotRegistered",
                          ("transport", dataSource.Transport.ToString()));

        var registry = await db.RegistryDefs
                           .AsNoTracking()
                           .Include(d => d.Fields)
                           .FirstOrDefaultAsync(d => d.Id == registryDefId, ct)
                           .ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Довідника {registryDefId} не існує.");

        var mappings = await MappingsAsync(entity.Id, registry.Fields.ToDictionary(f => f.Id), ct).ConfigureAwait(false);

        // D-212 PR-7: вікно дії — лише темпоральному довіднику (нетемпоральному політику з датами
        // не дає задати PUT …/registry/policy; довідник, що перестав бути темпоральним, — не пишемо).
        var validity = registry.IsTemporal && (entity.ValidFromAttribute is not null || entity.ValidToAttribute is not null)
            ? new RegistrySyncValiditySource(
                entity.ValidFromAttribute,
                entity.ValidToAttribute,
                entity.ValidToInclusive,
                RegistrySyncValidity.ResolveTimeZone(configuration?[RegistrySyncValidity.TimeZoneKey]))
            : null;

        var links = await LinksAsync(dataSource.Id, registryDefId, ct).ConfigureAwait(false);

        string[] extra = validity is null ? [] : [.. new[] { validity.FromAttribute, validity.ToAttribute }.OfType<string>()];
        var snapshot = await SnapshotAsync(adapter, dataSource.Id, entity, mappings, extra, links.Count > 0, ct).ConfigureAwait(false);

        var entries = await EntriesAsync(links, mappings, ct).ConfigureAwait(false);

        // ⚠ Цілі секунди: MissingInSourceSince — datetime2(3), і `since=` у ключі дедупу мусить
        // збігатися з тим, що повернеться з бази наступним прогоном.
        var now = WholeSeconds(clock.UtcNow);

        // GUID, прив'язаний у цьому джерелі до ІНШОГО довідника, — не наш елемент: створити чи
        // перепривʼязати його означало б порушити UQ_RegistryExternalKey або вкрасти чужий зв'язок.
        var foreign = await ForeignAsync(dataSource.Id, snapshot.Elements, links, ct).ConfigureAwait(false);

        var input = new RegistrySyncInput(
            registryDefId,
            registry.SourceKind,
            snapshot.IsComplete,
            [.. snapshot.Elements.Where(e => !foreign.Contains(e.ExternalId))],
            links,
            entries,
            mappings,
            CodeMode: registry.CodeMode,
            OnMissingInSource: entity.OnMissingInSource,
            Validity: validity);

        // D-212 (5): коди записів інших довідників → Id, одним запитом на довідник.
        var lookupCodes = await ResolveCodesAsync(RegistrySyncPlanner.LookupCodes(input), ct).ConfigureAwait(false);
        var plan = RegistrySyncPlanner.Plan(input with { LookupCodes = lookupCodes });

        var context = new ApplyContext(registryDefId, registry.Code, dataSource.Id, now, links, plan.MissingMarks);
        var events = new List<SyncEvent>();

        events.AddRange(snapshot.Rejections.Select(r => new SyncEvent(
            CollectionCoverage.RegistryValueRejected, r, KeyOf(CollectionCoverage.RegistryValueRejected, r, value: string.Empty))));
        events.AddRange(snapshot.Elements
            .Where(e => foreign.Contains(e.ExternalId))
            .OrderBy(e => e.ExternalId, StringComparer.Ordinal)
            .Select(e => Foreign(e.ExternalId)));
        events.AddRange(plan.Events.Select(e => e.Kind == RegistrySyncEventKind.SourceMissing
            ? Event(e, context.SinceOf(e.ExternalId))
            : Event(e)));

        if (registry.SourceKind is RegistrySourceKind.External or RegistrySourceKind.Hybrid)
        {
            events.AddRange(await ApplyAsync(context, plan, ct).ConfigureAwait(false));
        }
        else
        {
            // Local — лише звірка: оновлень планувальник тут не дає, шлях не пишеться.
            events.AddRange(plan.Updates.Select(Pending));
            events.AddRange(plan.PathChanges.Select(Pending));
        }

        var fresh = await DeduplicateAsync(entity.Id, events, ct).ConfigureAwait(false);

        if (fresh.Count == 0)
        {
            return;
        }

        db.CollectionCoverages.AddRange(fresh.Select(e => CollectionCoverage.SkippedRegistry(
            entity.Id, e.Status, WithKey(e.Details, e.Key), now)));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Виконує план: спроба на весь пакет, при відмові рядків — один повтор без них, далі
    /// ключі окремо й записи поштучно (див. <see cref="RegistrySyncJob"/>).
    /// </summary>
    /// <returns>Події дій (після коміту) і відмов.</returns>
    private async Task<List<SyncEvent>> ApplyAsync(ApplyContext context, RegistrySyncPlan plan, CancellationToken ct)
    {
        var events = new List<SyncEvent>();
        var refused = new List<CreateRefusal>();

        // Manual: код = ім'я елемента. Два елементи з тим самим ім'ям дали б writer'у дубль коду в
        // пакеті (помилка виклику) — другий і далі відмовляються як «код зайнято» першим.
        var creates = new List<RegistrySyncCreate>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var create in plan.Creates)
        {
            if (create.Code is { } code && !codes.Add(code))
            {
                refused.Add(new CreateRefusal(create, ErrorCodes.RegistryEntryInUse, RegistryEntryWriter.EntryCodeTakenKey));
                continue;
            }

            creates.Add(create);
        }

        var updates = Updates(plan);
        var keys = new KeyOps(plan.Relinks, plan.MissingMarks, plan.MissingClears, plan.PathChanges);
        var outcomes = new List<Outcome>();
        var externalIds = context.ExternalIds(plan);

        if (creates.Count > 0 || updates.Count > 0 || !keys.IsEmpty)
        {
            var single = false;
            var done = false;

            for (var attempt = 0; attempt < 2 && !done && !single; attempt++)
            {
                try
                {
                    var result = await AttemptAsync(context, creates, updates, keys, ct).ConfigureAwait(false);
                    if (result.Outcome is { } outcome)
                    {
                        outcomes.Add(outcome);
                        done = true;
                        break;
                    }

                    // Відмова рядків: вони — у журнал, решта пакета — одним повтором.
                    refused.AddRange(result.CreateErrors.Select(e => Refusal(creates[e.Row - 1], e)));
                    events.AddRange(result.UpdateErrors.Select(e => Rejected(updates[e.Row - 1], e, externalIds)));
                    creates = Without(creates, result.CreateErrors);
                    updates = Without(updates, result.UpdateErrors);
                    single = attempt == 1;
                }
                catch (Exception ex) when (IsBatchFailure(ex))
                {
                    single = true;
                }
            }

            if (single)
            {
                // Ключі — окремо: від відмови запису вони не залежать. Відмова тут — збій прогону.
                if (!keys.IsEmpty)
                {
                    var result = await AttemptAsync(context, [], [], keys, ct).ConfigureAwait(false);
                    outcomes.Add(result.Outcome!);
                }

                foreach (var create in creates)
                {
                    try
                    {
                        var result = await AttemptAsync(context, [create], [], KeyOps.None, ct).ConfigureAwait(false);
                        if (result.Outcome is { } outcome)
                        {
                            outcomes.Add(outcome);
                        }
                        else
                        {
                            refused.AddRange(result.CreateErrors.Select(e => Refusal(create, e)));
                        }
                    }
                    catch (Exception ex) when (IsBatchFailure(ex))
                    {
                        refused.Add(Refusal(create, ex));
                    }
                }

                foreach (var update in updates)
                {
                    try
                    {
                        var result = await AttemptAsync(context, [], [update], KeyOps.None, ct).ConfigureAwait(false);
                        if (result.Outcome is { } outcome)
                        {
                            outcomes.Add(outcome);
                        }
                        else
                        {
                            events.AddRange(result.UpdateErrors.Select(e => Rejected(update, e, externalIds)));
                        }
                    }
                    catch (Exception ex) when (IsBatchFailure(ex))
                    {
                        events.Add(Rejected(update, ex, externalIds));
                    }
                }
            }
        }

        foreach (var outcome in outcomes)
        {
            foreach (var (create, entryId) in outcome.Created)
            {
                externalIds[entryId] = create.ExternalId;
                events.Add(Event(new RegistrySyncEvent(RegistrySyncEventKind.AutoCreated, create.ExternalId, entryId)));
            }

            events.AddRange(outcome.Relinks.Select(Relinked));

            foreach (var update in outcome.Updates.Where(u => u.IsActive is not null))
            {
                var element = externalIds.GetValueOrDefault(update.RegistryEntryId);
                var kind = update.IsActive!.Value ? RegistrySyncEventKind.Reactivated : RegistrySyncEventKind.Deactivated;
                events.Add(Event(new RegistrySyncEvent(kind, element, update.RegistryEntryId), context.SinceOf(element)));
            }

            events.AddRange(outcome.Violations.Select(v => Violation(v, externalIds)));
        }

        events.AddRange(await RefusedAsync(context.RegistryDefId, refused, ct).ConfigureAwait(false));
        return events;
    }

    /// <summary>
    /// Одна спроба: власний DI-scope від <c>svc-integration</c> і ОДНА транзакція на створення,
    /// оновлення, ключі й оцінку правил.
    /// </summary>
    /// <returns>
    /// Виконане (<see cref="AttemptResult.Outcome"/>) або відмови рядків writer'а — тоді транзакцію
    /// відкочено й scope закрито без збереження.
    /// </returns>
    /// <remarks>
    /// ⛔ Відмова на весь пакет (<c>BusinessRule</c>, <c>Domain</c>, <c>ConcurrencyConflict</c>) і будь-який
    /// інший виняток летять далі — транзакція вже відкочена. ⚠ Контекст спроби очищується на
    /// початку тіла транзакції: стратегія повторів <c>ExecuteInTransactionAsync</c> на
    /// транзієнтному збої виконує тіло ще раз, і відстежене з першого разу вставилося б удруге.
    /// </remarks>
    private async Task<AttemptResult> AttemptAsync(
        ApplyContext context,
        List<RegistrySyncCreate> creates,
        List<RegistryEntryUpdate> updates,
        KeyOps keys,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(ct).ConfigureAwait(false);

        var scoped = services.GetRequiredService<EcrDbContext>();
        var uow = services.GetRequiredService<IUnitOfWork>();
        var writer = services.GetRequiredService<RegistryEntryWriter>();

        Outcome? outcome = null;
        IReadOnlyList<RegistryEntryImportError> createErrors = [];
        IReadOnlyList<RegistryEntryImportError> updateErrors = [];

        try
        {
            await uow.ExecuteInTransactionAsync(
                async tx =>
                {
                    scoped.ChangeTracker.Clear();
                    var created = new List<(RegistrySyncCreate Create, long EntryId)>();
                    var changed = new HashSet<long>();

                    if (creates.Count > 0)
                    {
                        var result = await writer.WriteAsync(
                            new RegistryEntryWriteBatch(context.RegistryDefId, [.. creates.Select(Write)]) { CreateOnly = true },
                            tx).ConfigureAwait(false);

                        if (result.Errors.Count > 0)
                        {
                            createErrors = result.Errors;
                            throw new RollbackAttempt();
                        }

                        foreach (var row in result.Rows)
                        {
                            var create = creates[row.Row - 1];
                            var key = new RegistryExternalKey(row.Entry.Id, context.DataSourceId, create.ExternalId);
                            key.MarkSynced(create.ExternalPath, context.Now);
                            scoped.RegistryExternalKeys.Add(key);
                            created.Add((create, row.Entry.Id));
                            changed.Add(row.Entry.Id);
                        }
                    }

                    if (updates.Count > 0)
                    {
                        var result = await writer.UpdateAsync(
                            new RegistryEntryUpdateBatch(context.RegistryDefId, updates), tx).ConfigureAwait(false);

                        if (result.Errors.Count > 0)
                        {
                            updateErrors = result.Errors;
                            throw new RollbackAttempt();
                        }

                        changed.UnionWith(result.Rows.Where(r => r.IsChanged).Select(r => r.Entry.Id));
                    }

                    var relinked = await ApplyKeysAsync(scoped, context, keys, tx).ConfigureAwait(false);
                    await uow.SaveChangesAsync(tx).ConfigureAwait(false);

                    // ⛔ D-212 PR-7 (ФВ-8.13a): нове вікно вже збережене — перерахунок IsOrphaned рядків, що
                    // посилаються на запис, у тій самій транзакції (як SetEntryValidityHandler). Новий запис
                    // посилань не має; оновлення без Validity вікна не змінює.
                    var rescan = updates.Where(u => u.Validity is not null).Select(u => u.RegistryEntryId).ToList();
                    if (rescan.Count > 0)
                    {
                        var scanner = services.GetRequiredService<IOrphanScanner>();
                        foreach (var entryId in rescan)
                        {
                            await scanner.RescanForEntryAsync(entryId, tx).ConfigureAwait(false);
                        }
                    }

                    if (relinked.Count > 0)
                    {
                        var audit = services.GetRequiredService<IAuditWriter>();
                        var user = services.GetRequiredService<ICurrentUser>();
                        foreach (var (relink, keyId) in relinked)
                        {
                            await audit.WriteStructureChangeAsync(RelinkChange(context, relink, keyId, user), tx).ConfigureAwait(false);
                        }
                    }

                    // ⛔ Правила — ПІСЛЯ збереження в тій самій транзакції (знімок бачить стан після запису),
                    // і лише ОЦІНКА: ThrowIfErrors тут немає — порушення стають подіями.
                    IReadOnlyList<RegistryRuleViolationDto> violations = [];
                    if (changed.Count > 0)
                    {
                        var definition = await services.GetRequiredService<IRegistryStore>()
                                             .FindDefinitionByIdAsync(context.RegistryDefId, tx)
                                             .ConfigureAwait(false)
                                         ?? throw new InvalidOperationException($"Довідника {context.RegistryDefId} не існує.");

                        violations = (await services.GetRequiredService<IRegistryRuleEngine>()
                            .EvaluateAsync(definition, [.. changed], [], businessDate: null, tx)
                            .ConfigureAwait(false)).Violations;
                    }

                    outcome = new Outcome(created, updates, [.. relinked.Select(r => r.Relink)], violations);
                },
                ct).ConfigureAwait(false);
        }
        catch (RollbackAttempt)
        {
            // Задуманий відкат: відмова рядків writer'а.
        }

        return new AttemptResult(outcome, createErrors, updateErrors);
    }

    /// <summary>
    /// Операції над ключами на відстежених рядках контексту спроби: шляхи, зняття й постановка
    /// позначки зникнення, перепривʼязка.
    /// </summary>
    /// <returns>Виконані перепривʼязки з Id ключа — для <c>aud.StructureChange</c> після збереження.</returns>
    private static async Task<List<(RegistrySyncRelink Relink, long KeyId)>> ApplyKeysAsync(
        EcrDbContext scoped, ApplyContext context, KeyOps keys, CancellationToken ct)
    {
        var relinked = new List<(RegistrySyncRelink Relink, long KeyId)>();
        if (keys.IsEmpty)
        {
            return relinked;
        }

        var ids = keys.Relinks.Select(r => r.OldExternalId)
            .Concat(keys.Marks.Select(m => m.ExternalId))
            .Concat(keys.Clears.Select(c => c.ExternalId))
            .Concat(keys.Paths.Select(p => p.ExternalId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byExternalId = new Dictionary<string, RegistryExternalKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            var list = chunk.ToList();
            var rows = await scoped.RegistryExternalKeys
                .Where(k => k.DataSourceId == context.DataSourceId && list.Contains(k.ExternalId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                byExternalId.TryAdd(row.ExternalId, row);
            }
        }

        // Ключ мусить і досі вести на той самий запис: між читанням і записом його могли
        // перев'язати вручну — тоді синк його не чіпає.
        RegistryExternalKey? Find(string externalId, long entryId)
            => byExternalId.TryGetValue(externalId, out var key) && key.RegistryEntryId == entryId ? key : null;

        foreach (var path in keys.Paths)
        {
            Find(path.ExternalId, path.RegistryEntryId)?.MarkSynced(path.NewPath, context.Now);
        }

        foreach (var clear in keys.Clears)
        {
            Find(clear.ExternalId, clear.RegistryEntryId)?.ClearMissing();
        }

        foreach (var mark in keys.Marks)
        {
            Find(mark.ExternalId, mark.RegistryEntryId)?.MarkMissing(context.Now);
        }

        foreach (var relink in keys.Relinks)
        {
            if (Find(relink.OldExternalId, relink.RegistryEntryId) is { } key)
            {
                key.Relink(relink.NewExternalId);
                key.MarkSynced(relink.Path, context.Now);
                relinked.Add((relink, key.Id));
            }
        }

        return relinked;
    }

    /// <summary>Рядок <c>aud.StructureChange</c> перепривʼязки — у форматі прив'язки/відв'язки вручну.</summary>
    private static StructureChangeRecord RelinkChange(
        ApplyContext context, RegistrySyncRelink relink, long keyId, ICurrentUser user)
        => new(
            ChangedAt: context.Now,
            TemplateVersionId: 0,
            EntityType: "dic.RegistryExternalKey",
            EntityId: checked((int)keyId),
            ChangeClass: ChangeClass.Guarded,
            Operation: "Relink",
            OldJson: RelinkJson(context, relink.RegistryEntryId, relink.OldExternalId),
            NewJson: RelinkJson(context, relink.RegistryEntryId, relink.NewExternalId),
            ChangeReason: $"Синк довідника «{context.RegistryCode}»: елемент AF перестворено з новим GUID "
                          + $"(шлях {relink.Path}) — зв'язок запису {relink.RegistryEntryId.ToString(CultureInfo.InvariantCulture)} "
                          + $"перев'язано з «{relink.OldExternalId}» на «{relink.NewExternalId}» (D-212 (7)).",
            ChangedByUserId: user.UserId
                             ?? throw new InvalidOperationException("Спроба синку без автора svc-integration."),
            CorrelationId: user.CorrelationId);

    private static string RelinkJson(ApplyContext context, long entryId, string externalId)
        => JsonSerializer.Serialize(new
        {
            registry = context.RegistryCode,
            entryId,
            dataSourceId = context.DataSourceId,
            externalId,
        });

    /// <summary>Рядок пакета створення для writer'а: код (порожній — Auto), назва, значення.</summary>
    private static RegistryEntryWrite Write(RegistrySyncCreate create)
        => new(
            create.Code ?? string.Empty,
            create.Values.ToDictionary(v => v.FieldCode, v => (object?)v.Value, StringComparer.Ordinal))
        {
            DisplayName = create.DisplayName,
            Validity = create.Validity,
        };

    /// <summary>
    /// Оновлення на запис: поля планувальника + увімкнення/вимкнення (<see cref="RegistryEntryUpdate.IsActive"/>)
    /// — одним рядком writer'а, у порядку першої появи запису в плані.
    /// </summary>
    private static List<RegistryEntryUpdate> Updates(RegistrySyncPlan plan)
    {
        var order = new List<long>();
        var values = new Dictionary<long, Dictionary<string, object?>>();
        var active = new Dictionary<long, bool>();
        var windows = plan.ValidityChanges.ToDictionary(w => w.RegistryEntryId, w => w.New);

        foreach (var update in plan.Updates)
        {
            if (!values.TryGetValue(update.RegistryEntryId, out var fields))
            {
                fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                values[update.RegistryEntryId] = fields;
                order.Add(update.RegistryEntryId);
            }

            fields[update.FieldCode] = update.NewValue;
        }

        foreach (var (activation, isActive) in plan.Deactivations.Select(d => (d, false))
                     .Concat(plan.Reactivations.Select(r => (r, true))))
        {
            if (!values.ContainsKey(activation.RegistryEntryId) && !active.ContainsKey(activation.RegistryEntryId))
            {
                order.Add(activation.RegistryEntryId);
            }

            active[activation.RegistryEntryId] = isActive;
        }

        foreach (var change in plan.ValidityChanges)
        {
            if (!values.ContainsKey(change.RegistryEntryId) && !active.ContainsKey(change.RegistryEntryId))
            {
                order.Add(change.RegistryEntryId);
            }
        }

        return [.. order.Select(id => new RegistryEntryUpdate(
            id, values.GetValueOrDefault(id) ?? new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            IsActive = active.TryGetValue(id, out var isActive) ? isActive : null,
            Validity = windows.TryGetValue(id, out var window) ? window : null,
        })];
    }

    private static List<T> Without<T>(IReadOnlyList<T> items, IReadOnlyList<RegistryEntryImportError> errors)
    {
        var failed = errors.Select(e => e.Row).ToHashSet();
        return [.. items.Where((_, i) => !failed.Contains(i + 1))];
    }

    /// <summary>
    /// Відмова на весь пакет — дані, а не збій прогону. ⛔ <c>ConcurrencyConflictException</c> —
    /// індекс <c>UX_RegistryEntryKey_Live</c> (<c>keyTakenConcurrently</c>), коли паралельний запис
    /// випередив блокування; поштучний повтор розводить гонку. Інші <c>EcrException</c> (немає автора,
    /// немає довідника) — збій прогону: їх не ковтаємо.
    /// </summary>
    private static bool IsBatchFailure(Exception ex)
        => ex is BusinessRuleException or DomainException or ConcurrencyConflictException;

    /// <summary>Коди записів інших довідників → Id: живі записи, регістронезалежно (як колація бази).</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>>> ResolveCodesAsync(
        IReadOnlyList<RegistrySyncLookupCode> codes, CancellationToken ct)
    {
        var resolved = new Dictionary<int, IReadOnlyDictionary<string, long>>();

        foreach (var group in codes.GroupBy(c => c.RegistryDefId))
        {
            var registryDefId = group.Key;
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunk in group.Select(c => c.Code).Chunk(ChunkSize))
            {
                var list = chunk.ToList();
                var rows = await db.RegistryEntries
                    .AsNoTracking()
                    .Where(e => e.RegistryDefId == registryDefId && !e.IsDeleted && list.Contains(e.Code))
                    .Select(e => new { e.Code, e.Id })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var row in rows)
                {
                    map.TryAdd(row.Code, row.Id);
                }
            }

            resolved[registryDefId] = map;
        }

        return resolved;
    }

    /// <summary>GUID елементів знімка, прив'язані в цьому джерелі до записів ІНШИХ довідників.</summary>
    private async Task<HashSet<string>> ForeignAsync(
        int dataSourceId,
        IReadOnlyList<RegistrySyncSourceElement> elements,
        IReadOnlyList<RegistrySyncLink> links,
        CancellationToken ct)
    {
        var own = links.Select(l => l.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = elements.Select(e => e.ExternalId).Where(id => !own.Contains(id)).ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in candidates.Chunk(ChunkSize))
        {
            var list = chunk.ToList();
            taken.UnionWith(await db.RegistryExternalKeys
                .AsNoTracking()
                .Where(k => k.DataSourceId == dataSourceId && list.Contains(k.ExternalId))
                .Select(k => k.ExternalId)
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return taken;
    }

    /// <summary>
    /// Події відмов створення. <c>entryCodeTaken</c> — з Id запису, що тримає код (шаблон тексту
    /// має <c>{id}</c>, а помилка рядка writer'а параметрів не несе): читається тут одним запитом.
    /// </summary>
    /// <remarks>
    /// ⚠ Id, а не інший ключ: код у тексті вже є, а Id — те, за чим адміністратор знайде запис, що
    /// заважає (зокрема видалений логічно: код за ним лишається, і в переліку він не видний).
    /// </remarks>
    private async Task<List<SyncEvent>> RefusedAsync(int registryDefId, List<CreateRefusal> refused, CancellationToken ct)
    {
        var codes = refused
            .Where(r => r.MessageKey == RegistryEntryWriter.EntryCodeTakenKey && r.Create.Code is not null)
            .Select(r => r.Create.Code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var holders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in codes.Chunk(ChunkSize))
        {
            var list = chunk.ToList();
            var rows = await db.RegistryEntries
                .AsNoTracking()
                .Where(e => e.RegistryDefId == registryDefId && list.Contains(e.Code))
                .OrderBy(e => e.Id)
                .Select(e => new { e.Code, e.Id })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                holders.TryAdd(row.Code, row.Id);
            }
        }

        return [.. refused.Select(r => Refused(
            r, r.MessageKey == RegistryEntryWriter.EntryCodeTakenKey && r.Create.Code is { } code
                ? holders.TryGetValue(code, out var id) ? id : null
                : null))];
    }

    /// <summary>Мапінги сутності на поля ЦЬОГО довідника — і активні, і вимкнені (планувальнику потрібні обидва).</summary>
    private async Task<List<RegistrySyncFieldMapping>> MappingsAsync(
        int sourceEntityId,
        Dictionary<int, Domain.Entities.Configuration.RegistryFieldDef> fields,
        CancellationToken ct)
    {
        var maps = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == sourceEntityId
                        && m.TargetKind == FieldTargetKind.RegistryField
                        && m.TargetRegistryFieldDefId != null)
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Поле чужого довідника пропускається: мапінг на нього відхиляє вже
        // створення мапінгу (S3), а писати в чужий довідник синк не має права.
        // ⚠ RefRegistryDefId поля Lookup — атрибут несе КОД запису (D-212 (5)).
        return [.. maps
            .Where(m => fields.ContainsKey(m.TargetRegistryFieldDefId!.Value))
            .Select(m =>
            {
                var field = fields[m.TargetRegistryFieldDefId!.Value];
                return new RegistrySyncFieldMapping(
                    field.Id, field.Code, field.DataType, field.UnitId, m.SourceField, m.IsActive,
                    field.DataType == CellDataType.Lookup ? field.RefRegistryDefId : null);
            })];
    }

    /// <summary>Знімок джерела: елементи під коренем сутності й поточні значення змаплених атрибутів.</summary>
    /// <remarks>
    /// ⛔ Елементи — з <see cref="IExternalDataSource.DiscoverElementsAsync"/>, а не з каталогу
    /// конфігуратора (<c>D-212</c> §0): каталог PI SQL Client дає рядки-атрибути, і знімок із
    /// нього був порожнім і «повним» — хибний <c>RegistrySourceMissing</c> на всі зв'язки.
    /// <para>
    /// ⛔ Повний знімок (<c>D-187</c>) — лише коли перелік повний, GUID не дублюються,
    /// <c>ReadCurrentAsync</c> не повернув жодної відмови шляху і — ЗАПОБІЖНИК — перелік не
    /// порожній при наявних зв'язках (для будь-якого транспорту): «зникли всі» від порожньої
    /// відповіді не відрізнити від хибного кореня чи тексту запиту. Відмова переліку чи всього
    /// джерела — виняток адаптера; задача стає <c>Failed</c> з його кодом, а не пише знімок,
    /// якого немає.
    /// </para>
    /// </remarks>
    private static async Task<Snapshot> SnapshotAsync(
        IExternalDataSource adapter,
        int dataSourceId,
        SourceEntity entity,
        IReadOnlyList<RegistrySyncFieldMapping> mappings,
        IReadOnlyList<string> validityAttributes,
        bool hasLinks,
        CancellationToken ct)
    {
        // ⚠ Той самий запасний шлях, що в CollectionRunner.Paths: сутність без
        // шляху адресується своїм кодом.
        var root = entity.EntityPath ?? entity.Code;

        var listed = await adapter.DiscoverElementsAsync(dataSourceId, root, ct).ConfigureAwait(false);
        var complete = listed.IsComplete && (listed.Elements.Count > 0 || !hasLinks);

        var elements = new Dictionary<string, SourceElement>(StringComparer.OrdinalIgnoreCase);

        var rejections = new List<string>();

        foreach (var listedElement in listed.Elements)
        {
            // ⛔ Аудит 2026-10-03 (L4-03): ідентифікатор чи шлях, ширший за колонку, валив прогін.
            var (element, rejection) = RegistrySyncElementLimits.Fit(listedElement);
            if (rejection is not null)
            {
                rejections.Add(Truncate(rejection));
            }

            if (element is null)
            {
                complete = false;
                continue;
            }

            // Без GUID елемент не зіставити, а дубль GUID — порушення контракту
            // переліку: і те, і те робить знімок неповним, а не падінням задачі.
            if (string.IsNullOrWhiteSpace(element.ExternalId) || !elements.TryAdd(element.ExternalId, element))
            {
                complete = false;
            }
        }

        // ⚠ Атрибути дат (D-212 PR-7) читаються тим самим запитом, що й змаплені: неприйнятий шлях
        // робить знімок неповним так само.
        var attributes = mappings
            .Select(m => m.SourceAttribute)
            .Concat(validityAttributes)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Шлях атрибута → (елемент, атрибут).
        var addresses = new Dictionary<string, (string ExternalId, string Attribute)>(StringComparer.OrdinalIgnoreCase);

        // ⛔ L4-10: PI SQL Client адресує елемент ІМЕНЕМ (`WHERE e.Name = ?`), а однакові імена в різних
        // гілках AF — норма. Перший за порядком переліку отримував значення, прочитані за спільним ім'ям,
        // тобто, можливо, ЧУЖІ, а другий — нічого. Тепер жоден з них не читається, знімок неповний
        // (D-187: зниклих немає), і кожен названо у відмові.
        // ⚠ Лише для адресації ІМЕНЕМ: шлях PI Web API в AF унікальний, а два GUID на одному шляху —
        // кандидати на перепривʼязку (D-212), їх розводить планувальник.
        var byName = adapter.Transport == ExternalTransport.PiSqlClient;
        var ambiguous = elements.Values
            .Where(e => byName && !string.IsNullOrWhiteSpace(e.ReadAddress))
            .GroupBy(e => e.ReadAddress, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        foreach (var element in ambiguous.OrderBy(e => e.ExternalId, StringComparer.Ordinal))
        {
            complete = false;
            rejections.Add(Truncate(
                $"element={element.ExternalId}; path={element.ReadAddress}; error=ECR-INT-0422; "
                + "messageKey=err.ECR-INT-0422.elementNameAmbiguous"));
        }

        var skipped = ambiguous.Select(e => e.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements.Values
                     .Where(e => !string.IsNullOrWhiteSpace(e.ReadAddress) && !skipped.Contains(e.ExternalId)))
        {
            foreach (var attribute in attributes)
            {
                addresses.TryAdd($"{element.ReadAddress}{AttributeSeparator}{attribute}", (element.ExternalId, attribute));
            }
        }

        var values = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        if (addresses.Count > 0)
        {
            var read = await adapter.ReadCurrentAsync(dataSourceId, [.. addresses.Keys], ct).ConfigureAwait(false);

            foreach (var point in read.Values)
            {
                if (!addresses.TryGetValue(point.SourcePath, out var address))
                {
                    continue;
                }

                if (!values.TryGetValue(address.ExternalId, out var byAttribute))
                {
                    byAttribute = new Dictionary<string, object?>(StringComparer.Ordinal);
                    values[address.ExternalId] = byAttribute;
                }

                byAttribute[address.Attribute] = point.ValueNumeric is { } number ? number : point.ValueString;
            }

            foreach (var failure in read.Failures)
            {
                complete = false;
                var element = addresses.TryGetValue(failure.SourcePath, out var address) ? address.ExternalId : "—";
                rejections.Add(Truncate(
                    $"element={element}; path={failure.SourcePath}; error={failure.ErrorCode}; messageKey={failure.MessageKey}"));
            }
        }

        // ⚠ Ім'я елемента — назва (і в Manual — код) автоствореного запису (D-212 Q4).
        var snapshot = elements.Values
            .Select(e => new RegistrySyncSourceElement(
                e.ExternalId,
                e.Path,
                values.TryGetValue(e.ExternalId, out var byAttribute)
                    ? byAttribute
                    : new Dictionary<string, object?>(StringComparer.Ordinal),
                e.Name))
            .ToList();

        return new Snapshot(snapshot, complete, rejections);
    }

    /// <summary>Зв'язки <c>dic.RegistryExternalKey</c> цього джерела на записи цього довідника.</summary>
    private async Task<List<RegistrySyncLink>> LinksAsync(int dataSourceId, int registryDefId, CancellationToken ct)
        => await (
                from key in db.RegistryExternalKeys.AsNoTracking()
                join entry in db.RegistryEntries.AsNoTracking() on key.RegistryEntryId equals entry.Id
                where key.DataSourceId == dataSourceId && entry.RegistryDefId == registryDefId
                orderby key.Id
                select new RegistrySyncLink(key.ExternalId, key.RegistryEntryId, key.ExternalPath, key.MissingInSourceSince))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Поточні значення змаплених полів і стан (увімкнено) прив'язаних ЖИВИХ записів.</summary>
    private async Task<List<RegistrySyncEntryState>> EntriesAsync(
        IReadOnlyList<RegistrySyncLink> links,
        IReadOnlyList<RegistrySyncFieldMapping> mappings,
        CancellationToken ct)
    {
        var linked = links.Select(l => l.RegistryEntryId).Distinct().ToList();
        var types = mappings
            .GroupBy(m => m.RegistryFieldDefId)
            .ToDictionary(g => g.Key, g => g.First().DataType);
        var fieldIds = types.Keys.ToList();

        var svcId = await db.Users
            .AsNoTracking()
            .Where(u => u.UserName == IntegrationActor.UserName)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var alive = new List<(long Id, bool IsActive, DateOnly? ValidFrom, DateOnly? ValidTo, int? ChangedBy)>();
        var stored = new List<RegistryValue>();

        foreach (var chunk in linked.Chunk(ChunkSize))
        {
            var ids = chunk.ToList();

            // ⚠ Видалений логічно запис у стан не йде: планувальник його не
            // планує, а писати в нього синк однаково не має права.
            var rows = await db.RegistryEntries
                .AsNoTracking()
                .Where(e => ids.Contains(e.Id) && !e.IsDeleted)
                .Select(e => new { e.Id, e.IsActive, e.ValidFrom, e.ValidTo, e.ChangedByUserId })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            alive.AddRange(rows.Select(r => (r.Id, r.IsActive, r.ValidFrom, r.ValidTo, r.ChangedByUserId)));

            if (fieldIds.Count > 0)
            {
                stored.AddRange(await db.RegistryValues
                    .AsNoTracking()
                    .Where(v => ids.Contains(v.RegistryEntryId) && fieldIds.Contains(v.RegistryFieldDefId))
                    .ToListAsync(ct)
                    .ConfigureAwait(false));
            }
        }

        var byEntry = stored.ToLookup(v => v.RegistryEntryId);

        return [.. alive.Select(e => new RegistrySyncEntryState(
            e.Id,
            byEntry[e.Id]
                .GroupBy(v => v.RegistryFieldDefId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var value = g.OrderByDescending(v => v.Id).First();
                        return new RegistrySyncCurrentValue(
                            Typed(types[value.RegistryFieldDefId], value),
                            IsHuman(value.ChangedByUserId, svcId));
                    }),
            e.IsActive,
            e.ValidFrom,
            e.ValidTo,
            IsHuman(e.ChangedBy, svcId)))];
    }

    /// <summary>
    /// Відкидає події, для яких ОСТАННЯ подія того самого предмета цієї сутності має те саме
    /// значення. Один запит на прогін.
    /// </summary>
    private async Task<List<SyncEvent>> DeduplicateAsync(int sourceEntityId, List<SyncEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return events;
        }

        var statuses = CollectionCoverage.RegistryStatuses.ToList();
        var journal = await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => c.SourceEntityId == sourceEntityId
                        && c.PeriodKey == null
                        && c.Status != null
                        && statuses.Contains(c.Status)
                        && c.Details != null
                        && c.Details.Contains(DedupKeyPrefix))
            .OrderBy(c => c.Id)
            .Select(c => c.Details!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Предмет → значення ОСТАННЬОЇ його події (порядок за Id: пізніша перезаписує).
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var details in journal)
        {
            if (ParseKey(details) is { } key)
            {
                latest[key.Subject] = key.Value;
            }
        }

        var fresh = new List<SyncEvent>();
        foreach (var e in events)
        {
            if (latest.TryGetValue(e.Key.Subject, out var value) && value == e.Key.Value)
            {
                continue;
            }

            latest[e.Key.Subject] = e.Key.Value;
            fresh.Add(e);
        }

        return fresh;
    }

    /// <summary>
    /// Автор — людина: відомий і не <c>svc-integration</c>, або НЕВІДОМИЙ (<c>null</c>, значення
    /// до RT-04). ⛔ Невідомий = людина (<c>D-118</c>, дефолт координатора S7): синк не перетирає
    /// значення, про автора якого нічого не відомо.
    /// </summary>
    private static bool IsHuman(int? changedByUserId, int? svcId)
        => changedByUserId is not { } by || by != svcId;

    /// <summary>
    /// Типізоване значення в тій формі, яку повертає приведення планувальника
    /// (<c>RegistrySyncPlanner.TryConvert</c>): інакше «те саме» значення
    /// порівнювалось би як різне й давало б вічне оновлення.
    /// </summary>
    private static object? Typed(CellDataType type, RegistryValue value) => type switch
    {
        CellDataType.String => value.ValueString,
        CellDataType.Int or CellDataType.Decimal => value.ValueNumeric,
        CellDataType.Bool => value.ValueBool,
        CellDataType.Date => value.ValueDate,
        CellDataType.Lookup => value.ValueRefEntryId,
        CellDataType.Unit => value.ValueUnitId,
        _ => null,
    };

    private static string Status(RegistrySyncEventKind kind) => kind switch
    {
        RegistrySyncEventKind.Diverged => CollectionCoverage.RegistryDiverged,
        RegistrySyncEventKind.ConflictKeptManual => CollectionCoverage.RegistryConflictKeptManual,
        RegistrySyncEventKind.SourceMissing => CollectionCoverage.RegistrySourceMissing,
        RegistrySyncEventKind.ElementUnlinked => CollectionCoverage.RegistryElementUnlinked,
        RegistrySyncEventKind.ValueRejected => CollectionCoverage.RegistryValueRejected,
        RegistrySyncEventKind.AutoCreated => CollectionCoverage.RegistryAutoCreated,
        RegistrySyncEventKind.Deactivated => CollectionCoverage.RegistryDeactivated,
        RegistrySyncEventKind.Reactivated => CollectionCoverage.RegistryReactivated,
        RegistrySyncEventKind.RuleViolation => CollectionCoverage.RegistryRuleViolation,
        RegistrySyncEventKind.ExternalKeyRelinked => CollectionCoverage.RegistryExternalKeyRelinked,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Невідомий вид події синку."),
    };

    /// <summary>Подія планувальника або виконавця.</summary>
    /// <param name="e">Подія.</param>
    /// <param name="since">
    /// Момент зникнення (<c>MissingInSourceSince</c>) — у пояснення й у ЗНАЧЕННЯ ключа дедупу: новий
    /// епізод зникнення дає нову подію. <c>null</c> — без позначки.
    /// </param>
    private static SyncEvent Event(RegistrySyncEvent e, DateTime? since = null)
    {
        var status = Status(e.Kind);
        var parts = new List<string> { $"element={e.ExternalId ?? "—"}" };

        if (e.RegistryEntryId is { } entry)
        {
            parts.Add($"entry={entry.ToString(CultureInfo.InvariantCulture)}");
        }

        if (e.FieldCode is not null)
        {
            parts.Add($"field={e.FieldCode}");
            parts.Add($"ecr={Text(e.CurrentValue)}");
            parts.Add($"source={Text(e.SourceValue)}");
        }

        if (e.ErrorCode is not null)
        {
            parts.Add($"error={e.ErrorCode}");
        }

        if (e.MessageKey is not null)
        {
            parts.Add($"messageKey={e.MessageKey}");
        }

        var sinceText = since is { } moment ? $"since={Moment(moment)}" : null;
        if (sinceText is not null)
        {
            parts.Add(sinceText);
        }

        // ⚠ Значення в ECR (`ecr=`) у ключ НЕ входить: людина, що змінила своє значення, не
        // робить незмінне джерело новою подією.
        var subject = $"element={e.ExternalId}; entry={e.RegistryEntryId}; field={e.FieldCode}";
        var value = e.FieldCode is null
            ? sinceText ?? string.Empty
            : $"source={Text(e.SourceValue)}; error={e.ErrorCode}; messageKey={e.MessageKey}";

        return new SyncEvent(status, string.Join("; ", parts), KeyOf(status, subject, value));
    }

    /// <summary>Перепривʼязка виконана.</summary>
    private static SyncEvent Relinked(RegistrySyncRelink r)
    {
        var status = CollectionCoverage.RegistryExternalKeyRelinked;
        var entry = r.RegistryEntryId.ToString(CultureInfo.InvariantCulture);

        return new SyncEvent(
            status,
            Truncate($"element={r.NewExternalId}; entry={entry}; old={r.OldExternalId}; path={r.Path}"),
            KeyOf(status, $"element={r.NewExternalId}; entry={entry}; relink", $"old={r.OldExternalId}"));
    }

    /// <summary>Порушення правила довідника після запису синком (рядок записано).</summary>
    private static SyncEvent Violation(RegistryRuleViolationDto v, IReadOnlyDictionary<long, string> externalIds)
    {
        var status = CollectionCoverage.RegistryRuleViolation;
        var entry = v.EntryId.ToString(CultureInfo.InvariantCulture);
        var element = externalIds.GetValueOrDefault(v.EntryId) ?? "—";
        var message = v.Params.GetValueOrDefault("message");
        var valueParam = v.Params.GetValueOrDefault("value");

        return new SyncEvent(
            status,
            Truncate($"element={element}; entry={entry}; code={v.EntryCode}; rule={v.Rule}; severity={v.Severity}; "
                     + $"messageKey={v.MessageKey}; message={message ?? "—"}"),
            KeyOf(
                status,
                $"entry={entry}; rule={v.Rule}",
                $"severity={v.Severity}; messageKey={v.MessageKey}; message={message}; value={valueParam}"));
    }

    /// <summary>Елемент з GUID, прив'язаним до запису іншого довідника цього джерела.</summary>
    private static SyncEvent Foreign(string externalId)
    {
        var status = CollectionCoverage.RegistryElementUnlinked;

        return new SyncEvent(
            status,
            Truncate($"element={externalId}; error={ErrorCodes.RegistryEntryInUse}; messageKey={ExternalKeyTakenKey}; "
                     + "not linked (taken by another registry)"),
            KeyOf(status, $"element={externalId}; foreign", "taken"));
    }

    /// <summary>Запис для нового елемента не створено: відмова writer'а.</summary>
    private static SyncEvent Refused(CreateRefusal r, long? holderId)
    {
        var status = CollectionCoverage.RegistryElementUnlinked;
        var id = holderId?.ToString(CultureInfo.InvariantCulture);
        var value = $"code={r.Create.Code}; id={id}; field={r.Field}; error={r.ErrorCode}; messageKey={r.MessageKey}";

        return new SyncEvent(
            status,
            Truncate($"element={r.Create.ExternalId}; code={r.Create.Code ?? "—"}; id={id ?? "—"}; field={r.Field ?? "—"}; "
                     + $"error={r.ErrorCode ?? "—"}; messageKey={r.MessageKey ?? "—"}; not created (writer)"),
            KeyOf(status, $"element={r.Create.ExternalId}; create", value));
    }

    private static CreateRefusal Refusal(RegistrySyncCreate create, RegistryEntryImportError error)
        => new(create, ErrorCodeOf(error.MessageKey), error.MessageKey) { Field = error.Field };

    private static CreateRefusal Refusal(RegistrySyncCreate create, Exception failure)
    {
        var (code, messageKey, field) = Describe(failure);
        return new CreateRefusal(create, code, messageKey) { Field = field };
    }

    private static SyncEvent Pending(RegistrySyncUpdate u)
    {
        var subject = $"entry={u.RegistryEntryId.ToString(CultureInfo.InvariantCulture)}; field={u.FieldCode}";
        var value = $"source={Text(u.NewValue)}";

        return new SyncEvent(
            CollectionCoverage.RegistryPendingUpdate,
            $"{subject}; ecr={Text(u.OldValue)}; {value}; not applied (Local, reconciliation only)",
            KeyOf(CollectionCoverage.RegistryPendingUpdate, subject, value));
    }

    private static SyncEvent Pending(RegistrySyncPathChange p)
    {
        var subject = $"element={p.ExternalId}; entry={p.RegistryEntryId.ToString(CultureInfo.InvariantCulture)}; path";
        var value = $"path={p.NewPath}";

        return new SyncEvent(
            CollectionCoverage.RegistryPendingUpdate,
            $"element={p.ExternalId}; entry={p.RegistryEntryId.ToString(CultureInfo.InvariantCulture)}; "
            + $"path={p.OldPath ?? "—"} -> {p.NewPath}; not applied (Local, reconciliation only)",
            KeyOf(CollectionCoverage.RegistryPendingUpdate, subject, value));
    }

    /// <summary>Відмова writer'а по рядку пакета.</summary>
    private static SyncEvent Rejected(
        RegistryEntryUpdate update, RegistryEntryImportError error, IReadOnlyDictionary<long, string> externalIds)
        => Rejected(update, error.Field, ErrorCodeOf(error.MessageKey), error.MessageKey, externalIds);

    /// <summary>Відмова writer'а на весь поштучний пакет (<c>ECR-REG-4092</c> тощо).</summary>
    private static SyncEvent Rejected(
        RegistryEntryUpdate update, Exception failure, IReadOnlyDictionary<long, string> externalIds)
    {
        var (code, messageKey, field) = Describe(failure);
        return Rejected(update, field, code, messageKey, externalIds);
    }

    private static (string? Code, string? MessageKey, string? Field) Describe(Exception failure)
    {
        var (code, details) = failure switch
        {
            EcrException ecr => (ecr.ErrorCode, ecr.Details),
            DomainException domain => (domain.ErrorCode, domain.Details),
            _ => ("ECR-REG-0422", null),
        };

        var messageKey = details?.GetValueOrDefault("messageKey") as string;
        var field = details?.GetValueOrDefault("fieldCode") as string ?? details?.GetValueOrDefault("fields") as string;
        return (code, messageKey, field);
    }

    private static SyncEvent Rejected(
        RegistryEntryUpdate update,
        string? field,
        string? errorCode,
        string? messageKey,
        IReadOnlyDictionary<long, string> externalIds)
    {
        var entry = update.RegistryEntryId.ToString(CultureInfo.InvariantCulture);
        var element = externalIds.GetValueOrDefault(update.RegistryEntryId) ?? "—";

        // Поле названо — значення саме його; ні — усі значення пакета цього запису.
        var source = field is not null && update.Values.TryGetValue(field, out var raw)
            ? Text(raw)
            : field == RegistryEntryWriter.ValidityFieldCode && update.Validity is { } window
                ? RegistrySyncValidity.Text(window)
                : string.Join(",", update.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={Text(v.Value)}"));

        var subject = $"element={element}; entry={entry}; field={field}";
        var value = $"source={source}; error={errorCode}; messageKey={messageKey}";

        return new SyncEvent(
            CollectionCoverage.RegistryValueRejected,
            $"element={element}; entry={entry}; field={field ?? "—"}; source={source}; "
            + $"error={errorCode ?? "—"}; messageKey={messageKey ?? "—"}; not applied (writer)",
            KeyOf(CollectionCoverage.RegistryValueRejected, subject, value));
    }

    /// <summary>Код помилки з ключа каталогу: <c>err.ECR-REG-0422.x</c> → <c>ECR-REG-0422</c>.</summary>
    private static string? ErrorCodeOf(string? messageKey)
    {
        var parts = messageKey?.Split('.');
        return parts is { Length: >= 3 } && parts[0] == "err" ? parts[1] : null;
    }

    private static DedupKey KeyOf(string status, string subject, string value)
        => new(Hash($"{status}\u001f{subject}"), Hash(value));

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Пояснення + ключ дедупу; ключ — у кінці й ніколи не обрізається.</summary>
    private static string WithKey(string details, DedupKey key)
    {
        var suffix = $"{DedupKeyPrefix}{key.Subject}:{key.Value}";
        var room = CollectionCoverage.MaxDetailsLength - suffix.Length;
        return (details.Length > room ? details[..room] : details) + suffix;
    }

    private static DedupKey? ParseKey(string details)
    {
        var at = details.LastIndexOf(DedupKeyPrefix, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var key = details[(at + DedupKeyPrefix.Length)..].Split(':');
        return key.Length == 2 ? new DedupKey(key[0], key[1]) : null;
    }

    private static string Text(object? value) => value switch
    {
        null => "∅",
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "∅",
    };

    private static string Moment(DateTime moment)
        => moment.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    private static DateTime WholeSeconds(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static string Truncate(string text)
        => text.Length > CollectionCoverage.MaxDetailsLength ? text[..CollectionCoverage.MaxDetailsLength] : text;

    private static BusinessRuleException Unavailable(string message, string messageKey, (string Key, string Value) extra)
        => new(
            SourceUnavailable,
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = messageKey,
                [extra.Key] = extra.Value,
            });

    /// <summary>Прочитаний знімок джерела.</summary>
    /// <param name="Elements">Елементи з GUID, іменем і прочитаними атрибутами.</param>
    /// <param name="IsComplete">Чи можна за ним судити про зникнення елементів.</param>
    /// <param name="Rejections">Пояснення відмов читання окремих шляхів.</param>
    private sealed record Snapshot(
        IReadOnlyList<RegistrySyncSourceElement> Elements, bool IsComplete, IReadOnlyList<string> Rejections);

    /// <summary>Ключ дедупу: хеш предмета й хеш значення.</summary>
    private sealed record DedupKey(string Subject, string Value);

    /// <summary>Подія до запису в журнал.</summary>
    private sealed record SyncEvent(string Status, string Details, DedupKey Key);

    /// <summary>Спільне для всіх спроб прогону.</summary>
    /// <param name="RegistryDefId">Довідник.</param>
    /// <param name="RegistryCode">Код довідника — для <c>aud.StructureChange</c>.</param>
    /// <param name="DataSourceId">Джерело.</param>
    /// <param name="Now">Момент прогону (цілі секунди).</param>
    /// <param name="Links">Зв'язки на момент читання.</param>
    /// <param name="Marks">Позначки зникнення, які ставить цей прогін.</param>
    private sealed record ApplyContext(
        int RegistryDefId,
        string RegistryCode,
        int DataSourceId,
        DateTime Now,
        IReadOnlyList<RegistrySyncLink> Links,
        IReadOnlyList<RegistrySyncLinkMark> Marks)
    {
        private readonly Dictionary<string, RegistrySyncLink> _links =
            Links.ToDictionary(l => l.ExternalId, StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _marked =
            Marks.Select(m => m.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Момент зникнення елемента: позначку ставить цей прогін — <see cref="Now"/>; вона вже
        /// була — збережена. Для повернення — та, що знімається.
        /// </summary>
        public DateTime? SinceOf(string? externalId)
            => externalId is null
                ? null
                : _marked.Contains(externalId) ? Now : _links.GetValueOrDefault(externalId)?.MissingInSourceSince;

        /// <summary>Запис → елемент: зв'язки й елементи плану (для подій).</summary>
        public Dictionary<long, string> ExternalIds(RegistrySyncPlan plan)
        {
            var map = new Dictionary<long, string>();
            foreach (var link in Links)
            {
                map.TryAdd(link.RegistryEntryId, link.ExternalId);
            }

            foreach (var activation in plan.Deactivations.Concat(plan.Reactivations))
            {
                map[activation.RegistryEntryId] = activation.ExternalId;
            }

            foreach (var relink in plan.Relinks)
            {
                map[relink.RegistryEntryId] = relink.NewExternalId;
            }

            return map;
        }
    }

    /// <summary>Операції над ключами одного прогону.</summary>
    private sealed record KeyOps(
        IReadOnlyList<RegistrySyncRelink> Relinks,
        IReadOnlyList<RegistrySyncLinkMark> Marks,
        IReadOnlyList<RegistrySyncLinkMark> Clears,
        IReadOnlyList<RegistrySyncPathChange> Paths)
    {
        public static KeyOps None { get; } = new([], [], [], []);

        public bool IsEmpty => Relinks.Count == 0 && Marks.Count == 0 && Clears.Count == 0 && Paths.Count == 0;
    }

    /// <summary>Закомічена спроба.</summary>
    /// <param name="Created">Створені записи з Id.</param>
    /// <param name="Updates">Застосовані оновлення (зокрема увімкнення/вимкнення).</param>
    /// <param name="Relinks">Виконані перепривʼязки.</param>
    /// <param name="Violations">Порушення правил довідника після запису.</param>
    private sealed record Outcome(
        IReadOnlyList<(RegistrySyncCreate Create, long EntryId)> Created,
        IReadOnlyList<RegistryEntryUpdate> Updates,
        IReadOnlyList<RegistrySyncRelink> Relinks,
        IReadOnlyList<RegistryRuleViolationDto> Violations);

    /// <summary>Результат спроби: виконане або відмови рядків (тоді відкочено).</summary>
    private sealed record AttemptResult(
        Outcome? Outcome,
        IReadOnlyList<RegistryEntryImportError> CreateErrors,
        IReadOnlyList<RegistryEntryImportError> UpdateErrors);

    /// <summary>Запис для нового елемента не створено.</summary>
    private sealed record CreateRefusal(RegistrySyncCreate Create, string? ErrorCode, string? MessageKey)
    {
        /// <summary>Поле, назване у відмові.</summary>
        public string? Field { get; init; }
    }

    /// <summary>Сигнал відкату транзакції спроби: відмова рядків writer'а.</summary>
    private sealed class RollbackAttempt : Exception
    {
    }
}
