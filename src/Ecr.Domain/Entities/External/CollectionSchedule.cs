// src/Ecr.Domain/Entities/External/CollectionSchedule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Розклад збору для сутності джерела (<c>ext.CollectionSchedule</c>).
/// </summary>
/// <remarks>
/// ⚠ <see cref="Watermark"/> — **оптимізація, а не стан**. Його втрата не
/// коштує даних: збір із запасом <see cref="LookbackDays"/> перекриє проміжок
/// повторно, а ідемпотентність за природним ключем не дасть подвоїти точки
/// (ФВ-11.3). Тлумачити watermark як стан означало б, що збій кешу створює
/// дірку в даних.
/// </remarks>
public sealed class CollectionSchedule : Entity<int>
{
    /// <summary>Скільки днів перекривати назад за замовчуванням.</summary>
    private const int DefaultLookbackDays = 7;

    private CollectionSchedule() { }

    /// <summary>Створює розклад.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="cronExpression">Розклад у cron.</param>
    public CollectionSchedule(int sourceEntityId, string cronExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        SourceEntityId = sourceEntityId;
        CronExpression = cronExpression;
        LookbackDays = DefaultLookbackDays;
        IsEnabled = true;
    }

    public int SourceEntityId { get; private set; }
    public string CronExpression { get; private set; } = null!;

    /// <summary>Скільки днів перекривати назад — саме це закриває пропущені вікна.</summary>
    public int LookbackDays { get; private set; }

    public bool IsEnabled { get; private set; }
    public DateTime? LastRunAt { get; private set; }

    /// <summary>Оптимізація, не стан: втрата не коштує даних.</summary>
    public DateTime? Watermark { get; private set; }

    /// <summary>Ставить перекриття назад.</summary>
    /// <param name="days">Скільки днів; від'ємне не має сенсу.</param>
    public void SetLookback(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        LookbackDays = days;
    }

    /// <summary>Фіксує успішний прогін.</summary>
    /// <param name="utcNow">Момент прогону.</param>
    /// <param name="watermark">Нова межа зібраного.</param>
    public void MarkRun(DateTime utcNow, DateTime? watermark)
    {
        LastRunAt = utcNow;

        // ⚠ Watermark рухається лише ВПЕРЕД. Відкат назад від збійного прогону
        // змусив би наступний збирати те саме двічі — а на PI AF це години.
        if (watermark is { } mark && (Watermark is null || mark > Watermark))
        {
            Watermark = mark;
        }
    }

    /// <summary>Вимикає розклад; сутність лишається налаштованою.</summary>
    public void Disable() => IsEnabled = false;
}
