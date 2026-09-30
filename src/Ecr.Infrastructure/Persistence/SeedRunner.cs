using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// <param name="db">Контекст бази.</param>
/// <param name="logger">
/// Журнал старту; <c>null</c> — PRINT-и сіду нікуди не йдуть (тести, інструменти).
/// ⚠ Не для налагодження: одноразові виправлення даних у сіді
/// (<c>COLL:period0-supersede</c>) звітують кількість змінених рядків саме
/// PRINT-ом, і без цього каналу застосунок, що виконує сід на кожному старті,
/// ковтав би їх мовчки.
/// </param>
public sealed partial class SeedRunner(EcrDbContext db, ILogger? logger = null)
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

        // ⚠ Підписка лише на час сіду: з'єднання належить контексту і живе далі,
        // а чужі PRINT-и в журнал сіду потрапляти не мають.
        var sql = logger is null ? null : connection as SqlConnection;
        if (sql is not null)
        {
            sql.InfoMessage += OnInfoMessage;
        }

        try
        {
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Сира команда не успадковує таймаут EF і мала б дефолтні 30 с замість
            // `Database:CommandTimeoutSeconds`. Беремо значення з того ж джерела,
            // що й EF, — на кожен батч: це дрібні MERGE, окрема межа їм не потрібна.
            var timeout = db.Database.GetCommandTimeout();

            foreach (var batch in batches)
            {
                await using var cmd = connection.CreateCommand();
                if (timeout is int seconds)
                {
                    cmd.CommandTimeout = seconds;
                }

                cmd.Transaction = tx;
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (sql is not null)
            {
                sql.InfoMessage -= OnInfoMessage;
            }
        }
    }

    /// <summary>PRINT сіду → журнал старту.</summary>
    /// <remarks>
    /// ⚠ Лише клас 0 — це і є PRINT. Помилки (клас 11+) сюди не доходять: вони
    /// летять винятком із <c>ExecuteNonQueryAsync</c> і зупиняють старт, як і раніше.
    /// </remarks>
    private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        foreach (SqlError message in e.Errors)
        {
            if (message.Class == 0)
            {
                LogSeedPrint(logger!, message.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed: {Message}")]
    private static partial void LogSeedPrint(ILogger logger, string message);

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
