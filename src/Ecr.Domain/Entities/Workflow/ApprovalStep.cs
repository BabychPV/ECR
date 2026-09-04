// src/Ecr.Domain/Entities/Workflow/ApprovalStep.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>Крок маршруту погодження: хто і на якому місці затверджує.</summary>
public sealed class ApprovalStep : Entity<int>
{
    private ApprovalStep() { }

    public ApprovalStep(int approvalRouteId, int ordinal, int roleId)
    {
        ApprovalRouteId = approvalRouteId;
        Ordinal = ordinal;
        RoleId = roleId;
    }

    public int ApprovalRouteId { get; private set; }

    /// <summary>Порядок кроку в маршруті; унікальний у межах маршруту.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Роль за ідентифікатором, не за назвою.</summary>
    public int RoleId { get; private set; }

    /// <summary>Крок можна пропустити.</summary>
    public bool IsOptional { get; private set; }
}
