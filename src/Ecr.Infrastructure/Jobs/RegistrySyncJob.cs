// src/Ecr.Infrastructure/Jobs/RegistrySyncJob.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Errors;
using Ecr.Application.Integration.RegistrySync;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Синхронізація довідника із зовнішнім джерелом для сутності збору з
/// <c>RegistryDefId</c> (<c>ФВ-8.10</c>, <c>ФВ-8.11</c>; FEATURE-REGISTRY-SYNC S5, S7).
/// </summary>
/// <remarks>
/// Окремий інтерфейс, а не маркер черги: задачу ставить <see cref="CollectionJob"/>
/// тим самим розкладом і тією самою кнопкою <c>POST /sources/{id}/collect</c>, тож
/// окремого коду в черзі Quartz вона не потребує.
/// </remarks>
public interface IRegistrySyncJob
{
    /// <summary>Звіряє довідник сутності з джерелом і (для <c>External</c>/<c>Hybrid</c>) застосовує оновлення.</summary>
    /// <param name="sourceEntityId">Сутність джерела, прив'язана до довідника.</param>
    /// <param name="ct">Скасування.</param>
    public Task ExecuteAsync(int sourceEntityId, CancellationToken ct);
}

/// <summary>
/// Читає джерело й довідник, будує план <see cref="RegistrySyncPlanner"/>, для
/// <c>External</c>/<c>Hybrid</c> застосовує його через <see cref="RegistryEntryWriter"/>
/// (S7), а події пише в журнал покриття.
/// </summary>
/// <remarks>
/// ⛔ У <c>dic.RegistryValue</c> задача пише ЛИШЕ через <see cref="RegistryEntryWriter.UpdateAsync"/>
/// (лише оновлення, адресація за Id) від <c>svc-integration</c>: ревізія даних, ключі, аудит
/// <c>RegistryValueChanged</c> і автор <c>ChangedByUserId</c> (RT-04) — ті самі, що в ручного
/// запису. Пакет на довідник — в ОКРЕМОМУ DI-scope зі своїм автором; після <c>Applied = false</c>
/// scope закривається без <c>SaveChanges</c>.
/// <para>
/// ⚠ <c>Local</c> — лише звірка (<c>D-49</c>): у <c>dic.*</c> не пишеться нічого, зміна шляху
/// журналюється подією <see cref="CollectionCoverage.RegistryPendingUpdate"/>. Для
/// <c>External</c>/<c>Hybrid</c> зміна шляху — <c>RegistryExternalKey.MarkSynced(newPath)</c>.
/// </para>
/// <para>
/// ⚠ «Все або нічого» writer'а (рішення S7): відмова рядків → ці записи йдуть подією
/// <see cref="CollectionCoverage.RegistryValueRejected"/>, решта пакета — ОДНИМ повтором;
/// відмова на весь пакет (<c>ECR-REG-4092</c>, зокрема <c>keyTakenConcurrently</c> від індексу) або
/// невдалий повтор → решта поштучно, кожен у власному scope. Один поганий запис не блокує
/// довідник, а кількість спроб обмежена. Два записи пакета з тим самим ключем — помилки обох
/// рядків (<c>keyDuplicateInBatch</c>) від writer'а, а не падіння прогону.
/// </para>
/// <para>
/// ⚠ Дедуп подій: у <c>Details</c> — ключ <c>key=&lt;предмет&gt;:&lt;значення&gt;</c>. Предмет —
/// статус + елемент + запис + поле, значення — значення джерела (+ код і ключ відмови). Подія не
/// пишеться, якщо ОСТАННЯ подія того самого предмета цієї сутності має те саме значення (один
/// запит на прогін). Обмеження: подія без значення (зниклий/неприв'язаний елемент), що зникла й
/// повернулась, повторно не пишеться — ознаки «розв'язано» журнал не має.
/// </para>
/// <para>
/// ⚠ «Останній автор — людина» (<c>D-118</c>): автор є і це не <c>svc-integration</c>, АБО автор
/// невідомий (<c>null</c> — значення до RT-04). Невідомий = людина: значення лишається, подія
/// <see cref="CollectionCoverage.RegistryConflictKeptManual"/> (дефолт координатора).
/// </para>
/// <para>
/// ⚠ Приведення одиниць на межі (<c>D-173</c>) ще не існує в коді: значення
/// йде в планувальник в одиниці джерела.
/// </para>
/// </remarks>
public sealed class RegistrySyncJob(
    EcrDbContext db,
    IEnumerable<IExternalDataSource> sources,
    IntegrationActor actor,
    IClock clock,
    IServiceScopeFactory scopes) : IRegistrySyncJob
{
    /// <summary>Префікс ключа дедупу в <c>Details</c> події.</summary>
    public const string DedupKeyPrefix = "; key=";

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

        var links = await LinksAsync(dataSource.Id, registryDefId, ct).ConfigureAwait(false);

        var snapshot = await SnapshotAsync(adapter, dataSource.Id, entity, mappings, links.Count > 0, ct).ConfigureAwait(false);

        var entries = await EntriesAsync(links, mappings, ct).ConfigureAwait(false);

        var plan = RegistrySyncPlanner.Plan(new RegistrySyncInput(
            registryDefId,
            registry.SourceKind,
            snapshot.IsComplete,
            snapshot.Elements,
            links,
            entries,
            mappings));

        var now = clock.UtcNow;
        var events = new List<SyncEvent>();

        events.AddRange(snapshot.Rejections.Select(r => new SyncEvent(
            CollectionCoverage.RegistryValueRejected, r, KeyOf(CollectionCoverage.RegistryValueRejected, r, value: string.Empty))));
        events.AddRange(plan.Events.Select(Event));

        // TODO PR-6: перепривʼязку задача ще не виконує — доти журнал той самий, що до
        // D-212 (зниклий старий GUID і неприв'язаний новий), а не мовчить про обидва.
        // Creates, MissingMarks, MissingClears, Deactivations, Reactivations — теж PR-6; поки
        // задача не передає Name, IsActive, MissingInSourceSince і політику, вони порожні, а
        // MissingMarks (MarkOrphaned) супроводжує подія SourceMissing з plan.Events.
        events.AddRange(plan.Relinks.SelectMany(r => new[]
        {
            Event(new RegistrySyncEvent(RegistrySyncEventKind.SourceMissing, r.OldExternalId, r.RegistryEntryId)),
            Event(new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, r.NewExternalId, null)),
        }));

        var pathsChanged = false;

        if (registry.SourceKind is RegistrySourceKind.External or RegistrySourceKind.Hybrid)
        {
            var externalIds = links
                .GroupBy(l => l.RegistryEntryId)
                .ToDictionary(g => g.Key, g => g.First().ExternalId);

            events.AddRange(await ApplyUpdatesAsync(registryDefId, plan.Updates, externalIds, ct).ConfigureAwait(false));
            pathsChanged = await MarkPathsAsync(dataSource.Id, plan.PathChanges, now, ct).ConfigureAwait(false);
        }
        else
        {
            // Local — лише звірка: оновлень планувальник тут не дає, шлях не пишеться.
            events.AddRange(plan.Updates.Select(Pending));
            events.AddRange(plan.PathChanges.Select(Pending));
        }

        var fresh = await DeduplicateAsync(entity.Id, events, ct).ConfigureAwait(false);

        if (fresh.Count == 0 && !pathsChanged)
        {
            return;
        }

        db.CollectionCoverages.AddRange(fresh.Select(e => CollectionCoverage.SkippedRegistry(
            entity.Id, e.Status, WithKey(e.Details, e.Key), now)));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Застосовує оновлення планувальника через writer — пакет на довідник, у власному scope,
    /// з обмеженим повтором (див. <see cref="RegistrySyncJob"/>).
    /// </summary>
    /// <returns>Події відмов writer'а.</returns>
    private async Task<List<SyncEvent>> ApplyUpdatesAsync(
        int registryDefId,
        IReadOnlyList<RegistrySyncUpdate> updates,
        IReadOnlyDictionary<long, string> externalIds,
        CancellationToken ct)
    {
        var rejected = new List<SyncEvent>();

        if (updates.Count == 0)
        {
            return rejected;
        }

        List<RegistryEntryUpdate> remaining = [.. updates
            .GroupBy(u => u.RegistryEntryId)
            .Select(g => new RegistryEntryUpdate(
                g.Key,
                g.ToDictionary(u => u.FieldCode, u => u.NewValue, StringComparer.Ordinal)))];

        var outcome = await TryWriteAsync(registryDefId, remaining, ct).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return rejected;
        }

        if (outcome.Failure is null)
        {
            // Відмова рядків: вони — у журнал, решта пакета — одним повтором.
            var failedRows = outcome.Errors.Select(e => e.Row).ToHashSet();
            rejected.AddRange(outcome.Errors.Select(e => Rejected(remaining[e.Row - 1], e, externalIds)));
            remaining = [.. remaining.Where((_, i) => !failedRows.Contains(i + 1))];

            if (remaining.Count == 0)
            {
                return rejected;
            }

            outcome = await TryWriteAsync(registryDefId, remaining, ct).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                return rejected;
            }
        }

        // Відмова на весь пакет (ключ, 4092) або невдалий повтор — решта поштучно.
        foreach (var single in remaining)
        {
            var one = await TryWriteAsync(registryDefId, [single], ct).ConfigureAwait(false);
            if (one.Succeeded)
            {
                continue;
            }

            if (one.Failure is { } failure)
            {
                rejected.Add(Rejected(single, failure, externalIds));
            }
            else
            {
                rejected.AddRange(one.Errors.Select(e => Rejected(single, e, externalIds)));
            }
        }

        return rejected;
    }

    /// <summary>Одна спроба запису пакета — у власному DI-scope від <c>svc-integration</c>.</summary>
    /// <remarks>
    /// ⛔ Scope закривається без <c>SaveChanges</c>, що б не повернув writer: при
    /// <c>Applied = true</c> він уже зберіг сам, при <c>false</c> — відстежені зміни мусять пропасти.
    /// </remarks>
    private async Task<WriteOutcome> TryWriteAsync(
        int registryDefId, IReadOnlyList<RegistryEntryUpdate> batch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(ct).ConfigureAwait(false);

        try
        {
            var result = await services.GetRequiredService<RegistryEntryWriter>()
                .UpdateAsync(new RegistryEntryUpdateBatch(registryDefId, batch), ct)
                .ConfigureAwait(false);

            return new WriteOutcome(result.Errors, Failure: null);
        }
        // ⛔ ConcurrencyConflictException — теж відмова пакета, не падіння прогону: індекс
        // UX_RegistryEntryKey_Live (keyTakenConcurrently) спрацьовує, коли паралельний запис
        // випередив блокування (справжня гонка). Обмін ключами між записами пакета (A: k1→k2,
        // B: k2→k1) до індексу вже не доходить — служба ключів пише їх у дві фази (RT-14).
        // Поштучний повтор розводить гонку. Інші EcrException (немає автора, немає довідника) —
        // збій прогону, а не дані: їх не ковтаємо.
        catch (Exception ex) when (ex is BusinessRuleException or DomainException or ConcurrencyConflictException)
        {
            return new WriteOutcome([], ex);
        }
    }

    /// <summary>Зміни шляхів — <c>RegistryExternalKey.MarkSynced</c> на відстежених ключах.</summary>
    /// <returns>Чи змінено хоч один ключ (тоді потрібне збереження).</returns>
    private async Task<bool> MarkPathsAsync(
        int dataSourceId, IReadOnlyList<RegistrySyncPathChange> changes, DateTime now, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return false;
        }

        var byExternalId = changes.ToDictionary(c => c.ExternalId, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var chunk in byExternalId.Keys.Chunk(ChunkSize))
        {
            var ids = chunk.ToList();
            var keys = await db.RegistryExternalKeys
                .Where(k => k.DataSourceId == dataSourceId && ids.Contains(k.ExternalId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var key in keys)
            {
                if (byExternalId.TryGetValue(key.ExternalId, out var change) && key.RegistryEntryId == change.RegistryEntryId)
                {
                    key.MarkSynced(change.NewPath, now);
                    changed = true;
                }
            }
        }

        return changed;
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
        return [.. maps
            .Where(m => fields.ContainsKey(m.TargetRegistryFieldDefId!.Value))
            .Select(m =>
            {
                var field = fields[m.TargetRegistryFieldDefId!.Value];
                return new RegistrySyncFieldMapping(
                    field.Id, field.Code, field.DataType, field.UnitId, m.SourceField, m.IsActive);
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
        bool hasLinks,
        CancellationToken ct)
    {
        // ⚠ Той самий запасний шлях, що в CollectionRunner.Paths: сутність без
        // шляху адресується своїм кодом.
        var root = entity.EntityPath ?? entity.Code;

        var listed = await adapter.DiscoverElementsAsync(dataSourceId, root, ct).ConfigureAwait(false);
        var complete = listed.IsComplete && (listed.Elements.Count > 0 || !hasLinks);

        var elements = new Dictionary<string, SourceElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in listed.Elements)
        {
            // Без GUID елемент не зіставити, а дубль GUID — порушення контракту
            // переліку: і те, і те робить знімок неповним, а не падінням задачі.
            if (string.IsNullOrWhiteSpace(element.ExternalId) || !elements.TryAdd(element.ExternalId, element))
            {
                complete = false;
            }
        }

        var attributes = mappings
            .Select(m => m.SourceAttribute)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Шлях атрибута → (елемент, атрибут).
        var addresses = new Dictionary<string, (string ExternalId, string Attribute)>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements.Values.Where(e => !string.IsNullOrWhiteSpace(e.ReadAddress)))
        {
            foreach (var attribute in attributes)
            {
                addresses.TryAdd($"{element.ReadAddress}{AttributeSeparator}{attribute}", (element.ExternalId, attribute));
            }
        }

        var values = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var rejections = new List<string>();

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

        var snapshot = elements.Values
            .Select(e => new RegistrySyncSourceElement(
                e.ExternalId,
                e.Path,
                values.TryGetValue(e.ExternalId, out var byAttribute)
                    ? byAttribute
                    : new Dictionary<string, object?>(StringComparer.Ordinal)))
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
                select new RegistrySyncLink(key.ExternalId, key.RegistryEntryId, key.ExternalPath))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Поточні значення змаплених полів прив'язаних ЖИВИХ записів.</summary>
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

        var alive = new List<long>();
        var stored = new List<RegistryValue>();

        foreach (var chunk in linked.Chunk(ChunkSize))
        {
            var ids = chunk.ToList();

            // ⚠ Видалений логічно запис у стан не йде: планувальник його не
            // планує, а писати в нього синк однаково не має права.
            alive.AddRange(await db.RegistryEntries
                .AsNoTracking()
                .Where(e => ids.Contains(e.Id) && !e.IsDeleted)
                .Select(e => e.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false));

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

        return [.. alive.Select(id => new RegistrySyncEntryState(
            id,
            byEntry[id]
                .GroupBy(v => v.RegistryFieldDefId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var value = g.OrderByDescending(v => v.Id).First();
                        return new RegistrySyncCurrentValue(
                            Typed(types[value.RegistryFieldDefId], value),
                            IsHuman(value.ChangedByUserId, svcId));
                    })))];
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

    private static SyncEvent Event(RegistrySyncEvent e)
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

        // ⚠ Значення в ECR (`ecr=`) у ключ НЕ входить: людина, що змінила своє значення, не
        // робить незмінне джерело новою подією.
        var subject = $"element={e.ExternalId}; entry={e.RegistryEntryId}; field={e.FieldCode}";
        var value = e.FieldCode is null
            ? string.Empty
            : $"source={Text(e.SourceValue)}; error={e.ErrorCode}; messageKey={e.MessageKey}";

        return new SyncEvent(status, string.Join("; ", parts), KeyOf(status, subject, value));
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
        var (code, details) = failure switch
        {
            EcrException ecr => (ecr.ErrorCode, ecr.Details),
            DomainException domain => (domain.ErrorCode, domain.Details),
            _ => ("ECR-REG-0422", null),
        };

        var messageKey = details?.GetValueOrDefault("messageKey") as string;
        var field = details?.GetValueOrDefault("fieldCode") as string ?? details?.GetValueOrDefault("fields") as string;
        return Rejected(update, field, code, messageKey, externalIds);
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
    /// <param name="Elements">Елементи з GUID і прочитаними атрибутами.</param>
    /// <param name="IsComplete">Чи можна за ним судити про зникнення елементів.</param>
    /// <param name="Rejections">Пояснення відмов читання окремих шляхів.</param>
    private sealed record Snapshot(
        IReadOnlyList<RegistrySyncSourceElement> Elements, bool IsComplete, IReadOnlyList<string> Rejections);

    /// <summary>Ключ дедупу: хеш предмета й хеш значення.</summary>
    private sealed record DedupKey(string Subject, string Value);

    /// <summary>Подія до запису в журнал.</summary>
    private sealed record SyncEvent(string Status, string Details, DedupKey Key);

    /// <summary>Результат однієї спроби запису: помилки рядків або відмова на весь пакет.</summary>
    private sealed record WriteOutcome(IReadOnlyList<RegistryEntryImportError> Errors, Exception? Failure)
    {
        public bool Succeeded => Failure is null && Errors.Count == 0;
    }
}
