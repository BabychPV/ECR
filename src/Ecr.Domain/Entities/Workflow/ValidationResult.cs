using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Результат прогону валідації документа за період (ФВ-5.19).
/// </summary>
/// <remarks>
/// Зберігається зведення, а не кожне повідомлення окремим рядком: перевірок
/// на великому документі тисячі, і таблиця «одне повідомлення — один рядок»
/// зростала б швидше за самі дані, а читалася б однаково цілком.
/// </remarks>
public sealed class ValidationResult : Entity<long>
{
    private ValidationResult() { }

    /// <summary>Фіксує результат прогону.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="runAt">Момент прогону в UTC.</param>
    /// <param name="errorCount">Кількість помилок.</param>
    /// <param name="warningCount">Кількість попереджень.</param>
    /// <param name="infoCount">Кількість інформаційних повідомлень.</param>
    /// <param name="messagesJson">Повідомлення в JSON.</param>
    public ValidationResult(
        long documentId,
        int periodKey,
        DateTime runAt,
        int errorCount,
        int warningCount,
        int infoCount,
        string messagesJson)
    {
        DocumentId = documentId;
        PeriodKey = periodKey;
        RunAt = runAt;
        ErrorCount = errorCount;
        WarningCount = warningCount;
        InfoCount = infoCount;
        MessagesJson = messagesJson;
    }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Період.</summary>
    public int PeriodKey { get; private set; }

    /// <summary>Момент прогону в UTC.</summary>
    public DateTime RunAt { get; private set; }

    /// <summary>Помилки; **будь-яка** з них блокує подання (ФВ-5.19).</summary>
    public int ErrorCount { get; private set; }

    /// <summary>Попередження; подання не блокують.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Інформаційні повідомлення.</summary>
    public int InfoCount { get; private set; }

    /// <summary>Повідомлення прогону в JSON.</summary>
    public string MessagesJson { get; private set; } = null!;
}
