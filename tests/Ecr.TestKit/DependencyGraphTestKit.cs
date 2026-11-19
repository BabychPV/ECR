// tests/Ecr.TestKit/DependencyGraphTestKit.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.DocumentDependencies;
using Ecr.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.TestKit;

/// <summary>
/// B179 L6: помічники тестів, яким потрібен граф залежностей документів замість ручного
/// <c>cfg.DocumentSourceBindingConsumer</c>: справжній постачальник графа без мемоїзації ревізії й споживач — шаблон із
/// формулою, що читає довідник.
/// </summary>
public static class DependencyGraphTestKit
{
    private static readonly DateTime Now = new(2099, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Справжній постачальник графа над <paramref name="db"/>; ревізія читається щоразу (тест не чекає вікна).</summary>
    /// <param name="db">Контекст.</param>
    public static IDependencyGraphProvider Provider(EcrDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return new DependencyGraphProvider(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            new RealFormulaEngine(),
            new MethodologyRegistryEdges(db),
            new CacheLifetimes(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.Zero));
    }

    /// <summary>
    /// Робить шаблон документа споживачем довідника: формула колонки читає довідник (<c>REGCOUNT</c>), версія
    /// опублікована — граф бачить ребро «довідник → шаблон» за AST.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="document">Документ-споживач.</param>
    /// <param name="registryCode">Код довідника.</param>
    public static async Task MakeConsumerAsync(EcrDbContext db, TestDocument document, string registryCode)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);

        var formula = new FormulaDef(
            document.TableDefId, FormulaScope.Column, $"REGCOUNT('{registryCode}', ROW.VALUE > 0)", ExpressionDialect.Template);
        formula.AssignColumn(document.ColumnDefIds[1]);
        db.FormulaDefs.Add(formula);
        var version = await db.TemplateVersions.FindAsync(document.TemplateVersionId).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Версії шаблону споживача немає.");
        if (version.Status == TemplateVersionStatus.Draft)
        {
            version.Publish(1, Now);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
