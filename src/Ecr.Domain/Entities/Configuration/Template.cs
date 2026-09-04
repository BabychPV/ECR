using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Іменований набір структур звітності. Сам по собі структури не містить:
/// вона живе у <see cref="TemplateVersion"/>, бо мусить бути версійною.
/// </summary>
public sealed class Template : Entity<int>
{
    private readonly List<TemplateVersion> _versions = [];

    private Template() { }   // для EF Core

    /// <summary>Створює шаблон.</summary>
    public Template(EcrCode code, LocalizedText name, int createdByUserId, DateTime utcNow)
    {
        Code = code.Value;
        NameL10n = name;
        CreatedByUserId = createdByUserId;
        CreatedAt = utcNow;
        IsActive = true;
    }

    /// <summary>Код шаблону, унікальний у системі.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Локалізована назва.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Довільні теги (<c>["ECR","Land"]</c>) — замість предметних колонок у ядрі.</summary>
    public string? TagsJson { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }

    /// <summary>Версії шаблону.</summary>
    public IReadOnlyList<TemplateVersion> Versions => _versions;

    /// <summary>Позначає шаблон неактивним. Наявні документи не зачіпаються.</summary>
    public void Deactivate() => IsActive = false;
}
