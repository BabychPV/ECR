// tests/Ecr.Infrastructure.Tests/Jobs/CapturingJobProgress.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>Збирає останнє повідомлення задачі, щоб тест міг прочитати підсумок <c>FormulaRecalcOutcome</c>.</summary>
internal sealed class CapturingJobProgress : IJobProgress
{
    /// <summary>Останнє (сире) повідомлення.</summary>
    public string? Last { get; private set; }

    /// <summary>Скільки комірок записано за підсумком; <c>null</c> — підсумку немає.</summary>
    public int? Written => FormulaRecalcOutcome.WrittenCountOf("Succeeded", Last);

    /// <summary>Періоди, у які прохід записав комірки (за зростанням).</summary>
    public IReadOnlyList<int> Periods => FormulaRecalcOutcome.WrittenPeriodsOf("Succeeded", Last);

    /// <inheritdoc />
    public Task ReportAsync(int percent, string? message, CancellationToken ct)
    {
        Last = message;

        return Task.CompletedTask;
    }
}
