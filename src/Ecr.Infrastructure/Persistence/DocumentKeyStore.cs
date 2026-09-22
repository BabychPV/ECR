using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentKeyStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentKeyStore(EcrDbContext db) : IDocumentKeyStore
{
    /// <inheritdoc />
    public Task<Document?> FindForUpdateAsync(long documentId, CancellationToken ct)
        => db.Documents
            .FromSql($"SELECT * FROM doc.Document WITH (UPDLOCK) WHERE Id = {documentId}")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public Task<bool> IsKeyTakenAsync(int projectId, string businessKey, long exceptDocumentId, CancellationToken ct)
        => db.Documents
            .FromSql($"SELECT * FROM doc.Document WITH (UPDLOCK, HOLDLOCK) WHERE ProjectId = {projectId} AND BusinessKey = {businessKey}")
            .AsNoTracking()
            .AnyAsync(d => d.Id != exceptDocumentId, ct);
}
