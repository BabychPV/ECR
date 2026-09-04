// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Periods;

/// <summary>Будує календар періодів проєкту (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarHandler(IUnitOfWork uow, IClock clock)
{
    public Task<int> HandleAsync(int projectId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) кількість періодів за PeriodKind: Monthly = 12, Quarterly = 4, " +
            "   Yearly = 1, Custom = задано вручну;\n" +
            "2) PeriodKey = Year*100 + Sequence; Sequence ЛИШЕ 1..12 (D-108), " +
            "   інакше ECR-PRD-4224 — верхня межа не довільна, вона збігається " +
            "   з межами партиційної функції;\n" +
            "3) межі рахувати опівночі В ПОЯСІ МАЙДАНЧИКА, потім у UTC (D-68). " +
            "   DateTime.Now заборонений, час лише з IClock;\n" +
            "4) RecomputeBoundaries за PeriodPolicy проєкту;\n" +
            "5) ідемпотентність: повторний виклик не створює дублікатів.");
}
