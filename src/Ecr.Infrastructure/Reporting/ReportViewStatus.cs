// src/Ecr.Infrastructure/Reporting/ReportViewStatus.cs
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Reporting;

/// <summary>Реалізація в памʼяті (singleton).</summary>
public sealed class ReportViewStatus : IReportViewStatus
{
    private readonly object _gate = new();
    // ✎ P-4b P3: ключ — (стабільні вʼюхи?, id): (false, версія) — _vN/повна генерація версії, (false, 0) — прогін по всіх,
    // (true, шаблон) — стабільні вʼюхи шаблону. Що який успіх знімає — IReportViewStatus (remarks).
    private readonly Dictionary<(bool Stable, int Id), ReportViewFailure> _failures = [];
    // ✎ P-4b: шаблон → покоління останньої позначки «оновлюються»; лічильник наскрізний (монотонний).
    private readonly Dictionary<int, long> _pending = [];
    // ✎ P-4b P2: шаблон → версії, що чекають _vN (переживає злиття задач; забирає задача на старті).
    private readonly Dictionary<int, SortedSet<int>> _waiting = [];
    private long _sequence;

    /// <inheritdoc />
    public long Pending(int templateId)
    {
        lock (_gate)
        {
            var generation = ++_sequence;
            _pending[templateId] = generation;
            return generation;
        }
    }

    /// <inheritdoc />
    public long Pending(int templateId, int templateVersionId)
    {
        lock (_gate)
        {
            AddWaiting(templateId, [templateVersionId]);
            return Pending(templateId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> DrainWaitingVersions(int templateId)
    {
        lock (_gate)
        {
            return _waiting.Remove(templateId, out var versions) ? [.. versions] : [];
        }
    }

    /// <inheritdoc />
    public void RequeueWaitingVersions(int templateId, IEnumerable<int> templateVersionIds)
    {
        ArgumentNullException.ThrowIfNull(templateVersionIds);
        lock (_gate)
        {
            AddWaiting(templateId, templateVersionIds);
        }
    }

    /// <inheritdoc />
    public long PendingGeneration(int templateId)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(templateId, out var generation) ? generation : 0;
        }
    }

    /// <inheritdoc />
    public void Settled(int templateId, long generation)
    {
        lock (_gate)
        {
            // ⛔ Новіша позначка (публікація під час прогону) — злитий повтор ще в черзі: не знімати.
            if (_pending.TryGetValue(templateId, out var current) && current <= generation)
            {
                _pending.Remove(templateId);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> PendingTemplates()
    {
        lock (_gate)
        {
            return [.. _pending.Keys.Order()];
        }
    }

    /// <inheritdoc />
    public void Failed(ReportViewFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _failures[failure.StableTemplateId is { } templateId ? (true, templateId) : (false, failure.TemplateVersionId ?? 0)] = failure;
        }
    }

    /// <inheritdoc />
    public void Succeeded(int? templateVersionId)
    {
        lock (_gate)
        {
            if (templateVersionId is null)
            {
                _failures.Clear();
            }
            else
            {
                _failures.Remove((false, templateVersionId.Value));
            }
        }
    }

    /// <inheritdoc />
    public void StableSucceeded(int templateId)
    {
        lock (_gate)
        {
            _failures.Remove((true, templateId));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReportViewFailure> Snapshot()
    {
        lock (_gate)
        {
            return [.. _failures.Values];
        }
    }

    private void AddWaiting(int templateId, IEnumerable<int> templateVersionIds)
    {
        if (!_waiting.TryGetValue(templateId, out var versions))
        {
            versions = [];
            _waiting[templateId] = versions;
        }

        versions.UnionWith(templateVersionIds);
        if (versions.Count == 0)
        {
            _waiting.Remove(templateId);
        }
    }
}
