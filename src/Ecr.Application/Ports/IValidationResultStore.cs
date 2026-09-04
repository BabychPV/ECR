namespace Ecr.Application.Ports;

/// <summary>Підсумок прогону валідації документа за період (ФВ-5.19).</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="RunAt">Момент прогону в UTC.</param>
/// <param name="ErrorCount">Помилки; **будь-яка** блокує подання.</param>
/// <param name="WarningCount">Попередження; подання не блокують.</param>
/// <param name="InfoCount">Інформаційні повідомлення.</param>
/// <param name="MessagesJson">Повний перелік повідомлень.</param>
public sealed record ValidationSummary(
    long DocumentId,
    int PeriodKey,
    DateTime RunAt,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    string MessagesJson);

/// <summary>Збереження підсумків валідації.</summary>
/// <remarks>
/// ⚠ Зберігається саме ПІДСУМОК, а не кожне повідомлення окремим рядком:
/// перевірок на великому документі тисячі, і таблиця «одне повідомлення —
/// один рядок» зростала б швидше за самі дані, а читалася б однаково цілком.
/// </remarks>
public interface IValidationResultStore
{
    /// <summary>Записує підсумок прогону.</summary>
    public Task SaveAsync(ValidationSummary summary, CancellationToken ct);

    /// <summary>Останній підсумок; <c>null</c> — валідацію ще не запускали.</summary>
    public Task<ValidationSummary?> GetLatestAsync(long documentId, int periodKey, CancellationToken ct);
}
