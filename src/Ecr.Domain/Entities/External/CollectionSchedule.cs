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

    /// <summary>Ширина стовпця <c>CronExpression</c>.</summary>
    /// <remarks>
    /// ⚠ Межу перевіряє прикладний шар ДО бази: без неї довший вираз із
    /// інтерфейсу доїжджав би до <c>SaveChanges</c> і повертався б
    /// <c>500 SqlException</c> «String or binary data would be truncated» —
    /// тобто про описку в cron користувач дізнавався б як про аварію сервера.
    /// </remarks>
    public const int MaxCronLength = 100;

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

    /// <summary>
    /// Розклад, після успішного прогону якого цей запускається (ФВ-13.15 «залежності»);
    /// <c>null</c> — залежності немає.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише розклад ТОГО Ж з'єднання, без циклів — це перевіряє прикладний шар ДО
    /// бази. Зовнішній ключ без каскаду (самопосилання): прибираючи розклад,
    /// <c>DeleteCollectionScheduleHandler</c> спершу знімає залежність у тих, хто на
    /// нього посилався.
    /// </remarks>
    public int? DependsOnScheduleId { get; private set; }

    /// <summary>Скільки годин (48) залежний розклад вважається живим: старший прогін не блокує.</summary>
    /// <remarks>
    /// ⚠ Судження, не факт із ТЗ. Залежність, що давно не бігала (вимкнена, зламана,
    /// тижнева), не має зупиняти збір назавжди: наздоганяння закриє затримку, а вічна
    /// відмова з'їла б дані мовчки.
    /// </remarks>
    public static readonly TimeSpan MaxDependencyStaleness = TimeSpan.FromHours(48);

    /// <summary>Ставить або знімає залежність.</summary>
    /// <param name="dependsOnScheduleId">Розклад-залежність; <c>null</c> — зняти.</param>
    /// <exception cref="ArgumentException">Розклад залежить сам від себе.</exception>
    public void SetDependency(int? dependsOnScheduleId)
    {
        if (dependsOnScheduleId is { } dep && Id != 0 && dep == Id)
        {
            throw new ArgumentException("Розклад не може залежати сам від себе.", nameof(dependsOnScheduleId));
        }

        DependsOnScheduleId = dependsOnScheduleId;
    }

    /// <summary>Чи можна запускати збір зараз з огляду на залежність (ФВ-13.15).</summary>
    /// <param name="dependency">Розклад-залежність; <c>null</c> — немає або вже видалений.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <returns><c>true</c> — запускати; <c>false</c> — пропустити цей запуск (наступний за cron перевірить знову).</returns>
    /// <remarks>
    /// Не блокує вічно: запускається завжди, якщо залежності немає, вона вимкнена, ще не
    /// бігала, не бігала понад <see cref="MaxDependencyStaleness"/>, або цей розклад
    /// ще жодного разу не бігав. Інакше потрібен прогін залежності НЕ раніший за наш
    /// останній — «спершу залежний, потім цей».
    /// </remarks>
    public bool IsDependencyMet(CollectionSchedule? dependency, DateTime utcNow)
    {
        if (DependsOnScheduleId is null || dependency is null || !dependency.IsEnabled)
        {
            return true;
        }

        if (LastRunAt is not { } own || dependency.LastRunAt is not { } theirs)
        {
            return true;
        }

        return theirs >= own || utcNow - theirs > MaxDependencyStaleness;
    }

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

    /// <summary>Найвужче вікно перекриття назад, днів (ФВ-13.15).</summary>
    /// <remarks>
    /// ⚠ Нуль — не «без перекриття», а порожнє вікно: <c>CollectionJob</c> збирає
    /// <c>[to − LookbackDays; to]</c>, і за нуля збір не бере жодної точки.
    /// </remarks>
    public const int MinLookbackDays = 1;

    /// <summary>Найширше вікно перекриття назад, днів (ФВ-13.15).</summary>
    /// <remarks>
    /// ⚠ Рік із запасом на високосний. Межі в ТЗ немає; ширше вікно означає
    /// щоразу перечитувати з PI AF понад рік точок заради того, що наздоганяння
    /// (<c>CatchUpLookback</c>, 45 днів) і так закриває.
    /// </remarks>
    public const int MaxLookbackDays = 366;

    /// <summary>Ставить перекриття назад.</summary>
    /// <param name="days">Скільки днів: від <see cref="MinLookbackDays"/> до <see cref="MaxLookbackDays"/>.</param>
    public void SetLookback(int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, MinLookbackDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, MaxLookbackDays);
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
