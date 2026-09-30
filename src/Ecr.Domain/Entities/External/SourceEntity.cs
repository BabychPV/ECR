// src/Ecr.Domain/Entities/External/SourceEntity.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Сутність збору в зовнішньому джерелі (<c>ext.SourceEntity</c>).
/// </summary>
/// <remarks>
/// Імена не вводяться руками: конфігуратор читає **каталог джерела** і дає
/// обрати зі списку (ФВ-13.13). Введене руками ім'я атрибута PI AF
/// відрізняється від справжнього одним символом рівно тоді, коли це найважче
/// помітити.
/// </remarks>
public sealed class SourceEntity : Entity<int>
{
    private SourceEntity() { }

    /// <summary>Створює сутність збору.</summary>
    /// <param name="dataSourceId">Джерело.</param>
    /// <param name="code">Код сутності в джерелі; входить у ключ унікальності.</param>
    /// <param name="sourceKind">Хто master для цих даних (ФВ-8.9).</param>
    public SourceEntity(int dataSourceId, string code, RegistrySourceKind sourceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        DataSourceId = dataSourceId;
        Code = code;
        SourceKind = sourceKind;
        IsActive = true;
    }

    public int DataSourceId { get; private set; }

    /// <summary>Код сутності в джерелі.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Підпис для конфігуратора; береться з каталогу джерела.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Шлях в ієрархії джерела: <c>\\Server\Db\Element</c>.</summary>
    public string? EntityPath { get; private set; }

    /// <summary>Хто master: зовнішня система, гібрид або ECR (ФВ-8.9).</summary>
    public RegistrySourceKind SourceKind { get; private set; }

    /// <summary>Довідник, який наповнюється з цієї сутності; <c>null</c> — не довідник.</summary>
    public int? RegistryDefId { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Ставить дані з каталогу джерела.</summary>
    /// <param name="displayName">Підпис.</param>
    /// <param name="entityPath">Шлях в ієрархії.</param>
    public void Describe(string? displayName, string? entityPath)
    {
        DisplayName = displayName;
        EntityPath = entityPath;
    }

    /// <summary>Прив'язує сутність до довідника.</summary>
    /// <param name="registryDefId">Довідник; <c>null</c> — відв'язати.</param>
    public void BindRegistry(int? registryDefId) => RegistryDefId = registryDefId;

    /// <summary>Вимикає збір із цієї сутності.</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Стеля імені атрибута чинності — ширина колонок <c>Valid*Attribute</c>.</summary>
    public const int MaxValidityAttributeLength = 200;

    /// <summary>Що робить синк довідника із записом, чий елемент зник із джерела (<c>D-212</c>).</summary>
    public RegistryMissingPolicy OnMissingInSource { get; private set; }

    /// <summary>
    /// Атрибут елемента AF, з якого синк бере <c>RegistryEntry.ValidFrom</c>;
    /// <c>null</c> — чинність не синхронізується.
    /// </summary>
    public string? ValidFromAttribute { get; private set; }

    /// <summary>Атрибут елемента AF, з якого синк бере кінець чинності; <c>null</c> — не береться.</summary>
    public string? ValidToAttribute { get; private set; }

    /// <summary>
    /// <c>true</c> — значення <see cref="ValidToAttribute"/> є ОСТАННІМ чинним днем
    /// (включна межа), і синк додає день, бо <c>RegistryEntry.ValidTo</c> виключна.
    /// </summary>
    public bool ValidToInclusive { get; private set; }

    /// <summary>Задає політику синку довідника з цієї сутності (<c>D-212</c>).</summary>
    /// <param name="policy">Що робити з записом, чий елемент зник у джерелі.</param>
    /// <param name="validFromAttribute">Атрибут початку чинності; <c>null</c> — не синхронізувати.</param>
    /// <param name="validToAttribute">Атрибут кінця чинності; <c>null</c> — не синхронізувати.</param>
    /// <param name="validToInclusive">Кінець у джерелі — останній чинний день.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-REQ-0422</c>: невідома політика, порожнє чи задовге ім'я атрибута,
    /// або включна межа без атрибута кінця.
    /// </exception>
    public void ConfigureRegistrySync(
        RegistryMissingPolicy policy,
        string? validFromAttribute,
        string? validToAttribute,
        bool validToInclusive)
    {
        // ⚠ Порожнє ім'я — помилка, а не «не задано»: «не задано» — це null, і
        // мовчазне перетворення "" на null сховало б зламану форму клієнта.
        if (!Enum.IsDefined(policy)
            || !IsValidAttribute(validFromAttribute)
            || !IsValidAttribute(validToAttribute)
            || (validToInclusive && validToAttribute is null))
        {
            throw new DomainException(
                ErrorCodes.RequestInvalid,
                $"Політика синку: невідоме значення або ім'я атрибута порожнє чи довше за {MaxValidityAttributeLength} символів; "
                + "включна межа вимагає атрибута кінця.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registrySyncPolicyInvalid",
                    ["max"] = MaxValidityAttributeLength,
                });
        }

        OnMissingInSource = policy;
        ValidFromAttribute = validFromAttribute?.Trim();
        ValidToAttribute = validToAttribute?.Trim();
        ValidToInclusive = validToInclusive;
    }

    private static bool IsValidAttribute(string? name)
        => name is null || (!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxValidityAttributeLength);
}
