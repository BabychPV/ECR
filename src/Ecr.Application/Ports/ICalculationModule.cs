// src/Ecr.Application/Ports/ICalculationModule.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Модуль розрахунку емісій. <b>Окрема точка розширення від</b>
/// <see cref="IFormulaEngine"/>: не всі обчислення є формулами (ФВ-9.2).
/// </summary>
public interface ICalculationModule
{
    /// <summary>Код модуля, унікальний у системі.</summary>
    public string Code { get; }

    /// <summary>Рівень драбини виразності, який реалізує модуль.</summary>
    public CalculationLevel Level { get; }

    /// <summary>Чи здатний модуль обробити цю методологію.</summary>
    public bool CanHandle(MethodologyDescriptor methodology);

    /// <summary>Виконує розрахунок. Не пише в БД — повертає результат.</summary>
    public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Опис версії методології — те, за чим модуль вирішує, чи здатний він її
/// обробити, і за чим рушій знає, як саме рахувати.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Склад полів визначений двома
/// джерелами. По-перше, <c>GenericCalculationModule.CanHandle</c> у своєму
/// <c>TODO</c> вимагає <c>methodology.Level == Configuration</c> — отже
/// <see cref="Level"/> обов'язковий. По-друге, <c>MethodologyVersion</c>
/// (`05b`) називає три режими, кожен з яких <b>визначає числа</b>:
/// <c>NumericMode</c> (момент округлення, ФВ-9.9), <c>CalendarMode</c>
/// (тривалість періоду, ФВ-16.11) і <c>TraceLevel</c> (обсяг журналу, ФВ-9.13).
/// Модуль не може рахувати, не знаючи їх, і читати сутність сам він не має
/// права — тому вони тут.
/// Опис <b>не</b> містить формул, констант і речовин: їх модуль бере через
/// власні залежності, а descriptor лишається легким — його передають на
/// кожен рядок.
/// </remarks>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyVersionId">Версія — те, що реально рахує.</param>
/// <param name="Code">Код методології.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Level">Рівень драбини виразності.</param>
/// <param name="NumericMode">Арифметика; <c>Legacy</c> відтворює числа чинної системи.</param>
/// <param name="CalendarMode">Джерело тривалості періоду.</param>
/// <param name="TraceLevel">Скільки писати в <c>calc.CalculationStep</c>.</param>
public sealed record MethodologyDescriptor(
    int MethodologyId,
    int MethodologyVersionId,
    string Code,
    string VersionNumber,
    CalculationLevel Level,
    NumericMode NumericMode,
    CalendarMode CalendarMode,
    TraceLevel TraceLevel);

/// <summary>
/// Вхід розрахунку — <b>один рядок документа</b> з усіма аргументами.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Гранульованість «один рядок»
/// задана <c>CalculationInputBuilder.BuildAsync</c>: «для кожного рядка зібрати
/// CalculationInput» — і тим, що метод повертає
/// <c>IReadOnlyList&lt;CalculationInput&gt;</c> на набір <c>rowKeys</c>.
/// Склад <see cref="CalculationArgument"/> дослівно повторює колонки
/// <c>calc.CalculationInput</c> (`02a-db-schema.md` рядок 1158):
/// <c>ArgumentCode</c>, <c>Value</c>, <c>ValueString</c>, <c>UnitId</c>.
/// <c>DocumentId</c> і <c>SourceRowKey</c> — теж колонки тієї таблиці.
/// <see cref="TableInstanceId"/> і <see cref="PeriodKey"/> додано мною:
/// без них модуль не має календарного контексту, а <c>CalendarMode</c> без
/// періоду не працює (D-78).
/// </remarks>
/// <param name="Methodology">Версія методології, яку виконують.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableInstanceId">Таблиця документа, з якої взято рядок.</param>
/// <param name="PeriodKey">Період — потрібен для календарного контексту.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> для розрахунку рівня таблиці.</param>
/// <param name="Arguments">Аргументи в одиницях джерела.</param>
public sealed record CalculationInput(
    MethodologyDescriptor Methodology,
    long DocumentId,
    long TableInstanceId,
    PeriodKey PeriodKey,
    string? SourceRowKey,
    IReadOnlyList<CalculationArgument> Arguments);

/// <summary>Один аргумент розрахунку — рядок <c>calc.CalculationInput</c>.</summary>
/// <remarks>
/// Значення зберігається <b>в одиниці джерела</b>: конверсія на межі, а не в
/// сховищі, інакше повторний перерахунок з архіву дасть інший результат (ФВ-16.9).
/// </remarks>
/// <param name="ArgumentCode">Ім'я аргументу — те, на що посилається <c>@Arg</c>.</param>
/// <param name="Value">Числове значення; <c>null</c> — порожньо.</param>
/// <param name="ValueString">Текстове значення для нечислових аргументів.</param>
/// <param name="UnitId">Одиниця значення; <c>null</c> — безрозмірне.</param>
public sealed record CalculationArgument(
    string ArgumentCode,
    decimal? Value,
    string? ValueString,
    int? UnitId);

/// <summary>
/// Результат розрахунку одного рядка: <b>усі</b> виходи методології плюс трейс.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Контейнер, а не один рядок, бо
/// <c>GenericCalculationModule.ExecuteAsync</c> повертає <b>один</b>
/// <c>CalculationOutput</c> на вхід, а рахувати має «для КОЖНОЇ речовини
/// методології … виходи (tons, gsec)» — тобто кілька значень.
/// <see cref="CalculationOutputValue"/> лягає 1:1 на <c>calc.CalculationResult</c>
/// (`02a` рядок 1131), <see cref="CalculationTraceStep"/> — на
/// <c>calc.CalculationStep</c> (`02a` рядок 1179).
/// Модуль у БД не пише (D-69) — запис робить реалізація
/// <c>ICalculationResultStore</c>.
/// </remarks>
/// <param name="DocumentId">Документ.</param>
/// <param name="SourceRowKey">Рядок документа.</param>
/// <param name="Values">Обчислені виходи.</param>
/// <param name="Trace">Кроки трейсу; порожній список, якщо <c>TraceLevel = Off</c>.</param>
public sealed record CalculationOutput(
    long DocumentId,
    string? SourceRowKey,
    IReadOnlyList<CalculationOutputValue> Values,
    IReadOnlyList<CalculationTraceStep> Trace);

/// <summary>Один обчислений вихід — рядок <c>calc.CalculationResult</c>.</summary>
/// <param name="MethodologyVersionId">Версія, що дала число.</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> для виходів без речовини.</param>
/// <param name="OutputCode">Код виходу з <c>calc.MethodologyOutput</c>.</param>
/// <param name="Value">Значення. <c>float</c> заборонений (D-30).</param>
/// <param name="UnitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
public sealed record CalculationOutputValue(
    int MethodologyVersionId,
    int? SubstanceEntryId,
    string OutputCode,
    decimal Value,
    int UnitId);

/// <summary>Крок трейсу — рядок <c>calc.CalculationStep</c>.</summary>
/// <remarks>
/// Обсяг трейсу керується <c>TraceLevel</c> версії: керуємо тим, <b>що</b>
/// пишемо, а не скільки зберігаємо (ЗБР-3).
/// </remarks>
/// <param name="StepOrder">Порядок кроку.</param>
/// <param name="StepCode">Код кроку — зазвичай код формули або виходу.</param>
/// <param name="Expression">Вираз як його бачив рушій.</param>
/// <param name="Value">Значення кроку.</param>
/// <param name="TraceJson">Довільна деталізація: підставлені аргументи, константи.</param>
public sealed record CalculationTraceStep(
    int StepOrder,
    string StepCode,
    string? Expression,
    decimal? Value,
    string? TraceJson);
