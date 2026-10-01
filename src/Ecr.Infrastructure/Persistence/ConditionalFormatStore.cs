using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IConditionalFormatStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ConditionalFormatStore(EcrDbContext db) : IConditionalFormatStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ConditionalFormatRule>> GetAsync(int templateVersionId, CancellationToken ct)
        => await db.ConditionalFormatRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.ColumnCode).ThenBy(r => r.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task ReplaceAsync(
        int templateVersionId, IReadOnlyList<ConditionalFormatRule> rules, CancellationToken ct)
    {
        // Один SaveChanges: EF впорядковує DELETE перед INSERT, тож UQ_CondFmt_Order
        // не заважає заміні (доводить ConditionalFormatStoreTests).
        var existing = await db.ConditionalFormatRules
            .Where(r => r.TemplateVersionId == templateVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.ConditionalFormatRules.RemoveRange(existing);
        db.ConditionalFormatRules.AddRange(rules);
    }
}