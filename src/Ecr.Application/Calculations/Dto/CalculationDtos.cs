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
