using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Правило доступу до періоду — заміна кнопки <c>Protect</c> чинного рішення
/// (ФВ-2.15): який аркуш або таблиця в якому періоді доступні на введення.
/// </summary>
public sealed class PeriodAccessRuleDef : Entity<int>
{
    private PeriodAccessRuleDef() { }

    public PeriodAccessRuleDef(int templateVersionId, OutOfWindowBehavior onOutOfWindow)
    {
        TemplateVersionId = templateVersionId;
        OnOutOfWindow = onOutOfWindow;
    }

    public int TemplateVersionId { get; private set; }
    public int? SheetDefId { get; private set; }
    public int? TableDefId { get; private set; }

    /// <summary><c>null</c> — правило діє для всіх ролей.</summary>
    public int? RoleId { get; private set; }

    /// <summary>Від якого порядкового номера періоду доступно; <c>null</c> — без обмеження.</summary>
    public byte? FromSequence { get; private set; }

    public byte? ToSequence { get; private set; }
    public OutOfWindowBehavior OnOutOfWindow { get; private set; }

    /// <summary>Чи діє правило для періоду з таким порядковим номером.</summary>
    public bool AppliesTo(byte sequence)
        => (FromSequence is null || sequence >= FromSequence)
        && (ToSequence   is null || sequence <= ToSequence);
}
