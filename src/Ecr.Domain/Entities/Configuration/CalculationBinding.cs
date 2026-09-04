using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Прив'язка результату методології до колонки документа.
/// Значення **не копіюється** в <c>doc.CellValue</c> — воно читається за
/// посиланням (D-69): інакше нічний перерахунок писав би десятки мільйонів
/// рядків у партиції документів і роздував аудит.
/// </summary>
public sealed class CalculationBinding : Entity<int>
{
    private CalculationBinding() { }

    public CalculationBinding(int tableDefId, int columnDefId, int methodologyId, string outputCode, string matchJson)
    {
        TableDefId = tableDefId;
        ColumnDefId = columnDefId;
        MethodologyId = methodologyId;
        OutputCode = outputCode;
        MatchJson = matchJson;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public int ColumnDefId { get; private set; }
    public int MethodologyId { get; private set; }

    /// <summary>Який вихід методології (<c>tons</c>, <c>gsec</c>).</summary>
    public string OutputCode { get; private set; } = null!;

    /// <summary>Як зіставити рядок документа з результатом розрахунку.</summary>
    public string MatchJson { get; private set; } = null!;

    public bool IsActive { get; private set; }
}
