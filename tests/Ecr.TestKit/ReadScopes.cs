using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.TestKit;

/// <summary>
/// Межі читання документа (<see cref="DocumentReadScope"/>, S6) для заглушок
/// <see cref="IAccessDecisionService"/>.
/// </summary>
/// <remarks>
/// ⚠ Будується тим самим бойовим <see cref="DocumentReadScope.For"/>, а не
/// підробкою: заглушка каже лише «профіль бачить проєкт без жодної
/// заборони», а рішення по таблицях і колонках рахує справжнє правило. Тести,
/// що користуються цим помічником, — не про доступ; про доступ —
/// <c>DocumentReadScopeTests</c> і <c>DenyReadTests</c> (Api).
/// </remarks>
public static class ReadScopes
{
    /// <summary>Профіль бачить усе в документі.</summary>
    /// <param name="snapshot">Знімок структури документа.</param>
    public static DocumentReadScope Everything(TemplateVersionSnapshot snapshot)
        => DocumentReadScope.For(
            new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build(),
            AccessBuilder.ProjectId,
            snapshot);
}
