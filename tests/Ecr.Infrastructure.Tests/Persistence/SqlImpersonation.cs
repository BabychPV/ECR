using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Виконує код застосунку від імені користувача бази БЕЗ входу (<c>CREATE USER … WITHOUT LOGIN</c>):
/// кожне з'єднання з позначеним рядком підключення перед першою командою робить
/// <c>EXECUTE AS USER = N'…'</c>.
/// </summary>
/// <remarks>
/// ⛔ Чому не «відкрити з'єднання, EXECUTE AS, дати контексту»: сховища застосунку відкривають ВЛАСНІ
/// з'єднання з <c>db.Database.GetConnectionString()</c> (<c>RowChangeAudit</c>, <c>AuditWriter</c>,
/// <c>AuditReader</c>, <c>BulkCellLoader.ReserveIdsAsync</c>, <c>UiStringCatalogStore</c>…), і вони
/// пройшли б від імені власника бази — тобто тест був би зеленим саме там, де служба впала б. Тут
/// перемикається КОЖНЕ з'єднання з цим рядком, хоч би хто його відкрив.
///
/// ⚠ Механізм — подія <c>Microsoft.Data.SqlClient.WriteCommandBefore</c> (DiagnosticListener). Вона
/// синхронна і йде ДО виконання команди (і для <c>…Async</c>), тож перемикання гарантовано передує
/// першому запиту сеансу; подія <c>…ConnectionOpenAfter</c> для <c>OpenAsync</c> — продовження задачі, тобто
/// гонка з першою командою, тому не вона. Позначка — <c>Application Name</c> у рядку; <c>Pooling=false</c>:
/// закриття = кінець сеансу, тож контекст не переживає з'єднання і не потрапляє в пул (REVERT не потрібен).
/// Масове завантаження <c>SqlBulkCopy</c> подію не породжує — шляхи застосунку ним не користуються
/// (<c>BulkCellLoader.LoadAsync</c> — лише генератор обсягу).
/// </remarks>
internal static class SqlImpersonation
{
    private const string CommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";

    private static readonly ConcurrentDictionary<string, string> UsersByApplication = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, int> Switches = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<Guid, byte> Switched = new();

    private static readonly Lock Gate = new();

    private static IDisposable? _subscription;

    /// <summary>Рядок підключення, кожен сеанс якого працює від імені <paramref name="user"/>.</summary>
    /// <param name="connectionString">Рядок власника бази.</param>
    /// <param name="user">Користувач бази без входу.</param>
    /// <returns>Рядок із позначкою й без пулу.</returns>
    public static string ConnectionStringFor(string connectionString, string user)
    {
        Install();
        var application = "ecr-lowpriv-" + Guid.NewGuid().ToString("N")[..12];
        UsersByApplication[application] = user;
        return new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = application,
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>Скільки сеансів з цим рядком перемкнуто (доказ, що механізм справді спрацював).</summary>
    public static int SwitchCount(string connectionString)
        => Switches.TryGetValue(new SqlConnectionStringBuilder(connectionString).ApplicationName, out var n) ? n : 0;

    private static void Install()
    {
        lock (Gate)
        {
            _subscription ??= DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
        }
    }

    private static void OnCommandBefore(object? payload)
    {
        if (payload?.GetType().GetProperty("Command")?.GetValue(payload) is not SqlCommand command
            || command.Connection is not { } connection)
        {
            return;
        }

        var application = new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationName;
        if (!UsersByApplication.TryGetValue(application, out var user))
        {
            return;
        }

        // ⚠ Ключ — СЕАНС (ClientConnectionId), а не об'єкт SqlConnection: EF закриває й знову відкриває той самий
        // об'єкт, і без пулу кожне відкриття — новий сеанс із правами власника. Позначка ДО вкладеної команди:
        // вона сама породить цю ж подію.
        if (!Switched.TryAdd(connection.ClientConnectionId, 0))
        {
            return;
        }

        using var impersonate = connection.CreateCommand();
        impersonate.Transaction = command.Transaction;
        impersonate.CommandText = "EXECUTE AS USER = @user;";
        impersonate.Parameters.AddWithValue("@user", user);
        impersonate.ExecuteNonQuery();
        Switches.AddOrUpdate(application, 1, (_, n) => n + 1);
    }

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "SqlClientDiagnosticListener")
            {
                value.Subscribe(new EventObserver(), name => name == CommandBefore);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == CommandBefore)
            {
                OnCommandBefore(value.Value);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
