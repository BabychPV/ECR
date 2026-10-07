// src/Ecr.Infrastructure/Jobs/JobCorrelation.cs
namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Кореляція задачі, що виконується зараз, для коду, який не бачить ні логера, ні
/// контексту Quartz (<see cref="MaintenanceRunFailure"/>, <c>PeriodStateJob</c>).
/// </summary>
/// <remarks>
/// ⛔ SEC (TIER2): текст провалу задачі для користувача містить кореляцію, за якою
/// оператор шукає повний виняток у журналі сервера. Рядок журналу задачі несе
/// кореляцію зі scope, який відкривають <see cref="JobWorker"/> і
/// <see cref="QuartzJobAdapter"/>; та сама кореляція має потрапляти й у текст, інакше
/// екранний текст і журнал не зв'язуються. <see cref="AsyncLocal{T}"/> тече через
/// <c>await</c> того самого виконання задачі й не дістається сусідніх задач.
/// </remarks>
public static class JobCorrelation
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>Кореляція поточної задачі або <c>null</c>, якщо виклик поза задачею.</summary>
    public static string? Current => CurrentId.Value;

    /// <summary>Встановлює кореляцію на час виконання задачі.</summary>
    /// <param name="correlationId">Кореляція, та сама, що в scope журналу.</param>
    /// <returns>Звільнення повертає попереднє значення.</returns>
    public static IDisposable Begin(string correlationId)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = correlationId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentId.Value = previous;
    }
}
