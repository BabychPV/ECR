// src/Ecr.Infrastructure/Persistence/DocumentSourceBindingStore.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentSourceBindingStore"/> (B80 N1).</summary>
/// <remarks>
/// ⚠ Таблиці зв'язки визначає відображення (<see cref="DocumentSourceBinding.MappingKind"/> = 1): таблиці шаблону-джерела,
/// чий код — потік <see cref="Hse400CompositionMapping.Streams"/>; той самий відбір, що в
/// <see cref="Hse400CompositionSnapshotLoader"/>, і той самий вибір документа (найбільший id за рік, без проєкту
/// золотих тестів).
/// </remarks>
public sealed class DocumentSourceBindingStore(EcrDbContext db) : IDocumentSourceBindingStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSourceBindingView>> ListAsync(CancellationToken ct)
    {
        var rows = await (
                from b in db.DocumentSourceBindings.AsNoTracking()
                join t in db.Templates.AsNoTracking() on b.SourceTemplateId equals t.Id
                join r in db.RegistryDefs.AsNoTracking() on b.RegistryDefId equals r.Id
                orderby b.Code
                select new
                {
                    b.Id, b.Code, TemplateCode = t.Code, RegistryCode = r.Code, b.SourceState, b.Policy,
                    b.ReadFromDocument, b.RecalculateDependents, b.UpdatedAt, b.UpdatedByUserId,
                })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = rows.Select(r => r.Id).ToList();
        var consumers = await (
                from c in db.DocumentSourceBindingConsumers.AsNoTracking()
                where ids.Contains(c.BindingId)
                join t in db.Templates.AsNoTracking() on c.TemplateId equals t.Id
                select new { c.BindingId, t.Code })
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new DocumentSourceBindingView(
            r.Id,
            r.Code,
            r.TemplateCode,
            r.RegistryCode,
            r.SourceState,
            r.Policy,
            r.ReadFromDocument,
            r.RecalculateDependents,
            [.. consumers.Where(c => c.BindingId == r.Id).Select(c => c.Code).Order(StringComparer.Ordinal)],
            r.UpdatedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            r.UpdatedByUserId))];
    }

    /// <inheritdoc />
    public Task<DocumentSourceBinding?> LockAsync(int id, CancellationToken ct)
        => db.DocumentSourceBindings
            .FromSqlInterpolated($"SELECT * FROM cfg.DocumentSourceBinding WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<SourceDocumentFacts?> ChosenSourceDocumentAsync(int bindingId, int year, CancellationToken ct)
        => await ChosenSourceDocumentAsync(await BoundTableIdsAsync(bindingId, ct).ConfigureAwait(false), year, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// ✎ B80 N2b: той самий вибір документа за вже прочитаними таблицями зв'язки (<see cref="BoundTableIdsAsync"/>) —
    /// для воріт подання й затвердження споживача (<see cref="DocumentSourceMonthGate"/>).
    /// </summary>
    /// <param name="tableIds">Таблиці зв'язки.</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<SourceDocumentFacts?> ChosenSourceDocumentAsync(
        IReadOnlyCollection<int> tableIds, int year, CancellationToken ct)
    {
        var yearStart = year * 100;
        var yearEnd = (year + 1) * 100;
        if (tableIds.Count == 0)
        {
            return null;
        }

        var documentId = await (
                from i in db.TableInstances.AsNoTracking()
                where tableIds.Contains(i.TableDefId) && i.SheetCopyId == null
                      && i.PeriodKeyValue >= yearStart && i.PeriodKeyValue < yearEnd
                join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where p.Code != Hse400CompositionMapping.GoldProjectCode
                select (long?)i.DocumentId)
            .MaxAsync(ct).ConfigureAwait(false);
        if (documentId is not { } chosen)
        {
            return null;
        }

        // Версія шаблону — та, на якій стоять екземпляри таблиць зв'язки цього року (найновіша, якщо їх кілька).
        return await (
                from i in db.TableInstances.AsNoTracking()
                where i.DocumentId == chosen && tableIds.Contains(i.TableDefId) && i.SheetCopyId == null
                      && i.PeriodKeyValue >= yearStart && i.PeriodKeyValue < yearEnd
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                orderby v.Id descending
                select new SourceDocumentFacts(d.Id, p.PeriodKind, v.WorkflowMode))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> SourceYearsAsync(int bindingId, CancellationToken ct)
    {
        var tableIds = await BoundTableIdsAsync(bindingId, ct).ConfigureAwait(false);
        if (tableIds.Count == 0)
        {
            return [];
        }

        var periods = await (
                from i in db.TableInstances.AsNoTracking()
                where tableIds.Contains(i.TableDefId) && i.SheetCopyId == null
                join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where p.Code != Hse400CompositionMapping.GoldProjectCode
                select i.PeriodKeyValue)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. periods.Select(p => p / 100).Distinct().Order()];
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>Таблиці зв'язки: таблиці шаблону-джерела (усі версії), чий код — потік складу HSE400.</summary>
    /// <param name="bindingId">Зв'язка.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<List<int>> BoundTableIdsAsync(int bindingId, CancellationToken ct)
    {
        var tables = await (
                from b in db.DocumentSourceBindings.AsNoTracking()
                where b.Id == bindingId
                join v in db.TemplateVersions.AsNoTracking() on b.SourceTemplateId equals v.TemplateId
                join s in db.SheetDefs.AsNoTracking() on v.Id equals s.TemplateVersionId
                where !s.IsDeleted
                join d in db.TableDefs.AsNoTracking() on s.Id equals d.SheetDefId
                where !d.IsDeleted
                select new { d.Id, d.Code })
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. tables
            .Where(t => Hse400CompositionMapping.Streams.Any(s => string.Equals(s, t.Code, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Id)];
    }
}

/// <summary>Перекриття бітів зв'язки ключами конфігурації (B80; див. <see cref="Hse400CompositionSnapshotLoader.Override"/>).</summary>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class DocumentSourceFlagOverrides(IConfiguration configuration) : IDocumentSourceFlagOverrides
{
    /// <inheritdoc />
    public bool? ReadFromDocument { get; } =
        Hse400CompositionSnapshotLoader.Override(configuration, Hse400CompositionSnapshotLoader.EnabledKey);
}
