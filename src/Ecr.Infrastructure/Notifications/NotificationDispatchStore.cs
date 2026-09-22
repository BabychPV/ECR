// src/Ecr.Infrastructure/Notifications/NotificationDispatchStore.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Notifications;

/// <summary>
/// Дані розсилки поверх <see cref="EcrDbContext"/> з кешем конфігурації за
/// ревізією (<c>BE-34</c>).
/// </summary>
/// <remarks>
/// Кеш побудований тим самим прийомом, що <c>MetadataCache</c> і
/// <c>RegistryEntryCache</c> (D-16): ревізія — ЧАСТИНА КЛЮЧА, тож явної
/// інвалідації немає взагалі. Змінена конфігурація дає інший ключ, і наступний
/// прогін просто читає за ним; старий запис доживає своє і зникає.
/// </remarks>
public sealed class NotificationDispatchStore(EcrDbContext db, IMemoryCache memory) : INotificationDispatchStore
{
    /// <summary>
    /// Стеля правил у знімку: матриця «подія × канал», тобто п'ять подій на
    /// кожен із <see cref="NotificationStore.MaxChannels"/> каналів.
    /// </summary>
    public const int MaxRules = NotificationStore.MaxChannels * 5;

    /// <summary>
    /// Стеля життя знімка.
    /// </summary>
    /// <remarks>
    /// ⚠ Не для актуальності — її тримає ревізія в ключі, — а для пам'яті й для
    /// однієї названої прогалини: проба нижче бачить появу, зникнення,
    /// перейменування й вимкнення каналу чи правила, але НЕ бачить зміну самої
    /// лише межі <see cref="NotificationRule.MinSeverity"/> у наявному правилі
    /// (у тій таблиці немає ні позначки часу, ні <c>rowversion</c>). Така
    /// правка доходить до відправника за цю хвилину, а не миттєво.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<NotificationDispatchPlan> GetPlanAsync(CancellationToken ct)
    {
        var revision = await RevisionAsync(ct).ConfigureAwait(false);
        var key = CacheKey(revision);

        if (memory.TryGetValue(key, out NotificationDispatchPlan? cached) && cached is not null)
        {
            return cached;
        }

        var plan = await LoadAsync(revision, ct).ConfigureAwait(false);

        memory.Set(key, plan, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });

        return plan;
    }

    /// <inheritdoc />
    public Task<bool> WasSentSinceAsync(int channelId, string eventKey, DateTime since, CancellationToken ct)
        => db.NotificationDeliveries
            .AsNoTracking()
            .AnyAsync(
                d => d.ChannelId == channelId
                     && d.EventKey == eventKey
                     && d.Status == NotificationDeliveryStatus.Sent
                     && d.At >= since,
                ct);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Фіксація ОДРАЗУ, рядок за рядком: журнал доставок має пережити падіння
    /// посеред розсилки по каналах — інакше видно було б лише ті спроби, після
    /// яких процес дожив до кінця.
    /// </remarks>
    public async Task AppendDeliveryAsync(NotificationDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ключ знімка конфігурації.</summary>
    /// <param name="revision">Мітка ревізії.</param>
    public static string CacheKey(string revision) => $"notifications:plan:{revision}";

    /// <summary>
    /// Ревізія конфігурації — ОДНИМ запитом.
    /// </summary>
    /// <remarks>
    /// ⛔ Проба навмисно НЕ кешується. Кешувати її означало б, що вимкнений
    /// адміністратором канал ще якийсь час шле — тобто рівно та поведінка, від
    /// якої ревізія в ключі й рятує. Сам запит — чотири агрегати без читання
    /// рядків.
    /// </remarks>
    private async Task<string> RevisionAsync(CancellationToken ct)
    {
        var probe = await db.NotificationChannels
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Channels = g.Count(),
                Latest = (DateTime?)g.Max(c => c.ModifiedAt),
                Rules = db.NotificationRules.Count(),
                EnabledRules = db.NotificationRules.Count(r => r.IsEnabled),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Жодного каналу — жодної розсилки; окрема мітка, щоб порожня
        // конфігурація не ділила ключ із будь-якою іншою.
        return probe is null
            ? "empty"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{probe.Channels}:{probe.Latest:O}:{probe.Rules}:{probe.EnabledRules}");
    }

    /// <summary>
    /// Читає канали й правила ЦІЛКОМ, разом із вимкненими.
    /// </summary>
    /// <param name="revision">Мітка, з якою знімок ляже в кеш.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Фільтр «лише ввімкнені» тут був би ДРУГИМ місцем того самого правила:
    /// його вже знають <c>NotificationDispatchPlan.TargetsOf</c> і
    /// <c>NotificationRule.Matches</c>.
    /// </remarks>
    private async Task<NotificationDispatchPlan> LoadAsync(string revision, CancellationToken ct)
    {
        var channels = await db.NotificationChannels
            .AsNoTracking()
            .OrderBy(c => c.Id)
            .Take(NotificationStore.MaxChannels)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rules = await db.NotificationRules
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new NotificationDispatchPlan(revision, channels, rules);
    }
}
