// src/Ecr.Domain/Entities/Calculations/CalculationResult.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Результат розрахунку. **Не потрапляє в `doc.CellValue`** (ФВ-9.12, D-69):
/// у документ він приходить посиланням через <c>cfg.CalculationBinding</c>.
/// </summary>
/// <remarks>
/// Тип — <c>decimal(34,16)</c>, ніколи <c>float</c> (ФВ-9.11): на мільйонах
/// рядків подвійна точність дає розбіжність, яку неможливо пояснити методологу.
/// </remarks>
public sealed class CalculationResult : Entity<long>
{
    private CalculationResult() { }

    /// <summary>Створює результат.</summary>
    /// <param name="runId">Прогін, що його порахував.</param>
    /// <param name="methodologyVersionId">Версія, що дала число.</param>
    /// <param name="periodKey">Період; він же ключ партиції.</param>
    /// <param name="documentId">Документ, до якого належить результат.</param>
    /// <param name="sourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
    /// <param name="outputCode">Код виходу методології.</param>
    /// <param name="value">Значення. <c>float</c> заборонений (D-30).</param>
    /// <param name="unitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
    /// <param name="kind">
    /// Вихід чи проміжне значення (<c>D-175</c>); типове — вихід, як до появи поля.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Вид поза переліком.</exception>
    public CalculationResult(
        long runId,
        int methodologyVersionId,
        int periodKey,
        long documentId,
        string? sourceRowKey,
        string outputCode,
        decimal value,
        int unitId,
        CalculationResultKind kind = CalculationResultKind.Output)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Невідомий вид результату.");
        }

        Kind = kind;
        CalculationRunId = runId;
        MethodologyVersionId = methodologyVersionId;
        PeriodKey = periodKey;
        DocumentId = documentId;
        SourceRowKey = sourceRowKey;
        OutputCode = outputCode;
        Value = value;
        UnitId = unitId;
    }

    public long CalculationRunId { get; private set; }

    /// <summary>Версія, що дала число. Без неї результат неможливо пояснити.</summary>
    public int MethodologyVersionId { get; private set; }

    public int PeriodKey { get; private set; }

    /// <summary>Документ результату.</summary>
    public long DocumentId { get; private set; }

    /// <summary>
    /// Рядок документа за КЛЮЧЕМ, а не за <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме <c>RowKey</c> (`calc`-частина `Q-027`): він переживає
    /// перестворення рядка і перенесення між періодами, а <c>TableRow.Id</c> —
    /// ні. Результат, що вказує на зниклий <c>Id</c>, неможливо ні пояснити,
    /// ні звірити.
    /// </remarks>
    public string? SourceRowKey { get; private set; }

    public string OutputCode { get; private set; } = null!;
    public decimal Value { get; private set; }
    public int UnitId { get; private set; }
    public long? SubstanceEntryId { get; private set; }

    /// <summary>
    /// Вихід методології чи значення видимої формули (<c>D-175</c>, V-6).
    /// </summary>
    /// <remarks>
    /// ⛔ Зрізи <c>rpt.*</c> беруть лише <see cref="CalculationResultKind.Output"/>:
    /// проміжне значення в зрізі змінило б його <c>ContentHash</c> (D-53). Для
    /// проміжного <see cref="OutputCode"/> — код формули, а не виходу.
    /// </remarks>
    public CalculationResultKind Kind { get; private set; }

    // ⚠ Прапорця IsCurrent тут НЕМАЄ, хоча ФВ-9.11 його називає. Він живе на
    // ПРОГОНІ (`calc.CalculationRun.Status`): інакше «перемикання актуального
    // прогону однією транзакцією» означало б оновити десятки мільйонів рядків,
    // і вимога перетворилася б на блокування партиції на хвилини. Результат
    // актуальний тоді, коли актуальний його прогін.
    /// <summary>Задає речовину результату.</summary>
    /// <param name="substanceEntryId">Запис довідника речовин; <c>null</c> — вихід без речовини.</param>
    public void SetSubstance(long? substanceEntryId) => SubstanceEntryId = substanceEntryId;
}
