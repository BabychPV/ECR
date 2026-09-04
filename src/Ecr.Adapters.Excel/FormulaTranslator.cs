namespace Ecr.Adapters.Excel;

/// <summary>
/// Транслює наші вирази в синтаксис Excel і назад.
/// </summary>
/// <remarks>
/// Це найпідступніша частина експорту: наша мова адресує рядки за
/// <c>RowKey</c>, Excel — за координатами. Тому потрібен **зворотний мапер
/// координат**, і будувати його треба на етапі експорту, а не «якось потім»
/// (ТЗ §13.5 п.6 — окрема пастка, яку легко недооцінити).
/// </remarks>
public sealed class FormulaTranslator
{
    /// <summary>Наш вираз → формула Excel.</summary>
    /// <param name="expression">Вираз у нашій граматиці.</param>
    /// <param name="coordinates">Мапа <c>(TableDefId, RowKey, ColumnDefId)</c> → адреса комірки Excel.</param>
    public string ToExcel(string expression, IReadOnlyDictionary<(int, string, int), string> coordinates)
        => throw new NotImplementedException(
            "TODO: розібрати вираз нашим парсером; замінити посилання на координати з мапи; " +
            "функції транслювати 1:1 (усі 11 мають Excel-відповідники); " +
            "CONVERT замінити множенням на константу — в Excel такої функції немає, " +
            "і саме тут експорт «з формулами» втрачає частину семантики: це треба сказати " +
            "користувачеві, а не приховати.");

    /// <summary>Формула Excel → наш вираз (для імпорту структури з довільного файлу).</summary>
    public string? FromExcel(string excelFormula, IReadOnlyDictionary<string, (int, string, int)> reverseCoordinates)
        => throw new NotImplementedException(
            "TODO: зворотна трансляція best-effort. Нерозпізнану формулу НЕ вгадувати: " +
            "повернути null і показати користувачеві — інакше імпорт мовчки створить " +
            "неправильне правило обчислення.");
}
