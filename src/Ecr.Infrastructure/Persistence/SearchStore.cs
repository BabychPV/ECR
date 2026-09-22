// src/Ecr.Infrastructure/Persistence/SearchStore.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ISearchStore"/>: по одному обмеженому запиту на тип.</summary>
/// <remarks>
/// ⚠ Сирий SQL: назви — JSON у <c>nvarchar(max)</c>, а <c>ToJson</c> екранує
/// кирилицю в <c>\uXXXX</c>, тож <c>LIKE</c> по сирому стовпцю кирилиці не
/// знайде. <c>OPENJSON</c> розкодовує значення. Індексу під <c>LIKE '%q%'</c>
/// немає: документи — seek за <c>ProjectId</c> (<c>UQ_Document</c>) і перебір
/// документів видимих проєктів; шаблони й довідники — повний перебір (десятки рядків).
/// </remarks>
public sealed class SearchStore(EcrDbContext db) : ISearchStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchRow>> SearchAsync(
        string term, SearchScope scope, int perKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var pattern = "%" + Escape(term) + "%";
        var result = new List<SearchRow>();

        if (scope.DocumentProjects is { Count: > 0 } projects)
        {
            var projectsJson = JsonSerializer.Serialize(projects);
            var documents = db.Database.SqlQuery<Row>($"""
                SELECT d.Id, d.BusinessKey AS Code, d.NameL10n AS Name
                FROM doc.Document d
                WHERE d.ProjectId IN (SELECT CAST(j.value AS int) FROM OPENJSON({projectsJson}) j)
                  AND (d.BusinessKey LIKE {pattern} ESCAPE N'\'
                       OR EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(d.NameL10n) = 1 THEN d.NameL10n END) n
                                  WHERE n.value COLLATE DATABASE_DEFAULT LIKE {pattern} ESCAPE N'\'))
                """);
            result.AddRange(await FirstAsync(documents, "document", perKind, ct).ConfigureAwait(false));
        }

        if (scope.Templates)
        {
            var templates = db.Database.SqlQuery<Row>($"""
                SELECT CAST(t.Id AS bigint) AS Id, t.Code, t.NameL10n AS Name
                FROM cfg.Template t
                WHERE t.Code LIKE {pattern} ESCAPE N'\'
                   OR EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(t.NameL10n) = 1 THEN t.NameL10n END) n
                              WHERE n.value COLLATE DATABASE_DEFAULT LIKE {pattern} ESCAPE N'\')
                """);
            result.AddRange(await FirstAsync(templates, "template", perKind, ct).ConfigureAwait(false));
        }

        if (scope.Registries)
        {
            // Лише активні — як і перелік довідників (`RegistryStore.ListDefinitionsAsync`).
            var registries = db.Database.SqlQuery<Row>($"""
                SELECT CAST(r.Id AS bigint) AS Id, r.Code, r.NameL10n AS Name
                FROM cfg.RegistryDef r
                WHERE r.IsActive = 1
                  AND (r.Code LIKE {pattern} ESCAPE N'\'
                       OR EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(r.NameL10n) = 1 THEN r.NameL10n END) n
                                  WHERE n.value COLLATE DATABASE_DEFAULT LIKE {pattern} ESCAPE N'\'))
                """);
            result.AddRange(await FirstAsync(registries, "registry", perKind, ct).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>Перші <paramref name="perKind"/> за кодом — <c>TOP</c> компонує EF.</summary>
    private static async Task<IEnumerable<SearchRow>> FirstAsync(
        IQueryable<Row> query, string kind, int perKind, CancellationToken ct)
    {
        var rows = await query.OrderBy(r => r.Code).ThenBy(r => r.Id).Take(perKind)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new SearchRow(kind, r.Id, r.Code, r.Name is null ? null : LocalizedText.FromJson(r.Name)));
    }

    /// <summary>Екранує метасимволи <c>LIKE</c>: запит «50%» шукає «50%», а не все з «50».</summary>
    private static string Escape(string term)
        => term.Replace(@"\", @"\\", StringComparison.Ordinal)
               .Replace("%", @"\%", StringComparison.Ordinal)
               .Replace("_", @"\_", StringComparison.Ordinal)
               .Replace("[", @"\[", StringComparison.Ordinal);

    /// <summary>Рядок запиту; імена колонок — імена властивостей.</summary>
    public sealed record Row(long Id, string Code, string? Name);
}
