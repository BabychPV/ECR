using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations.Dto;

/// <summary>Методологія у списку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код методології.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Group">Група для UI.</param>
/// <param name="Versions">Версії з їхніми вікнами дії.</param>
public sealed record MethodologyDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    string? Group,
    IReadOnlyList<MethodologyVersionDto> Versions);

/// <summary>
/// Версія методології.
/// </summary>
/// <remarks>
/// Три режими нижче <b>визначають числа</b>, і жоден не є технічною дрібницею:
/// <paramref name="NumericMode"/> — момент округлення (ФВ-9.9),
/// <paramref name="CalendarMode"/> — тривалість періоду (ФВ-16.11),
/// <paramref name="TraceLevel"/> — обсяг журналу (ФВ-9.13). Перші два
/// обов'язкові в diff при публікації: їхня зміна тихо змінює всі результати.
/// Тому вони є у відповіді, а не ховаються в конфігураторі.
///
/// Три осі версійності не змішуються (ФВ-13.2): версія визначення
/// (<paramref name="VersionNumber"/>), вікно дії
/// (<paramref name="EffectiveFrom"/>…<paramref name="EffectiveTo"/>) і версія
/// даних — остання живе в довідниках, а не тут.
/// </remarks>
/// <param name="Id">Ідентифікатор версії.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Status">Чернетка, опублікована чи виведена з обігу.</param>
/// <param name="Level">Рівень драбини виразності.</param>
/// <param name="EffectiveFrom">Початок вікна дії.</param>
/// <param name="EffectiveTo">Кінець вікна дії; <c>null</c> — без обмеження.</param>
/// <param name="NumericMode">Арифметика; <c>Legacy</c> відтворює числа чинної системи.</param>
/// <param name="CalendarMode">Джерело тривалості періоду.</param>
/// <param name="TraceLevel">Скільки писати в <c>calc.CalculationStep</c>.</param>
public sealed record MethodologyVersionDto(
    int Id,
    string VersionNumber,
    TemplateVersionStatus Status,
    CalculationLevel Level,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    NumericMode NumericMode,
    CalendarMode CalendarMode,
    TraceLevel TraceLevel);

/// <summary>Стан прогону розрахунку.</summary>
/// <remarks>
/// <paramref name="ModulesProfileJson"/> — не діагностика «на майбутнє»:
/// бюджет річного перерахунку 10 хвилин (ПРД-13) тримається на тому, щоб
/// знати, які модулі дають основний час.
/// </remarks>
/// <param name="Id">Ідентифікатор прогону.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період; <c>null</c> — повний рік.</param>
/// <param name="Status">Стан прогону.</param>
/// <param name="StartedAt">Початок.</param>
/// <param name="FinishedAt">Кінець; <c>null</c> — ще виконується.</param>
/// <param name="TriggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
/// <param name="ResultCount">Скільки результатів записано.</param>
/// <param name="ModulesProfileJson">Профіль часу по модулях.</param>
/// <param name="UnmatchedRows">
/// Рядки без правила (D14, інформація, не помилка); <c>null</c> — таких немає або профіль їх не містить.
/// </param>
public sealed record CalculationRunDto(
    long Id,
    int ProjectId,
    int? PeriodKey,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int? TriggeredByUserId,
    int ResultCount,
    string? ModulesProfileJson,
    UnmatchedRowsDto? UnmatchedRows = null);

/// <summary>Рядки документа, яким не підійшло жодне правило розрахунку (D14).</summary>
/// <remarks>
/// ⚠ Це ІНФОРМАЦІЯ: у Land рядок зі статусом «не Running» виключається навмисно й без помилки.
/// Значень комірок тут немає навмисно — лише кількість і ключі рядків.
/// </remarks>
/// <param name="Count">Скільки рядків документа не закрило жодне правило.</param>
/// <param name="Rows">До 10 рядків за зростанням таблиці й номера.</param>
public sealed record UnmatchedRowsDto(int Count, IReadOnlyList<UnmatchedRowItemDto> Rows);

/// <summary>Один рядок без правила (D14).</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowNumber">Номер рядка, з 1, у порядку створення.</param>
/// <param name="RowKey">Публічний ключ рядка.</param>
public sealed record UnmatchedRowItemDto(long TableInstanceId, int RowNumber, string RowKey);

