// src/Ecr.Domain/Entities/Workflow/ApprovalRoute.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Маршрут погодження: багатоетапне затвердження конфігурується на рівні
/// проєкту (ФВ-5.17).
/// </summary>
/// <remarks>
/// Кроки посилаються на ролі **за ідентифікаторами**, тому перейменування ролі
/// не ламає вже налаштований маршрут.
/// </remarks>
public sealed class ApprovalRoute : Entity<int>
{
    private readonly List<ApprovalStep> _steps = [];

    private ApprovalRoute() { }

    public ApprovalRoute(EcrCode code, LocalizedText name)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public IReadOnlyList<ApprovalStep> Steps => _steps;
}
