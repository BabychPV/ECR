using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Іммутабельний зріз поданих даних (ФВ-5.7, ФВ-9.4).
/// </summary>
/// <remarks>
/// ⚠ Зріз фіксує не лише значення, а й **версії**, за якими їх рахували:
/// шаблон, методології, режими чисел і календаря. Без них «перерахувати як
/// тоді» неможливо, і поданий звіт стає незвіряним.
///
/// ⛔ Зрізи **накопичуються**, а не перезаписуються: після повторного подання
/// старий лишається назавжди і не перераховується ніколи (ФВ-9.17). Тому в
/// класі немає жодного методу зміни — тільки конструктор.
///
/// ⚠ `NumericMode`/`CalendarMode` — `byte?`, а не `byte` (Q-155): обробник
/// подання (`SubmitSheetHandler`) не має відкіля взяти чинний режим на
/// момент виклику, і запис підставного значення («щоб не порожньо») там,
/// де насправді нічого не відомо, — це фальсифікація факту, а не
/// оформлення. `NULL` тут видно й перевіряється; підставне число виглядало
/// б як зафіксований вибір (той самий урок, що й `TemplateVersionId: 0` у
/// `W8`, тільки виявлений до того, як хтось на це значення поклався).
/// </remarks>
public sealed class SubmissionSnapshot : Entity<long>
{
    private SubmissionSnapshot() { }

    /// <summary>Створює зріз.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="templateVersionId">Версія шаблону на момент подання.</param>
    /// <param name="methodologyVersionsJson">Версії методологій.</param>
    /// <param name="numericMode">
    /// Режим чисел (ФВ-9.9); <c>null</c>, якщо обробник подання не провів
    /// чинний режим на момент виклику (Q-155).
    /// </param>
    /// <param name="calendarMode">
    /// Календарна конвенція (D-78); <c>null</c> з тієї самої причини, що й
    /// <paramref name="numericMode"/> (Q-155).
    /// </param>
    /// <param name="payloadJson">Значення комірок.</param>
    /// <param name="contentHash">Контрольна сума вмісту.</param>
    /// <param name="submittedAt">Момент подання.</param>
    /// <param name="submittedByUserId">Хто подав.</param>
    public SubmissionSnapshot(
        long documentId,
        int sheetDefId,
        int periodKey,
        int templateVersionId,
        string methodologyVersionsJson,
        byte? numericMode,
        byte? calendarMode,
        string payloadJson,
        byte[] contentHash,
        DateTime submittedAt,
        int submittedByUserId)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        TemplateVersionId = templateVersionId;
        MethodologyVersionsJson = methodologyVersionsJson;
        NumericMode = numericMode;
        CalendarMode = calendarMode;
        PayloadJson = payloadJson;
        ContentHash = contentHash;
        SubmittedAt = submittedAt;
        SubmittedByUserId = submittedByUserId;
    }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Аркуш; подання має гранулярність «аркуш × період» (D-38).</summary>
    public int SheetDefId { get; private set; }

    /// <summary>Період.</summary>
    public int PeriodKey { get; private set; }

    /// <summary>Версія шаблону на момент подання.</summary>
    public int TemplateVersionId { get; private set; }

    /// <summary>Версії методологій у JSON.</summary>
    public string MethodologyVersionsJson { get; private set; } = null!;

    /// <summary>
    /// Режим чисел (ФВ-9.9). <c>null</c> означає, що на момент подання чинний
    /// режим не був відомий обробнику (Q-155) — це навмисна порожнеча, а не
    /// втрачене значення: фіктивне число тут виглядало б як зафіксований
    /// вибір і приховало б, що рішення насправді не приймалося.
    /// </summary>
    public byte? NumericMode { get; private set; }

    /// <summary>
    /// Календарна конвенція (D-78). <c>null</c> з тієї самої причини, що й
    /// <see cref="NumericMode"/> (Q-155).
    /// </summary>
    public byte? CalendarMode { get; private set; }

    /// <summary>Значення комірок у JSON.</summary>
    public string PayloadJson { get; private set; } = null!;

    /// <summary>SHA-256 вмісту: за ним видно, що зріз не підмінили.</summary>
    public byte[] ContentHash { get; private set; } = null!;

    /// <summary>Момент подання.</summary>
    public DateTime SubmittedAt { get; private set; }

    /// <summary>Хто подав.</summary>
    public int SubmittedByUserId { get; private set; }
}
