// src/Ecr.Infrastructure/Jobs/RegistrySyncJob.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Integration.RegistrySync;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Синхронізація довідника із зовнішнім джерелом для сутності збору з
/// <c>RegistryDefId</c> (<c>ФВ-8.10</c>, <c>ФВ-8.11</c>; FEATURE-REGISTRY-SYNC S5).
/// </summary>
/// <remarks>
/// Окремий інтерфейс, а не маркер черги: задачу ставить <see cref="CollectionJob"/>
/// тим самим розкладом і тією самою кнопкою <c>POST /sources/{id}/collect</c>, тож
/// окремого коду в черзі Quartz вона не потребує.
/// </remarks>
public interface IRegistrySyncJob
{
    /// <summary>Звіряє довідник сутності з джерелом.</summary>
    /// <param name="sourceEntityId">Сутність джерела, прив'язана до довідника.</param>
    /// <param name="ct">Скасування.</param>
    public Task ExecuteAsync(int sourceEntityId, CancellationToken ct);
}

/// <summary>
/// S5 — <b>лише звірка</b>: читає джерело й довідник, будує план
/// <see cref="RegistrySyncPlanner"/> і пише події в журнал покриття.
/// </summary>
/// <remarks>
/// ⛔ У <c>dic.*</c> задача НЕ пише нічого: ні значень, ні ревізії, ні
/// <c>RegistryExternalKey.MarkSynced</c>. Запис — S7, через спільний
/// <c>RegistryEntryWriter</c>. Усе читання — <c>AsNoTracking</c>, а єдиний
/// <c>SaveChanges</c> кладе лише рядки <c>itg.CollectionCoverage</c>.
/// <para>
/// ⚠ Рішення S5 щодо <see cref="RegistrySyncPlan.Updates"/> і
/// <see cref="RegistrySyncPlan.PathChanges"/>: не застосовуються, а журналюються
/// подією <see cref="CollectionCoverage.RegistryPendingUpdate"/> — адміністратор
/// бачить, що саме синк записав би, до того, як S7 почне писати.
/// </para>
/// <para>
/// ⚠ Події пишуться рядками <see cref="CollectionCoverage.SkippedRegistry"/> прямо в
/// <c>itg.CollectionCoverage</c>, а не через <c>ICoverageJournal</c>: той приймає
/// непорожній <c>PeriodKey</c>, а в довідника періоду немає.
/// </para>
/// <para>
/// ⚠ «Останній автор — людина» береться з <c>RegistryValue.ChangedByUserId</c>
/// (RT-04): автор є і це не <c>svc-integration</c>. Автор невідомий
/// (<c>null</c> — значення, записані до RT-04) → <c>false</c>: у режимі звірки
/// це дає <see cref="CollectionCoverage.RegistryPendingUpdate"/> замість
/// <see cref="CollectionCoverage.RegistryConflictKeptManual"/>; для S7 це
/// питання треба закрити до першого запису.
/// </para>
/// <para>
/// ⚠ Приведення одиниць на межі (<c>D-173</c>) ще не існує в коді: значення
/// йде в планувальник в одиниці джерела.
/// </para>
/// </remarks>
public sealed class RegistrySyncJob(
    EcrDbContext db,
    IEnumerable<IExternalDataSource> sources,
    ISourceCatalogReader catalog,
    IntegrationActor actor,
    IClock clock) : IRegistrySyncJob
{
    /// <summary>
    /// Скільки елементів одного рівня віддає каталог; рівно стільки — знімок,
    /// можливо, обрізано (<c>PiAfCatalogReader.MaxNodesPerLevel</c>).
    /// </summary>
    public const int CatalogCeiling = 1_000;

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Роздільник атрибута й елемента в шляху PI AF (<c>ProbeSourcePathHandler.SplitPath</c>).</summary>
    private const char AttributeSeparator = '|';

    /// <summary>Скільки ідентифікаторів іде в один запит <c>IN (…)</c>.</summary>
    private const int ChunkSize = 1_000;

    /// <inheritdoc />
    public async Task ExecuteAsync(int sourceEntityId, CancellationToken ct)
    {
        // ⛔ Автор задачі — svc-integration, як у матеріалізації: коли S7 почне
        // писати, «хто змінив поле» мусить мати відповідь.
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

        var snapshot = await SnapshotAsync(adapter, dataSource.Id, entity, mappings, ct).ConfigureAwait(false);

        var links = await LinksAsync(dataSource.Id, registryDefId, ct).ConfigureAwait(false);
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
        var rows = new List<CollectionCoverage>();

        rows.AddRange(snapshot.Rejections.Select(r => Row(entity.Id, CollectionCoverage.RegistryValueRejected, r, now)));
        rows.AddRange(plan.Events.Select(e => Row(entity.Id, Status(e.Kind), Details(e), now)));
        rows.AddRange(plan.Updates.Select(u => Row(entity.Id, CollectionCoverage.RegistryPendingUpdate, Details(u), now)));
        rows.AddRange(plan.PathChanges.Select(p => Row(entity.Id, CollectionCoverage.RegistryPendingUpdate, Details(p), now)));

        if (rows.Count == 0)
        {
            return;
        }

        db.CollectionCoverages.AddRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

    /// <summary>Знімок джерела: діти елемента сутності й поточні значення змаплених атрибутів.</summary>
    /// <remarks>
    /// ⛔ Повний знімок (<c>D-187</c>) — лише коли каталог не обрізано, кожен елемент
    /// має GUID і <c>ReadCurrentAsync</c> не повернув жодної відмови шляху. Інакше
    /// відсутність елемента нічого не каже, і <c>RegistrySourceMissing</c> не пишеться.
    /// Відмова всього джерела — виняток адаптера; задача стає <c>Failed</c>, а не
    /// пише знімок, якого немає.
    /// </remarks>
    private async Task<Snapshot> SnapshotAsync(
        IExternalDataSource adapter,
        int dataSourceId,
        SourceEntity entity,
        IReadOnlyList<RegistrySyncFieldMapping> mappings,
        CancellationToken ct)
    {
        // ⚠ Той самий запасний шлях, що в CollectionRunner.Paths: сутність без
        // шляху адресується своїм кодом.
        var parent = entity.EntityPath ?? entity.Code;

        var children = await catalog.BrowseAsync(dataSourceId, parent, ct).ConfigureAwait(false);
        var complete = children.Count < CatalogCeiling;

        var elements = new Dictionary<string, SourceEntityDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in children.Where(c => string.Equals(c.DataType, "Element", StringComparison.OrdinalIgnoreCase)))
        {
            // Без GUID елемент не зіставити, а дубль GUID — порушення контракту
            // каталогу: і те, і те робить знімок неповним, а не падінням задачі.
            if (string.IsNullOrWhiteSpace(child.ExternalId) || !elements.TryAdd(child.ExternalId, child))
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

        foreach (var element in elements.Values.Where(e => !string.IsNullOrWhiteSpace(e.EntityPath)))
        {
            foreach (var attribute in attributes)
            {
                addresses.TryAdd($"{element.EntityPath}{AttributeSeparator}{attribute}", (element.ExternalId!, attribute));
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
                e.ExternalId!,
                e.EntityPath,
                values.TryGetValue(e.ExternalId!, out var byAttribute)
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
                            value.ChangedByUserId is { } by && by != svcId);
                    })))];
    }

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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Невідомий вид події синку."),
    };

    private static CollectionCoverage Row(int sourceEntityId, string status, string details, DateTime now)
        => CollectionCoverage.SkippedRegistry(sourceEntityId, status, details, now);

    private static string Details(RegistrySyncEvent e)
    {
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

        return Truncate(string.Join("; ", parts));
    }

    private static string Details(RegistrySyncUpdate u)
        => Truncate(
            $"entry={u.RegistryEntryId.ToString(CultureInfo.InvariantCulture)}; field={u.FieldCode}; "
            + $"ecr={Text(u.OldValue)}; source={Text(u.NewValue)}; not applied (S5, reconciliation only)");

    private static string Details(RegistrySyncPathChange p)
        => Truncate(
            $"element={p.ExternalId}; entry={p.RegistryEntryId.ToString(CultureInfo.InvariantCulture)}; "
            + $"path={p.OldPath ?? "—"} -> {p.NewPath}; not applied (S5, reconciliation only)");

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
}
