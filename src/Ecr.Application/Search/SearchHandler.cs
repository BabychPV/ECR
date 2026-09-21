// src/Ecr.Application/Search/SearchHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Search;

/// <summary>Пошук даних для командної палітри (BE-19): документи, шаблони, довідники.</summary>
/// <remarks>
/// ⛔ Видимість — ТІ САМІ перевірки, що й у переліків: <c>Document.View</c> +
/// гранти проєкту (<see cref="Documents.ListDocumentsHandler.ReadableProjects"/>),
/// <c>Template.View</c>, <c>Registry.View</c>. Немає права на тип — тип просто
/// не шукається (не 403): палітра одна для всіх ролей.
/// </remarks>
public sealed class SearchHandler(ISearchStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Коротший запит не шукається: одна літера збігається майже з усім.</summary>
    public const int MinTermLength = 2;

    /// <summary>Типова стеля відповіді.</summary>
    public const int DefaultLimit = 10;

    /// <summary>Абсолютна стеля відповіді, хоч би що попросив клієнт.</summary>
    public const int MaxLimit = 20;

    /// <summary>Шукає.</summary>
    /// <param name="query">Підрядок коду чи назви.</param>
    /// <param name="limit">Стеля; <c>&lt;= 0</c> — типова, понад <see cref="MaxLimit"/> — обрізається.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<SearchHitDto>> HandleAsync(string? query, int limit, CancellationToken ct)
    {
        var term = query?.Trim() ?? string.Empty;

        // Порожньо, а не 422: палітра шле запит на кожне натискання, і перша
        // літера — це не помилка користувача.
        if (term.Length < MinTermLength)
        {
            return [];
        }

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var scope = new SearchScope(
            profile.Has(Documents.ListDocumentsHandler.Permission)
                ? Documents.ListDocumentsHandler.ReadableProjects(profile)
                : null,
            profile.Has(Templates.ListTemplatesHandler.Permission),
            profile.Has(Registries.ListRegistriesHandler.Permission));

        var take = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        var rows = await store.SearchAsync(term, scope, take, ct).ConfigureAwait(false);

        return [.. Interleave(rows)
            .Take(take)
            .Select(r => new SearchHitDto(r.Kind, r.Id, r.Code, r.Name?.Get(currentUser.Language) ?? r.Code))];
    }

    /// <summary>По одному з кожного типу по черзі: десять документів не витісняють шаблон.</summary>
    private static IEnumerable<SearchRow> Interleave(IReadOnlyList<SearchRow> rows)
    {
        var queues = rows.GroupBy(r => r.Kind, StringComparer.Ordinal).Select(g => new Queue<SearchRow>(g)).ToList();

        while (queues.Exists(q => q.Count > 0))
        {
            foreach (var queue in queues.Where(q => q.Count > 0))
            {
                yield return queue.Dequeue();
            }
        }
    }
}

/// <summary>Один збіг пошуку палітри. Маршрут будує клієнт за <paramref name="Kind"/>.</summary>
/// <param name="Kind"><c>document</c>, <c>template</c> або <c>registry</c>.</param>
/// <param name="Id">Ідентифікатор сутності.</param>
/// <param name="Code">Код; для документа — <c>BusinessKey</c>.</param>
/// <param name="Title">Назва мовою запиту; немає назви — код.</param>
public sealed record SearchHitDto(string Kind, long Id, string Code, string Title);
