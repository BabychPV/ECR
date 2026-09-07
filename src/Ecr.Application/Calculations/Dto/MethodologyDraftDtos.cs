// src/Ecr.Application/Calculations/Dto/MethodologyDraftDtos.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Версія методології в **конфігураторі** — на відміну від
/// <see cref="MethodologyVersionDto"/>, тут є і чернетки.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не додаткове поле в наявному. У
/// <see cref="MethodologyVersionDto"/> <c>EffectiveFrom</c> обов'язковий, бо
/// той перелік описує ЧИННІ версії й обчислює межу вікна з початку наступної.
/// Чернетка вікна не має взагалі — зробити поле нульовним означало б, що
/// перелік для розрахунку теж почав би допускати версію без дати, тобто
/// версію, для якої <c>VersionOn</c> не має відповіді (ФВ-13.3).
/// </remarks>
/// <param name="Id">Ідентифікатор версії.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Status">Чернетка, опублікована чи виведена з обігу.</param>
/// <param name="Level">Рівень драбини виразності.</param>
/// <param name="EffectiveFrom">Початок вікна дії; <c>null</c> — чернетка.</param>
/// <param name="NumericMode">Арифметика; переноситься в клон незмінною.</param>
/// <param name="CalendarMode">Джерело тривалості періоду.</param>
/// <param name="TraceLevel">Обсяг журналу.</param>
/// <param name="IsEditable">
/// Чи можна правити вміст. Відповідь дає СЕРВЕР, а не форма: клієнт, який
/// вирішує це сам за <c>status</c>, розійдеться з доменом на першому ж новому
/// стані версії.
/// </param>
/// <param name="CreatedByUserId">Автор; публікувати власну правку він не зможе (D-40).</param>
public sealed record MethodologyDraftVersionDto(
    int Id,
    string VersionNumber,
    TemplateVersionStatus Status,
    CalculationLevel Level,
    DateOnly? EffectiveFrom,
    NumericMode NumericMode,
    CalendarMode CalendarMode,
    TraceLevel TraceLevel,
    bool IsEditable,
    int CreatedByUserId);

/// <summary>Формула версії методології, як її бачить конфігуратор.</summary>
/// <remarks>
/// ⚠ <paramref name="EvaluationOrder"/> віддається, але не приймається:
/// порядок топологічний і рахується при публікації (ФВ-9.4). Показувати його
/// варто — він пояснює, чому формула бачить результат сусідньої; приймати
/// його від клієнта означало б дозволити людині зсунути обчислення так, що
/// помилка стане числом у звіті, а не помилкою публікації.
/// </remarks>
/// <param name="Id">Ідентифікатор формули.</param>
/// <param name="Code">Код — те, на що посилається <c>!Name</c>.</param>
/// <param name="Expression">Вираз діалекту методологій.</param>
/// <param name="ResultType">Число чи текст.</param>
/// <param name="OutputUnitId">Одиниця результату; <c>null</c> — безрозмірна або текст.</param>
/// <param name="EvaluationOrder">Позиція в топологічному порядку; нуль до публікації.</param>
public sealed record MethodologyFormulaDto(
    int Id,
    string Code,
    string Expression,
    FormulaResultType ResultType,
    int? OutputUnitId,
    int EvaluationOrder);
