// src/Ecr.Domain/Entities/External/CollectionSchedule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Розклад збору — **на сутність**, не один на систему (ФВ-13.15).
/// </summary>
/// <remarks>
/// Стосується **лише** адаптера до PI AF (D-106): дані з наших веб-форм не
/// опитуються взагалі — вони приходять записом і одразу запускають
/// інкрементний перерахунок. Заводити розклад для власних форм означало б
/// опитувати власну базу про те, що ми самі щойно в неї поклали.
/// <para>
/// <see cref="Watermark"/> — **оптимізація, не стан**: втрата позначки має
/// призводити до повторного збору інтервалу, а не до його пропуску. Збір
/// ідемпотентний (ФВ-11.3), тому повтор безпечний, а пропуск — ні.
/// </para>
/// </remarks>
public sealed class CollectionSchedule : Entity<int>
{
    private CollectionSchedule() { }

    public CollectionSchedule(int sourceEntityId, string cronExpression)
    {
        SourceEntityId = sourceEntityId;
        CronExpression = cronExpression;
        IsActive = true;
    }

    public int SourceEntityId { get; private set; }
    public string CronExpression { get; private set; } = null!;

    /// <summary>Вікно збору в хвилинах: скільки назад від моменту запуску.</summary>
    public int LookbackMinutes { get; private set; }

    /// <summary>Позначка останнього успішного збору. Оптимізація, не стан.</summary>
    public DateTime? Watermark { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Просуває позначку після успішного збору інтервалу.</summary>
    public void AdvanceWatermark(DateTime to)
        => throw new NotImplementedException(
            "TODO: рухати позначку лише ВПЕРЕД і лише після підтвердженого " +
            "запису інтервалу. Відкат назад дозволений явною адміністративною " +
            "дією — це штатний спосіб перезібрати період (catch-up, ФВ-11.3).");
}
