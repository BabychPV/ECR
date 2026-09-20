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

    /// <summary>Ширина стовпця <c>LastError</c>.</summary>
    public const int MaxLastErrorLength = 400;

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

    /// <summary>Чому розклад не поставлено в планувальник; <c>null</c> — поставлено.</summary>
    /// <remarks>
    /// ⚠ Це стан ПОСТАНОВКИ, не збору: відмови самого збору живуть у
    /// <c>itg.CollectionRun</c>. Без поля пропущений на старті розклад було
    /// видно лише в журналі застосунку — тобто не тому, хто правив cron.
    /// </remarks>
    public string? LastError { get; private set; }

    /// <summary>Коли постановка не вдалася.</summary>
    public DateTime? LastErrorAt { get; private set; }

    /// <summary>Версія рядка: два редактори розкладу не затирають один одного.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Фіксує, що розклад не вдалося поставити.</summary>
    /// <param name="error">Причина; обрізається до ширини стовпця.</param>
    /// <param name="utcNow">Момент відмови.</param>
    public void MarkInvalid(string error, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var text = error.Trim();
        LastError = text.Length <= MaxLastErrorLength ? text : text[..MaxLastErrorLength];
        LastErrorAt = utcNow;
    }

    /// <summary>Знімає помилку постановки: розклад поставлено (або знято) успішно.</summary>
    public void ClearError()
    {
        LastError = null;
        LastErrorAt = null;
    }

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

    /// <summary>Змінює cron розкладу.</summary>
    /// <param name="cronExpression">Новий вираз.</param>
    /// <remarks>
    /// ⚠ Формат домен НЕ перевіряє — як і конструктор: синтаксис cron належить
    /// планувальнику (інфраструктура). Перевіряє прикладний шар через
    /// <c>IBackgroundJobScheduler.IsValidCron</c> ДО виклику цього методу.
    /// </remarks>
    public void Reschedule(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        CronExpression = cronExpression.Trim();
    }

    /// <summary>Вмикає розклад.</summary>
    public void Enable() => IsEnabled = true;

    /// <summary>Вимикає розклад; сутність лишається налаштованою.</summary>
    public void Disable() => IsEnabled = false;
}
