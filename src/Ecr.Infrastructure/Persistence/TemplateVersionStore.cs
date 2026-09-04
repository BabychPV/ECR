using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через EF без гонки.
/// </summary>
/// <remarks>
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт уведений `Q-032`,
/// реалізація — `Q-050`.
/// </remarks>
public sealed class TemplateVersionStore(EcrDbContext db) : ITemplateVersionStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДИН statement із <c>OUTPUT</c> (<c>R-B7</c>). Послідовність
    /// «прочитати → додати одиницю → записати» під паралельними
    /// презентаційними правками дає два однакові значення ревізії, а ревізія —
    /// це ключ кешу <c>v{id}:r{rev}</c>. Два різні знімки під одним ключем
    /// означають, що частина інстансів віддає стару структуру, і знайти це
    /// потім практично неможливо.
    /// </remarks>
    public async Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is { } tx)
        {
            command.Transaction = (SqlTransaction)tx.GetDbTransaction();
        }

        command.CommandText = """
            UPDATE cfg.TemplateVersion
            SET    PresentationRevision = PresentationRevision + 1
            OUTPUT inserted.PresentationRevision
            WHERE  Id = @id;
            """;
        command.Parameters.AddWithValue("@id", templateVersionId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? throw new NotFoundException("ECR-TMPL-0404", $"Версії шаблону {templateVersionId} не існує.")
            : (int)result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Питання не про кількість, а про факт: якщо на версії вже є документи,
    /// структурна правка заборонена незалежно від того, один він чи мільйон.
    /// Тому <c>AnyAsync</c>, а не <c>CountAsync</c>.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct)
        => db.Documents
             .AsNoTracking()
             .Join(db.Projects.AsNoTracking(),
                   d => d.ProjectId,
                   p => p.Id,
                   (d, p) => p.TemplateVersionId)
             .AnyAsync(id => id == templateVersionId, ct);
}
