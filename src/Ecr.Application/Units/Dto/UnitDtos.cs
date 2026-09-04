using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Units.Dto;

/// <summary>Одиниця вимірювання.</summary>
/// <remarks>
/// <paramref name="DimensionCode"/> обов'язковий у відповіді: без розмірності
/// клієнт не може перевірити нічого. Саме вона визначає, що конверсія
/// можлива, — і що «маса ↔ об'єм» конверсією <b>не є</b> (ФВ-16.5,
/// <c>D-75</c>): це контекстний коефіцієнт, який живе в константах
/// методології, а не в <c>uom.Conversion</c>.
/// </remarks>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код одиниці.</param>
/// <param name="Symbol">Символ для UI.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="DimensionCode">Розмірність: маса, об'єм, час, енергія тощо.</param>
/// <param name="IsBase">Чи є базовою одиницею своєї розмірності.</param>
public sealed record UnitDto(
    int Id,
    string Code,
    string Symbol,
    LocalizedText NameL10n,
    string DimensionCode,
    bool IsBase);

/// <summary>Результат конверсії.</summary>
/// <remarks>
/// Повертається <b>і</b> застосований коефіцієнт: без нього неможливо пояснити
/// число в звіті, а «покажіть, звідки взялося» — щоденне питання до системи
/// звітності.
/// </remarks>
/// <param name="Value">Сконвертоване значення. <c>decimal</c>: <c>float</c> заборонений (<c>D-30</c>).</param>
/// <param name="FromUnit">Вихідна одиниця.</param>
/// <param name="ToUnit">Цільова одиниця.</param>
/// <param name="Factor">Застосований коефіцієнт.</param>
/// <param name="Offset">Застосований зсув; ненульовий лише для шкал на кшталт температури.</param>
public sealed record UnitConversionResultDto(
    decimal Value,
    string FromUnit,
    string ToUnit,
    decimal Factor,
    decimal Offset);
