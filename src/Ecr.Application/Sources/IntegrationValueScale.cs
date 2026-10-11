// src/Ecr.Application/Sources/IntegrationValueScale.cs
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Sources;

/// <summary>
/// Число від інтеграції (згортка, конверсія на межі, точка джерела) — до масштабу комірки
/// перед записом (Z2-01).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Число межі ОБЧИСЛЕНЕ, а не введене людиною: <c>Avg</c> трьох точок (<c>31/3</c>), середнє за часом
/// за місяць (÷ 2 678 400 с), інтеграл (÷ 3600), <c>Nm3 → Sm3</c> (множник з 18 знаками) дають нескінченний
/// або довгий дріб. Z1-01 зводить його до масштабу СХОВИЩА ще на межі (<see cref="BoundaryValue.Storable"/>),
/// але колонка з меншим <c>Scale</c> однаково відхиляла число (<c>ColumnDef.ValidateValue</c> п. 7), і
/// обробник відхиляв ВЕСЬ батч таблиці. Події джерел (<c>SourceEventRowBuilder</c>: точка й
/// <see cref="BoundaryUnitConversion.Convert"/>) до <see cref="BoundaryValue.Storable"/> не доходять узагалі.
/// Тому тут — останній пункт перед обробником, спільний для всіх шляхів запису інтеграції.
/// </para>
/// <para>
/// Правило — те саме, що вже діє для інших обчислених і зовнішніх чисел: формули округлюють до масштабу
/// сховища (<c>RecalculationService.AsStored</c>, C1-02) і методологія — до <c>Scale</c> колонки
/// (D-148), імпорт <c>.xlsx</c> — до сховища й до <c>Scale</c> колонки типу <see cref="CellDataType.Decimal"/>
/// (ФВ-9.16b, <c>ImportDiffBuilder.RoundToColumnScale</c>). Округлення «від нуля»
/// (<see cref="MidpointRounding.AwayFromZero"/>) — тим самим правилом, яким TVP <c>decimal(34,16)</c> пише
/// в <c>doc.CellValue</c>, тож результат гарантовано проходить обидві перевірки масштабу.
/// </para>
/// <para>
/// ⚠ Цілочислову колонку (<see cref="CellDataType.Int"/>) до цілого НЕ округлюємо — так само, як імпорт:
/// дробове число в ній — розбіжність конфігурації (згортка <c>Avg</c> у колонку <c>Int</c>), і відмова
/// обробника про неї видима, а мовчазне відкидання до ±0.5 — ні.
/// </para>
/// </remarks>
public static class IntegrationValueScale
{
    /// <summary>Значення для комірки: число — округлене до масштабу колонки, решта — як є.</summary>
    /// <param name="value">Значення так, як його прийме <see cref="CellValueReader.Read"/>.</param>
    /// <param name="column">Колонка-адресат.</param>
    /// <returns>Округлене число або те саме значення.</returns>
    public static object ToColumn(object value, ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return value is decimal number ? Round(number, column) : value;
    }

    /// <summary>Число, округлене до масштабу колонки (не більше масштабу сховища).</summary>
    /// <param name="number">Число межі.</param>
    /// <param name="column">Колонка-адресат.</param>
    /// <returns>Округлене число.</returns>
    public static decimal Round(decimal number, ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return Round(number, column.DataType, column.Scale);
    }

    /// <summary>Число, округлене до масштабу колонки за її типом і <c>Scale</c> (не більше масштабу сховища).</summary>
    /// <remarks>
    /// Те саме правило, що й <see cref="Round(decimal, ColumnDef)"/>: перегляд мапінгу (V8-06) не має колонки
    /// сутністю, лише її тип і масштаб, а число в ньому мусить бути тим, що ляже в комірку.
    /// </remarks>
    /// <param name="number">Число межі.</param>
    /// <param name="dataType">Тип колонки; <c>null</c> - невідомий (масштаб сховища).</param>
    /// <param name="columnScale"><c>Scale</c> колонки; <c>null</c> - не задано.</param>
    /// <returns>Округлене число.</returns>
    public static decimal Round(decimal number, CellDataType? dataType, byte? columnScale)
    {
        var scale = dataType == CellDataType.Decimal
                    && columnScale is { } declared
                    && declared < CellValueReader.StorageScale
            ? declared
            : CellValueReader.StorageScale;

        return decimal.Round(number, scale, MidpointRounding.AwayFromZero);
    }
}
