using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.TestKit;

/// <summary>
/// Складає <see cref="AccessProfile"/> і <see cref="CellAccessContext"/> для
/// сценаріїв доступу.
/// </summary>
/// <remarks>
/// Сценарії з <c>02c §6</c> відрізняються один від одного однією-двома
/// умовами. Без будівника кожен тест починався б із двадцяти рядків
/// підготовки, і рівно та умова, заради якої він написаний, губилася б.
/// </remarks>
public sealed class AccessBuilder
{
    /// <summary>Проєкт сценаріїв.</summary>
    public const int ProjectId = 10;

    /// <summary>Аркуш.</summary>
    public const int SheetId = 20;

    /// <summary>Таблиця.</summary>
    public const int TableId = 30;

    /// <summary>Колонка.</summary>
    public const int ColumnId = 40;

    private readonly Dictionary<string, GrantLevel> _grants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);

    /// <summary>Користувач, чиї права описуються.</summary>
    public int UserId { get; init; } = 7;

    /// <summary>Дає грант на ресурс.</summary>
    public AccessBuilder Grant(ResourceKind kind, int id, GrantLevel level)
    {
        _grants[$"{kind}:{id}"] = level;
        return this;
    }

    /// <summary>Забороняє ресурс явно; заборона виграє на будь-якому рівні.</summary>
    public AccessBuilder Deny(ResourceKind kind, int id)
    {
        _denies.Add($"{kind}:{id}");
        return this;
    }

    /// <summary>Додає функціональне право.</summary>
    public AccessBuilder Permission(string code)
    {
        _permissions.Add(code);
        return this;
    }

    /// <summary>Збирає профіль.</summary>
    public AccessProfile Build(bool simulation = false, int? simulatedFor = null)
        => new()
        {
            CacheKey = $"u{UserId}:s1",
            UserId = UserId,
            SecurityStamp = "s1",
            Permissions = _permissions,
            Grants = _grants,
            Denies = _denies,
            IsSimulation = simulation,
            SimulatedForUserId = simulatedFor,
            SimulationActorUserId = simulation ? UserId : null,
        };

    /// <summary>Умови комірки; за замовчуванням усе дозволяє.</summary>
    public static CellAccessContext Cell(
        PeriodState period = PeriodState.Open,
        DocumentStatus sheet = DocumentStatus.Draft,
        ProjectStatus project = ProjectStatus.Active,
        bool archiving = false,
        bool outOfWindow = false,
        bool computed = false,
        bool columnReadOnly = false,
        bool rowReadOnly = false,
        int columnId = ColumnId)
        => new(ProjectId, SheetId, TableId, columnId, project, archiving, period,
               outOfWindow, sheet, computed, columnReadOnly, rowReadOnly);
}
