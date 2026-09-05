// src/Ecr.Domain/Entities/External/SourceEntity.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

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
}
