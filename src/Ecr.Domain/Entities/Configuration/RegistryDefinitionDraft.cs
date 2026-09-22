using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Чернетка опису довідника (<c>cfg.RegistryDefinitionDraft</c>, <c>BE-24</c> крок 2):
/// повний стан полів і правил, який ще НЕ застосовано до <see cref="RegistryDef"/>.
/// </summary>
/// <remarks>
/// ⛔ Окрема таблиця, а не прапорець на описі: читання довідника, записи й
/// документи бачать лише опублікований опис, і чернетка не може до них
/// «протекти» — у них просто немає шляху до цього рядка. Одна чернетка на
/// довідник (ключ — <see cref="RegistryDefId"/>).
/// </remarks>
public sealed class RegistryDefinitionDraft
{
    /// <summary>Найбільша довжина причини; та сама, що в стовпці.</summary>
    public const int ReasonMaxLength = 1000;

    private RegistryDefinitionDraft() { }

    /// <summary>Заводить чернетку.</summary>
    public RegistryDefinitionDraft(
        int registryDefId, int baseDefinitionVersion, string contentJson, string reason, int userId, DateTime utcNow)
    {
        RegistryDefId = registryDefId;
        Replace(baseDefinitionVersion, contentJson, reason, userId, utcNow);
    }

    public int RegistryDefId { get; private set; }

    /// <summary>Версія опублікованого опису, від якої відштовхується чернетка.</summary>
    public int BaseDefinitionVersion { get; private set; }

    /// <summary>Поля й правила — у формі запиту на збереження опису.</summary>
    public string ContentJson { get; private set; } = null!;

    /// <summary>Причина зміни; при публікації йде в журнал.</summary>
    public string Reason { get; private set; } = null!;

    public int UpdatedByUserId { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Замінює вміст чернетки й перебазовує її на поточну версію опису.</summary>
    /// <exception cref="DomainException">Порожня або задовга причина — <c>ECR-REG-0422</c>.</exception>
    public void Replace(int baseDefinitionVersion, string contentJson, string reason, int userId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentJson);

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > ReasonMaxLength)
        {
            throw new DomainException(
                "ECR-REG-0422",
                "Причина зміни опису обов'язкова і не довша за 1000 символів.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0422.definitionReasonRequired" });
        }

        BaseDefinitionVersion = baseDefinitionVersion;
        ContentJson = contentJson;
        Reason = reason;
        UpdatedByUserId = userId;
        UpdatedAt = utcNow;
    }
}
