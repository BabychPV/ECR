// src/Ecr.Infrastructure/Persistence/LockWaitGuard.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Обмежує очікування блокувань на записових шляхах комірок (<c>doc.CellValue</c>,
/// <c>doc.TableRow</c>) і перетворює вичерпане очікування на <c>409 ECR-DOC-4091</c>.
/// </summary>
/// <remarks>
/// ⛔ Що було (TIER2, N-3). Перенос версії великого проєкту (~2 млн значень, ~14 хв) тримає
/// блокування <c>doc.CellValue</c>/<c>doc.TableRow</c> до коміту; через ескалацію воно
/// накриває таблицю цілком. Правка комірки ІНШОГО проєкту чекала на <c>LCK_M_IX</c> увесь
/// <c>CommandTimeout</c> (30 с) і падала голим <c>SqlException</c> «Execution Timeout» —
/// <c>500 ECR-SYS-0500</c> «зверніться до адміністратора» на стан, що минає сам.
///
/// ⚠ Вузько, а не в <c>ExceptionHandlingMiddleware</c>: узагальнений catch на весь API
/// ховав би під 409 чужі таймаути (звіти, пошук), які дефектом і є. Тут перехоплюється лише
/// запис комірок.
///
/// ⛔ AN-106 (P1-01, аудит 2026-10-09b). Ліміт сеансу діє й на оператори ПІСЛЯ охоронця (аудит
/// <c>aud.CellChange</c>, «дотик» документа, <c>SaveChanges</c>): їхній 1222 доти виходив сирим
/// <c>SqlException</c> → 500. Тепер <c>UnitOfWork.ExecuteInTransactionAsync</c> перекладає 1222 з
/// будь-якого оператора транзакції в ту саму відмову (<see cref="FindLockWaitTimeout"/>,
/// <see cref="Busy"/>). Це не ширше за задум: 1222 буває ЛИШЕ після <c>SET LOCK_TIMEOUT</c>,
/// а його виставляє тільки запис комірок. Ліміт виставляється ДО створення нових рядків
/// (<see cref="LimitAsync"/>, <c>ICellStore.LimitLockWaitAsync</c>), щоб і вставка в
/// <c>doc.TableRow</c> не чекала весь <c>CommandTimeout</c>.
///
/// ⚠ <c>SET LOCK_TIMEOUT</c> — налаштування СЕАНСУ, і назад його НЕ скидають навмисно: окреме
/// скидання — ще одне звернення на кожен запис (бюджет звернень стережуть
/// <c>PatchCellsWorkbookTests</c> і <c>CellStoreBatchEquivalenceTests</c>), а пул сам скидає сеанс
/// (<c>sp_reset_connection</c>) при поверненні з'єднання. Решта операторів тієї ж транзакції
/// (аудит, «дотик» документа) отримує той самий ліміт 15 с — для запиту користувача це прийнятно.
///
/// ⚠ 409 без <c>Retry-After</c>: проміжне ПЗ помилок такого заголовка для 409 не виставляє,
/// а клієнт повторює за повідомленням каталогу.
/// </remarks>
public static class LockWaitGuard
{
    /// <summary>
    /// Скільки запис комірок чекає блокування, мс. 15 с: ліміт діє на ВСІ оператори ambient-транзакції
    /// (аудит, «дотик»), тож 8 с давали б хибні 409 при нормальних конкурентних PATCH одного проєкту
    /// під піком; і лишається запас до 30 с <c>CommandTimeout</c>.
    /// </summary>
    public const int LockTimeoutMs = 15_000;

    /// <summary>Ключ каталогу відмови «блокування не дочекалися».</summary>
    public const string MessageKey = "err.ECR-DOC-4091.lockTimeout";

    /// <summary>
    /// Лише 1222 — «Lock request time out period exceeded» (спрацював <c>SET LOCK_TIMEOUT</c>).
    /// ⛔ НЕ -2: це клієнтський Execution Timeout будь-якого повільного запиту, не обов'язково через
    /// блокування; ховати його під 409 означало б маскувати справжні повільні запити.
    /// </summary>
    public static bool IsLockWaitTimeout(SqlException ex) => ex.Number == 1222;

    /// <summary>Виконує <paramref name="body"/> з обмеженим очікуванням блокувань.</summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="transaction">Транзакція, на якій виконуються оператори.</param>
    /// <param name="body">Записові оператори.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConcurrencyConflictException"><c>409 ECR-DOC-4091</c>, <c>lockTimeout</c>.</exception>
    public static Task RunAsync(
        SqlConnection connection, SqlTransaction transaction, Func<Task> body, CancellationToken ct)
        => RunAsync(connection, transaction, limitAlreadySet: false, body, ct);

    /// <summary>
    /// Те саме, але без другого <c>SET</c>, якщо ліміт у цій транзакції вже виставлено
    /// (<see cref="LimitAsync"/> на початку транзакції запису — AN-106).
    /// </summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="transaction">Транзакція, на якій виконуються оператори.</param>
    /// <param name="limitAlreadySet"><c>true</c> — <c>SET LOCK_TIMEOUT</c> у цій транзакції вже був.</param>
    /// <param name="body">Записові оператори.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConcurrencyConflictException"><c>409 ECR-DOC-4091</c>, <c>lockTimeout</c>.</exception>
    public static async Task RunAsync(
        SqlConnection connection, SqlTransaction transaction, bool limitAlreadySet, Func<Task> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!limitAlreadySet)
        {
            await LimitAsync(connection, transaction, ct).ConfigureAwait(false);
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsLockWaitTimeout(ex))
        {
            throw Busy(ex);
        }
    }

    /// <summary>Виставляє ліміт очікування блокувань на сеанс транзакції запису.</summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="transaction">Транзакція запису.</param>
    /// <param name="ct">Скасування.</param>
    public static Task LimitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
        => SetAsync(connection, transaction, LockTimeoutMs, ct);

    /// <summary>
    /// <c>SqlException 1222</c> будь-де в ланцюжку винятку (EF загортає його в
    /// <c>DbUpdateException</c>, стратегія повторів — у <c>RetryLimitExceededException</c>);
    /// <c>null</c> — не вичерпане очікування блокування.
    /// </summary>
    /// <param name="exception">Виняток.</param>
    /// <returns>Знайдений <see cref="SqlException"/> або <c>null</c>.</returns>
    public static SqlException? FindLockWaitTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && IsLockWaitTimeout(sql))
            {
                return sql;
            }
        }

        return null;
    }

    /// <summary>Відмова <c>409 ECR-DOC-4091</c> з ключем <see cref="MessageKey"/>.</summary>
    /// <param name="ex">Спійманий <c>SqlException 1222</c>.</param>
    /// <returns>Виняток для кидка.</returns>
    public static ConcurrencyConflictException Busy(SqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new ConcurrencyConflictException(
            ErrorCodes.SheetBusy,
            "Записати комірки не вдалося: дані зайняті довгою операцією (наприклад, переносом версії "
            + "іншого документа). Нічого не збережено; повторіть дію за мить.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["sqlError"] = ex.Number.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static async Task SetAsync(SqlConnection connection, SqlTransaction transaction, int ms, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"SET LOCK_TIMEOUT {ms};");
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