/// <summary>Стан актуального прогону документа за період для аркуша (D14).</summary>
/// <param name="RunId">Актуальний прогін; <c>null</c> — документ за цей період не рахували.</param>
/// <param name="FinishedAt">Коли прогін завершився (UTC).</param>
/// <param name="UnmatchedRows">Рядки без правила; <c>null</c> — таких немає, а також для читача зі звуженою видимістю.</param>
public sealed record CalculationRunStatusDto(long? RunId, DateTime? FinishedAt, UnmatchedRowsDto? UnmatchedRows);

/// <summary>Поточні проблеми розрахунку документа за період (AN-69).</summary>
/// <param name="Items">Проблеми видимих читачеві таблиць; порожньо — проблем немає.</param>
/// <param name="Tables">
/// Що приймають правила методологій кожної таблиці з проблемами (UAT 08.11 b3): колонки, які згадують правила, і
/// допустимі комбінації значень. Таблиці немає — підказки немає (правил з колонками цієї таблиці немає, чинної версії
/// немає або хоч одна колонка прихована від читача).
/// </param>
public sealed record CalculationIssuesDto(
    IReadOnlyList<CalculationIssueDto> Items,
    IReadOnlyList<CalculationIssueTableHintDto>? Tables = null);

/// <summary>Що приймають правила методологій таблиці (UAT 08.11 b3).</summary>
/// <param name="TableCode">Код таблиці шаблону.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="Methodologies">Методології, чиї правила дали варіанти: код і назва (людині показується назва).</param>
/// <param name="Columns">Колонки, які згадують правила, у порядку колонок таблиці.</param>
/// <param name="Options">
/// Допустимі комбінації: кожен елемент — значення в порядку <paramref name="Columns"/>; порожній рядок у позиції —
/// правило цю колонку не обмежує. Без повторів, за зростанням.
/// </param>
public sealed record CalculationIssueTableHintDto(
    string TableCode,
    long? SheetCopyId,
    IReadOnlyList<CalculationIssueMethodologyDto> Methodologies,
    IReadOnlyList<CalculationIssueColumnDto> Columns,
    IReadOnlyList<IReadOnlyList<string>> Options);

/// <summary>Методологія підказки таблиці.</summary>
/// <param name="Code">Код методології.</param>
/// <param name="NameL10n">Назва мовами каталогу; <c>null</c> — невідома (показується код).</param>
public sealed record CalculationIssueMethodologyDto(string Code, LocalizedText? NameL10n);

/// <summary>Колонка, яку згадують правила методології.</summary>
/// <param name="Code">Код колонки.</param>
/// <param name="NameL10n">Заголовок колонки мовами каталогу.</param>
public sealed record CalculationIssueColumnDto(string Code, LocalizedText NameL10n);

/// <summary>Одна поточна проблема розрахунку: рядок, який правило методології не охопило (AN-69).</summary>
/// <remarks>
/// Значення комірок — лише в колонках, які згадують правила (<see cref="CalculationIssuesDto.Tables"/>), і лише коли
/// читач бачить ці колонки (UAT 08.11 b3).
/// </remarks>
/// <param name="TableCode">Код таблиці шаблону.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="RowNumber">Номер рядка, з 1, у порядку створення.</param>
/// <param name="RowKey">Публічний ключ рядка.</param>
/// <param name="Kind">Вид проблеми; нині <c>NoMatchingRule</c>.</param>
/// <param name="MethodologyCode">Методологія прив'язки таблиці; <c>null</c> — невідома.</param>
/// <param name="DetectedAt">Коли виявлено (UTC).</param>
/// <param name="CurrentValues">
/// Значення рядка в колонках підказки таблиці (<c>tables[].columns</c>, той самий порядок);
/// <c>null</c> — підказки для таблиці немає; порожній рядок у позиції — комірка порожня.
/// </param>
public sealed record CalculationIssueDto(
    string TableCode,
    long? SheetCopyId,
    int RowNumber,
    string RowKey,
    string Kind,
    string? MethodologyCode,
    DateTime DetectedAt,
    IReadOnlyList<string>? CurrentValues = null);
