// src/Ecr.Infrastructure/Reporting/ReportViewStatus.cs
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Reporting;

/// <summary>Реалізація в памʼяті (singleton).</summary>
public sealed class ReportViewStatus : IReportViewStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<int, ReportViewFailure> _failures = [];
    private readonly HashSet<int> _pending = [];

    /// <inheritdoc />
    public void Pending(int templateId)
    {
        lock (_gate)
        {
            _pending.Add(templateId);
        }
    }

    /// <inheritdoc />
    public void Settled(int templateId)
    {
        lock (_gate)
        {
            _pending.Remove(templateId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> PendingTemplates()
    {
        lock (_gate)
        {
            return [.. _pending.Order()];
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
