// src/Ecr.Domain/Entities/External/SourceEntity.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Сутність збору: що саме тягнемо з джерела. <see cref="SourceKind"/>
/// визначає, **хто master** (ФВ-8.9) — і перемикається лише поза відкритим
/// періодом, із періодом подвійної звірки (D-49).
/// </summary>
public sealed class SourceEntity : Entity<int>
{
    private SourceEntity() { }

    public SourceEntity(int dataSourceId, string sourcePath, RegistrySourceKind sourceKind)
    {
        DataSourceId = dataSourceId;
        SourcePath = sourcePath;
        SourceKind = sourceKind;
        IsActive = true;
    }

    public int DataSourceId { get; private set; }

    /// <summary>Шлях у джерелі: шаблон AF, вʼюха, процедура.</summary>
    public string SourcePath { get; private set; } = null!;

    public RegistrySourceKind SourceKind { get; private set; }
    public int? TargetRegistryDefId { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Перемикання master. Дозволене лише поза відкритим періодом.</summary>
    public void SwitchSourceKind(RegistrySourceKind kind)
        => throw new NotImplementedException(
            "TODO: змінити SourceKind. Перевірку «немає відкритого періоду» " +
            "робить use-case (ECR-REG-0422) — сутність про періоди не знає. " +
            "Період подвійної звірки після перемикання — регламент, не код.");
}
