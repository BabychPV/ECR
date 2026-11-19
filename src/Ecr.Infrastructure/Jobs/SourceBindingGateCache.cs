// src/Ecr.Infrastructure/Jobs/SourceBindingGateCache.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Активна зв'язка джерела (стан Any, біт RecalculateDependents); біт ReadFromDocument — сирий, без ключа.</summary>
public sealed record SourceGateBinding(int Id, int SourceTemplateId, int RegistryDefId, bool ReadFromDocument);

/// <summary>
/// Ворота хука джерела: активні зв'язки, версії шаблонів-джерел (версія → шаблон) і найбільший Id версії на момент
/// читання (новіша версія — ознака, що знімок треба перечитати).
/// </summary>
public sealed record SourceBindingGate(
    IReadOnlyList<SourceGateBinding> Bindings, IReadOnlyDictionary<int, int> SourceTemplateOfVersion, int MaxVersionId)
{
    /// <summary>Ворота з бази: 1 звернення без активних зв'язок, інакше 3.</summary>
    public static async Task<SourceBindingGate> LoadAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.SourceState == DocumentSourceState.Any && b.RecalculateDependents)
            .Select(b => new SourceGateBinding(b.Id, b.SourceTemplateId, b.RegistryDefId, b.ReadFromDocument))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return new SourceBindingGate(bindings, new Dictionary<int, int>(), int.MaxValue);
        }

        var templates = bindings.Select(b => b.SourceTemplateId).Distinct().ToList();
        var versions = await db.TemplateVersions.AsNoTracking()
            .Where(v => templates.Contains(v.TemplateId))
            .Select(v => new { v.Id, v.TemplateId })
            .ToDictionaryAsync(v => v.Id, v => v.TemplateId, ct)
            .ConfigureAwait(false);
        var maxVersion = await db.TemplateVersions.AsNoTracking().MaxAsync(v => (int?)v.Id, ct).ConfigureAwait(false) ?? 0;

        return new SourceBindingGate(bindings, versions, maxVersion);
    }
}

/// <summary>
/// B179 L2 (perf, «perf max»): процесний кеш <see cref="SourceBindingGate"/>. Задача формул НЕ-джерела не звертається до
/// бази взагалі: зв'язки й версії шаблонів-джерел — з кешу, версії документа — з уже прочитаних екземплярів проходу.
/// </summary>
/// <remarks>
/// <para>
/// Інвалідація: явна — <see cref="ISourceBindingGateInvalidation.Invalidate"/> (перемикач стану зв'язки,
/// <c>SwitchDocumentSourceStateHandler</c>); версія новіша за <see cref="SourceBindingGate.MaxVersionId"/> — перечитування
/// (нова версія шаблону-джерела); страховка — <see cref="Lifetime"/>.
/// </para>
/// <para>
/// ⚠ Кілька процесів (Api + Ecr.Worker, кілька вузлів): явне скидання діє лише в процесі перемикача; решта бачать зміну
/// зв'язки не пізніше за <see cref="Lifetime"/>. Зв'язки інакше змінює лише сід на старті (новий процес — порожній кеш).
/// </para>
/// </remarks>
public sealed class SourceBindingGateCache : ISourceBindingGateInvalidation
{
    /// <summary>Страхувальний TTL знімка (межа застарілості між процесами).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private sealed record Entry(SourceBindingGate Gate, long LoadedAtTicks);

    private volatile Entry? _entry;

    /// <summary>Скільки разів знімок справді читали з бази (для тестів).</summary>
    public int Loads => _loads;

    private int _loads;

    /// <summary>Знімок воріт: з кешу, або з бази, якщо кеш порожній, скинутий чи старший за <see cref="Lifetime"/>.</summary>
    public async Task<SourceBindingGate> GetAsync(EcrDbContext db, CancellationToken ct)
    {
        var entry = _entry;
        if (entry is not null && Environment.TickCount64 - entry.LoadedAtTicks < (long)Lifetime.TotalMilliseconds)
        {
            return entry.Gate;
        }

        return await ReloadAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>Перечитує знімок примусово (новіша версія шаблону, ніж відома знімку).</summary>
    public async Task<SourceBindingGate> ReloadAsync(EcrDbContext db, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var generation = Volatile.Read(ref _generation);
        var gate = await SourceBindingGate.LoadAsync(db, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _loads);

        // ⚠ Скидання під час читання: прочитане могло бути ДО коміту перемикача — не кешуємо (наступний виклик перечитає).
        if (Volatile.Read(ref _generation) == generation)
        {
            _entry = new Entry(gate, started);
        }

        return gate;
    }

    private int _generation;

    /// <inheritdoc />
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _entry = null;
    }
}
