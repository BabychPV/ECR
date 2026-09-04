namespace Ecr.Domain.Entities.Units;

/// <summary>
/// Явна конверсія: виняток із маршруту через базову одиницю.
/// </summary>
/// <remarks>
/// ⛔ Контекстні коефіцієнти (щільність, теплотворність, молярна маса) сюди
/// **не потрапляють**: вони залежать від речовини й умов і змінюються з часом,
/// тому належать до <c>calc.MethodologyConstant</c>. На рівні БД це
/// забезпечує <c>CK_Conv_SameDimension</c> (ФВ-16.5).
/// </remarks>
public sealed class UnitConversion
{
    private UnitConversion() { }

    public UnitConversion(int fromUnitId, int toUnitId, decimal factor, decimal offset, byte kind, string? note)
    {
        FromUnitId = fromUnitId;
        ToUnitId = toUnitId;
        Factor = factor;
        Offset = offset;
        Kind = kind;
        Note = note;
    }

    public int Id { get; private set; }
    public int FromUnitId { get; private set; }
    public int ToUnitId { get; private set; }
    public decimal Factor { get; private set; }
    public decimal Offset { get; private set; }

    /// <summary>0 <c>Exact</c> — заміщає маршрут через базу; 1 <c>LegacyPinned</c> — заради сумісності чисел.</summary>
    public byte Kind { get; private set; }

    /// <summary>Обов'язковий для <c>LegacyPinned</c>: звідки взято коефіцієнт.</summary>
    public string? Note { get; private set; }
}
