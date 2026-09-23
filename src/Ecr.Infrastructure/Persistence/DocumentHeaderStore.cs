using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentHeaderStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentHeaderStore(EcrDbContext db) : IDocumentHeaderStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, DocumentHeaderValueData>> GetValuesAsync(
        long documentId, CancellationToken ct)
    {
        var rows = await db.DocumentHeaderValues
            .AsNoTracking()
            .Where(v => v.DocumentId == documentId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(v => v.HeaderFieldDefId, v => v.ToData());
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Приєднання до <c>HeaderFieldDef</c> тут потрібне, щоб віддати
    /// значення, ключовані КОДОМ поля: рушій виразів (<c>HDR.Code</c>) знає
    /// код, а не внутрішній <c>HeaderFieldDefId</c>.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ExpressionValue>> GetExpressionValuesAsync(
        long documentId, CancellationToken ct)
    {
        var rows = await (
            from value in db.DocumentHeaderValues.AsNoTracking()
            join field in db.HeaderFieldDefs.AsNoTracking() on value.HeaderFieldDefId equals field.Id
            where value.DocumentId == documentId && !field.IsDeleted
            select new { field.Code, Value = value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<string, ExpressionValue>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.Code] = HeaderValueMapping.ToExpressionValue(row.Value.ToData());
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SaveValuesAsync(
        long documentId,
        IReadOnlyDictionary<int, DocumentHeaderValueData> values,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);

        var fieldIds = values.Keys.ToArray();
        var existing = await db.DocumentHeaderValues
            .Where(v => v.DocumentId == documentId && fieldIds.Contains(v.HeaderFieldDefId))
            .ToDictionaryAsync(v => v.HeaderFieldDefId, ct)
            .ConfigureAwait(false);

        foreach (var (fieldId, data) in values)
        {
            if (existing.TryGetValue(fieldId, out var row))
            {
                row.Apply(data);
            }
            else
            {
                db.DocumentHeaderValues.Add(new DocumentHeaderValue(documentId, fieldId, data));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
