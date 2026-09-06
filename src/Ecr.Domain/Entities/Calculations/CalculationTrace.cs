// src/Ecr.Domain/Entities/Calculations/CalculationTrace.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Вхід розрахунку — один аргумент одного рядка (<c>calc.CalculationInput</c>).
/// </summary>
/// <remarks>
/// ⚠ Входи зберігаються **разом із результатами** і є частиною доказової бази
/// відтворюваності (B-4): без них неможливо відповісти на питання «з яких
/// чисел вийшло це число», і перерахунок через рік дав би інший результат без
/// жодного способу з'ясувати, чому.
/// <para>
/// Значення лежить <b>в одиниці джерела</b> (ФВ-16.10): конверсія робиться на
/// межі й журналюється, інакше повторний перерахунок з архіву дасть інше число
/// (D-79).
/// </para>
/// </remarks>
public sealed class CalculationInputRow : Entity<long>
{
    private CalculationInputRow() { }

    /// <summary>Створює запис входу.</summary>
    /// <param name="runId">Прогін.</param>
    /// <param name="periodKey">Період; він же ключ партиції.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
    /// <param name="argumentCode">Ім'я аргументу — те, на що посилається <c>@Arg</c>.</param>
    public CalculationInputRow(
        long runId, int periodKey, long documentId, string? sourceRowKey, string argumentCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentCode);

        CalculationRunId = runId;
        PeriodKey = periodKey;
        DocumentId = documentId;
        SourceRowKey = sourceRowKey;
        ArgumentCode = argumentCode;
    }

    public int PeriodKey { get; private set; }
    public long CalculationRunId { get; private set; }
    public long DocumentId { get; private set; }
    public string? SourceRowKey { get; private set; }
    public string ArgumentCode { get; private set; } = null!;

    /// <summary>Числове значення; <c>null</c> — аргумент нечисловий або порожній.</summary>
    public decimal? Value { get; private set; }

    /// <summary>Текстове значення для нечислових аргументів.</summary>
    public string? ValueString { get; private set; }

    /// <summary>Одиниця ДЖЕРЕЛА, а не цільова (ФВ-16.10).</summary>
    public int? UnitId { get; private set; }

    /// <summary>Записує значення аргументу.</summary>
    /// <param name="value">Числове значення.</param>
    /// <param name="valueString">Текстове значення.</param>
    /// <param name="unitId">Одиниця джерела.</param>
    public void SetValue(decimal? value, string? valueString, int? unitId)
    {
        Value = value;
        ValueString = valueString;
        UnitId = unitId;
    }
}

/// <summary>
/// Крок трейсу розрахунку (<c>calc.CalculationStep</c>).
/// </summary>
/// <remarks>
/// Обсяг керується <c>MethodologyVersion.TraceLevel</c>: керуємо тим, <b>що</b>
/// пишемо, а не скільки зберігаємо (ЗБР-3). Політика зберігання — «нічого не
/// затирається» (ЗБР-1), тому повний трейс кожного кроку кожної методології
/// кожного періоду перевищив би обсяг самих даних.
/// </remarks>
public sealed class CalculationStep : Entity<long>
{
    private CalculationStep() { }

    /// <summary>Створює крок трейсу.</summary>
    /// <param name="runId">Прогін.</param>
    /// <param name="periodKey">Період; він же ключ партиції.</param>
    /// <param name="stepOrder">Порядок кроку в обчисленні.</param>
    /// <param name="stepCode">Код кроку — зазвичай код формули або виходу.</param>
    public CalculationStep(long runId, int periodKey, int stepOrder, string stepCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepCode);

        CalculationRunId = runId;
        PeriodKey = periodKey;
        StepOrder = stepOrder;
        StepCode = stepCode;
    }

    public int PeriodKey { get; private set; }
    public long CalculationRunId { get; private set; }

    /// <summary>Результат, до якого належить крок; <c>null</c> — проміжний.</summary>
    public long? ResultId { get; private set; }

    public int StepOrder { get; private set; }
    public string StepCode { get; private set; } = null!;

    /// <summary>Вираз як його бачив рушій.</summary>
    public string? Expression { get; private set; }

    /// <summary>Значення кроку; <c>null</c> — крок завершився помилкою.</summary>
    public decimal? Value { get; private set; }

    /// <summary>Деталізація: підставлені аргументи, константи, код помилки.</summary>
    public string? TraceJson { get; private set; }

    /// <summary>
    /// Чому значення стало нулем (<c>H-24d-1</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ ОКРЕМА колонка, а не поле в <see cref="TraceJson"/>, і це весь
    /// зміст кроку. Чинна система маскує <c>NaN</c> і <c>±∞</c> у нуль
    /// мовчки, і цінність цього запису — у тому, що такі випадки можна
    /// **перелічити**. Шукати їх у JSON по мільйонах рядків трейсу означало б
    /// повне сканування щоразу — тобто звіт, який ніхто не буде будувати.
    ///
    /// ⚠ Типове значення — <see cref="Enums.MaskedZeroReason.None"/>, тож наявні рядки
    /// при міграції кажуть правду: маскування до цього кроку не траплялося
    /// ніколи — режим <c>Legacy</c> рахував у <c>decimal</c>, де <c>NaN</c> не буває.
    /// </remarks>
    public Enums.MaskedZeroReason Masked { get; private set; }

    /// <summary>Записує деталі кроку.</summary>
    /// <param name="expression">Вираз.</param>
    /// <param name="value">Значення.</param>
    /// <param name="traceJson">Деталізація.</param>
    /// <param name="resultId">Результат, до якого належить крок.</param>
    /// <param name="masked">Причина маскування в нуль (<c>H-24d-1</c>).</param>
    public void Describe(
        string? expression,
        decimal? value,
        string? traceJson,
        long? resultId,
        Enums.MaskedZeroReason masked = Enums.MaskedZeroReason.None)
    {
        Expression = expression;
        Value = value;
        TraceJson = traceJson;
        ResultId = resultId;
        Masked = masked;
    }
}
