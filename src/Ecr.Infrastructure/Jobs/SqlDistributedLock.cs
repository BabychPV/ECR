using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Взаємовиключення між ІНСТАНСАМИ застосунку через <c>sp_getapplock</c>.
/// </summary>
/// <remarks>
/// ⛔ Q-223 (`Jobs`-секція): D-32 вимагає ≥2 інстанси застосунку за
/// балансувальником, а нічні/погодинні задачі ставилися в Quartz на
/// КОЖНОМУ інстансі окремо (`RecurringScheduleService`) — кожен інстанс
/// тримає свій ВЛАСНИЙ `in-memory` планувальник, і той самий крон-триґер
/// спрацьовує в кожному одночасно. Без координації N інстансів виконують
/// той самий job N разів на кожен тик.
///
/// Той самий примітив, що вже в `StartupSequence.ApplySchemaModeAsync`
/// (там — навколо міграції), але з ІНШОЮ семантикою: міграція має
/// зачекати й виконатися рівно один раз; повторний прогін нічної задачі
/// не повинен чекати й виконуватися ПІЗНІШЕ — він має просто ПРОПУСТИТИ
/// цей тик, якщо інший інстанс уже виконує той самий job
/// (`@LockTimeout = 0`, не блокуюча спроба).
///
/// Лок тримається на ОКРЕМОМУ, виділеному з'єднанні на весь час виконання
/// job'и: `sp_getapplock @LockOwner = 'Session'` прив'язаний до сесії, що
/// його взяла, і звільняється лише тим самим з'єднанням (або
/// автоматично, коли з'єднання рветься) — реюзати з'єднання, яким job
/// сама читає/пише дані, означало б, що ЇЇ власні `SqlConnection`
/// (через EF `DbContext`) можуть закритися чи змінитися всередині
/// виконання, а лок має пережити все це незмінним.
/// </remarks>
public sealed class SqlDistributedLock : IAsyncDisposable
{
    private readonly SqlConnection connection;
    private readonly string resource;
    private bool released;

    private SqlDistributedLock(SqlConnection connection, string resource)
    {
        this.connection = connection;
        this.resource = resource;
    }

    /// <summary>
    /// Пробує взяти лок негайно, без очікування. <c>null</c> — інший
    /// інстанс/сесія вже тримає той самий ресурс просто зараз.
    /// </summary>
    public static Task<SqlDistributedLock?> TryAcquireAsync(
        string connectionString, string resource, CancellationToken ct)
        => AcquireAsync(connectionString, resource, TimeSpan.Zero, ct);

    /// <summary>
    /// Бере лок, чекаючи не довше за <paramref name="timeout"/>. <c>null</c> — не
    /// дочекалися: ресурс досі тримає інша сесія.
    /// </summary>
    /// <remarks>
    /// ⚠ Скасування <paramref name="ct"/> під час очікування — <see cref="OperationCanceledException"/>,
    /// а не <see cref="SqlException"/> «operation cancelled»: витіснена задача
    /// (<c>EnqueueExclusiveAsync</c>) мусить виглядати скасованою, а не зламаною.
    /// </remarks>
    public static async Task<SqlDistributedLock?> AcquireAsync(
        string connectionString, string resource, TimeSpan timeout, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var acquire = connection.CreateCommand();
            acquire.CommandText =
                "DECLARE @result int; " +
                "EXEC @result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', " +
                "@LockOwner = 'Session', @LockTimeout = @Timeout; " +
                "SELECT @result;";
            acquire.Parameters.AddWithValue("@Resource", resource);
            acquire.Parameters.AddWithValue("@Timeout", (int)timeout.TotalMilliseconds);

            // Команда не має впасти за власним таймаутом раніше, ніж лок за своїм.
            acquire.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) + 30;

            int result;
            try
            {
                result = (int)(await acquire.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }
            catch (SqlException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            // sp_getapplock: 0/1 — узято (1 — були інші охочі); від'ємне —
            // не взято (-1 — «зайнято» / не дочекалися в межах @LockTimeout).
            if (result < 0)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new SqlDistributedLock(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (released)
        {
            return;
        }

        released = true;

        try
        {
            await using var release = connection.CreateCommand();
            release.CommandText = "EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session';";
            release.Parameters.AddWithValue("@Resource", resource);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // З'єднання вже розірване (напр., SQL Server перезапущено) —
            // сесія й так забирає лок із собою, коли рветься.
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
