using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Units;

/// <summary>
/// Розмірність величини. Конверсія можлива **лише в межах однієї
/// розмірності** — це те, що не дає щільності стати «конверсією» (ФВ-16.5).
/// </summary>
public sealed class Dimension
{
    private Dimension() { }

    public Dimension(byte id, EcrCode code, LocalizedText name)
    {
        Id = id;
        Code = code.Value;
        NameL10n = name;
    }

    public byte Id { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Канонічна одиниця розмірності.</summary>
    public int? BaseUnitId { get; private set; }

    /// <summary>Похідна = відношення двох розмірностей (<c>MassFlow = Mass / Time</c>).</summary>
    public bool IsDerived { get; private set; }

    public byte? NumeratorDimensionId { get; private set; }
    public byte? DenominatorDimensionId { get; private set; }
}
