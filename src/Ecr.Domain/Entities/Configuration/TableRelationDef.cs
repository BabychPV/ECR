using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Зв'язок між таблицями: дзеркало, rollup, посилання, каскад, перевірка, копія.</summary>
public sealed class TableRelationDef : Entity<int>
{
    private TableRelationDef() { }

    public TableRelationDef(EcrCode code, int sourceTableDefId, int targetTableDefId,
                            TableRelationKind kind, string matchJson)
    {
        Code = code.Value;
        SourceTableDefId = sourceTableDefId;
        TargetTableDefId = targetTableDefId;
        RelationKind = kind;
        MatchJson = matchJson;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public int SourceTableDefId { get; private set; }
    public int TargetTableDefId { get; private set; }
    public TableRelationKind RelationKind { get; private set; }

    /// <summary>Як зіставляються рядки джерела і приймача.</summary>
    public string MatchJson { get; private set; } = null!;

    /// <summary>Які колонки на які.</summary>
    public string? MapJson { get; private set; }

    /// <summary>0 Recalc, 1 Warn, 2 Block — що робити при зміні джерела.</summary>
    public byte OnSourceChange { get; private set; }

    public bool IsActive { get; private set; }
}
