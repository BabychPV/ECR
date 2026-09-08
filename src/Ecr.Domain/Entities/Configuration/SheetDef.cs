using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Аркуш шаблону — аналог аркуша Excel.</summary>
public sealed class SheetDef : Entity<int>
{
    private readonly List<TableDef> _tables = [];

    private SheetDef() { }

    public SheetDef(int templateVersionId, EcrCode code, LocalizedText name, int ordinal)
    {
        TemplateVersionId = templateVersionId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        IsVisible = true;
    }

    public int TemplateVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Порядок відображення. **Не ідентичність** — на нього не можна посилатися.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Група аркушів для правил складу документа (<c>SheetGroupRule</c>).</summary>
    public string? SheetGroup { get; private set; }

    public bool IsMandatory { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    public IReadOnlyList<TableDef> Tables => _tables;

    /// <summary>Змінює порядок — **презентаційна** операція, дозволена після публікації.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>
    /// Змінює назву аркуша.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>NameL10n</c> — поле презентаційного шару (<see
    /// cref="Services.ChangeClassifier.PresentationFields"/>), тому цей
    /// сеттер викликає і <c>PUT …/sheets/{code}</c> (структурна правка
    /// чернетки), і <c>PATCH …/presentation</c> (опублікована версія) —
    /// **той самий метод**, а не дві копії: інакше правило «як міняється
    /// назва» жило б у двох місцях і розійшлося б на першій же зміні одного
    /// з них.
    /// </remarks>
    public void Rename(LocalizedText name)
    {
        ArgumentNullException.ThrowIfNull(name);
        NameL10n = name;
    }

    /// <summary>Групує аркуш для правил складу документа (<c>SheetGroupRule</c>).</summary>
    /// <param name="group"><c>null</c> — аркуш поза групами.</param>
    public void SetGroup(string? group) => SheetGroup = group;

    /// <summary>Позначає аркуш обов'язковим для складу документа.</summary>
    public void SetMandatory(bool mandatory) => IsMandatory = mandatory;

    /// <summary>
    /// Видимість аркуша.
    /// </summary>
    /// <remarks>
    /// ⚠ Те саме поле, що й у <see cref="Services.ChangeClassifier.PresentationFields"/>
    /// (<c>SheetDef.IsVisible</c>): він презентаційний, тобто його дозволено
    /// міняти і в опублікованій версії через <c>PATCH …/presentation</c>, і
    /// тут — у чернетці разом із рештою полів аркуша через <c>PUT
    /// …/sheets/{code}</c>.
    /// </remarks>
    public void SetVisible(bool visible) => IsVisible = visible;

    /// <summary>Додає таблицю до аркуша.</summary>
    /// <exception cref="DomainException">Таблиця з таким кодом уже є на аркуші.</exception>
    public void AddTable(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (_tables.Any(t => string.Equals(t.Code, table.Code, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Таблиця з кодом {table.Code} на аркуші {Code} уже існує: код — це ідентичність, " +
                "на нього посилаються вирази.");
        }

        _tables.Add(table);
    }

    /// <summary>Логічне видалення: фізично запис лишається, бо на нього посилаються дані (ФВ-7.6).</summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }
}
