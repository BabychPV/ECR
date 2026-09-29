// src/Ecr.Domain/Entities/Security/RoleAssignmentScope.cs
using System.Text.Json;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Область дії призначення ролі (ФВ-6.14): роль діє ЛИШЕ в перелічених
/// проєктах. Зберігається в <c>sec.RoleAssignment.ScopeJson</c> як
/// <c>{"projects":[1,2]}</c>.
/// </summary>
/// <remarks>
/// ⚠ ТЗ називає три виміри — проєкт, аркуш, період, — але семантику дає лише
/// проєктові. Тому формат розширюваний (об'єкт, а не голий масив), а
/// реалізовано рівно <c>projects</c>. Невідомий ключ у збереженому JSON НЕ
/// ігнорується (<see cref="TryParse"/>): поле, яке записали й не
/// застосували, розширило б права мовчки.
/// </remarks>
public sealed class RoleAssignmentScope
{
    private const string ProjectsKey = "projects";

    private RoleAssignmentScope(IReadOnlyList<int> projectIds) => ProjectIds = projectIds;

    /// <summary>Проєкти, у яких діє призначення; впорядковані, без повторів, непорожні.</summary>
    public IReadOnlyList<int> ProjectIds { get; }

    /// <summary>Створює область із переліку проєктів.</summary>
    /// <param name="projectIds">Проєкти; непорожній перелік додатних ідентифікаторів без повторів.</param>
    /// <returns>Область дії.</returns>
    /// <exception cref="DomainException">Порожньо, повтор або недодатний ідентифікатор — <c>ECR-REQ-0422</c>.</exception>
    /// <remarks>
    /// ⛔ Порожня область — не «без обмежень», а роль, що не дає нічого. Таку
    /// річ не записуємо зовсім: відсутність області й порожня область
    /// читалися б однаково тим, хто дивиться в базу, і діяли б протилежно.
    /// </remarks>
    public static RoleAssignmentScope Create(IEnumerable<int> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        var list = projectIds.ToList();
        if (list.Count == 0 || list.Exists(id => id <= 0) || list.Distinct().Count() != list.Count)
        {
            throw new DomainException(
                ErrorCodes.RequestInvalid,
                "Область дії ролі: потрібен непорожній перелік проєктів без повторів.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422" });
        }

        list.Sort();
        return new RoleAssignmentScope(list);
    }

    /// <summary>Чи входить проєкт в область.</summary>
    /// <param name="projectId">Проєкт.</param>
    public bool Includes(int projectId) => ProjectIds.Contains(projectId);

    /// <summary>JSON для <c>sec.RoleAssignment.ScopeJson</c>.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<int>> { [ProjectsKey] = ProjectIds });

    /// <summary>Розбирає збережену область.</summary>
    /// <param name="json">Вміст <c>ScopeJson</c>.</param>
    /// <returns>
    /// Область, або <c>null</c> — JSON зіпсований чи несе невідомий ключ.
    /// </returns>
    /// <remarks>
    /// ⛔ Викликач трактує <c>null</c> тут як «область, що не містить нічого»
    /// (роль не діє), а НЕ як «області немає» (роль діє скрізь): зіпсований
    /// запис має відбирати права, а не роздавати їх.
    /// </remarks>
    public static RoleAssignmentScope? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<int>? projects = null;
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, ProjectsKey, StringComparison.Ordinal)
                    || property.Value.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                projects = [];
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var id))
                    {
                        return null;
                    }

                    projects.Add(id);
                }
            }

            return projects is null ? null : Create(projects);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
