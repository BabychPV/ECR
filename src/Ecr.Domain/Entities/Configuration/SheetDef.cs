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

    /// <summary>Логічне видалення: фізично запис лишається, бо на нього посилаються дані (ФВ-7.6).</summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }
}
