// src/Ecr.Domain/Entities/Calculations/Methodology.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Методологія розрахунку — **контейнер версій**, а не сама формула
/// (ФВ-9.1). Обчислює завжди конкретна версія, обрана за датою періоду.
/// </summary>
public sealed class Methodology : Entity<int>
{
    private readonly List<MethodologyVersion> _versions = [];

    private Methodology() { }

    public Methodology(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public IReadOnlyList<MethodologyVersion> Versions => _versions;

    /// <summary>Версія, чинна на дату звітного періоду (ФВ-9.3).</summary>
    public MethodologyVersion? VersionOn(DateOnly periodDate)
        => throw new NotImplementedException(
            "TODO: обрати опубліковану версію, чиє вікно дії містить дату. " +
            "Вікна опублікованих версій НЕ перетинаються (ФВ-13.3), тому " +
            "збігів бути не може; якщо їх два — це помилка даних, а не привід " +
            "узяти першу.");
}
