using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Ідемпотентний seed. Без нього застосунок не стартує: немає ані мов, ані
/// прав, ані базових одиниць.
/// </summary>
/// <remarks>
/// Скрипт лежить у <c>Persistence/Sql/09-seed.sql</c> і **вбудований у
/// збірку**: інакше застосунок можна було б запустити з чужою або застарілою
/// копією seed, і розбіжність вилізла б не тут, а на першому вході, де немає
/// підписів кнопок.
///
/// ⚠ Це <b>єдиний</b> випадок, коли застосунок сам виконує SQL-скрипт. Решта
/// (<c>01</c>–<c>08</c>) — DDL, а DDL-прав у застосунку немає (<c>D-66</c>).
/// Seed — це DML, і за <c>02-contracts.md</c> §14 він належить застосунку.
/// </remarks>
public sealed class SeedRunner(EcrDbContext db)
{
    private const string ResourceName = "Ecr.Infrastructure.Persistence.Sql.09-seed.sql";

    /// <summary>Виконує seed. Повторний запуск не створює дублікатів.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var batches = SqlBatches.Split(ReadScript());

        // Одна транзакція на весь seed. Кожен MERGE ідемпотентний і сам по
        // собі, але напівзастосований seed — це база, яка стартує і мовчки
        // не має половини прав; краще не стартувати взагалі.
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (var batch in batches)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string ReadScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Ресурс '{ResourceName}' не вбудований у збірку. " +
                "Перевірте <EmbeddedResource> у Ecr.Infrastructure.csproj.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
