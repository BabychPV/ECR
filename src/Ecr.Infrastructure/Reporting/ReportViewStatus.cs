// src/Ecr.Infrastructure/Reporting/ReportViewStatus.cs
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Reporting;

/// <summary>Реалізація в памʼяті (singleton).</summary>
public sealed class ReportViewStatus : IReportViewStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<int, ReportViewFailure> _failures = [];
    // ✎ P-4b: шаблон → покоління останньої позначки «оновлюються»; лічильник наскрізний (монотонний).
    private readonly Dictionary<int, long> _pending = [];
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
            _failures[failure.TemplateVersionId ?? 0] = failure;
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
                _failures.Remove(templateVersionId.Value);
            }
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
}
