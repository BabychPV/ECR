using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Правило складу документа: які групи аркушів обов'язкові разом або взаємовиключні.</summary>
public sealed class SheetGroupRule : Entity<int>
{
    private SheetGroupRule() { }

    public SheetGroupRule(int templateVersionId, string sheetGroup, byte ruleKind, string? targetGroup)
    {
        TemplateVersionId = templateVersionId;
        SheetGroup = sheetGroup;
        RuleKind = ruleKind;
        TargetGroup = targetGroup;
    }

    public int TemplateVersionId { get; private set; }
    public string SheetGroup { get; private set; } = null!;

    /// <summary>0 RequiresAll, 1 RequiresOne, 2 Excludes.</summary>
    public byte RuleKind { get; private set; }

    public string? TargetGroup { get; private set; }
}
