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
/// ⚠ <c>SET LOCK_TIMEOUT</c> — налаштування СЕАНСУ, і назад його НЕ скидають навмисно: окреме
/// скидання — ще одне звернення на кожен запис (бюджет звернень стережуть
/// <c>PatchCellsWorkbookTests</c> і <c>CellStoreBatchEquivalenceTests</c>), а пул сам скидає сеанс
/// (<c>sp_reset_connection</c>) при поверненні з'єднання. Решта операторів тієї ж транзакції
/// (аудит, «дотик» документа) отримує той самий ліміт 8 с — для запиту користувача це прийнятно.
///
/// ⚠ 409 без <c>Retry-After</c>: проміжне ПЗ помилок такого заголовка для 409 не виставляє,
/// а клієнт повторює за повідомленням каталогу.
/// </remarks>
internal static class LockWaitGuard
{
    /// <summary>Скільки запис комірок чекає блокування, мс. Менше за 30 с <c>CommandTimeout</c>.</summary>
    internal const int LockTimeoutMs = 8_000;

    /// <summary>Ключ каталогу відмови «блокування не дочекалися».</summary>
    internal const string MessageKey = "err.ECR-DOC-4091.lockTimeout";

    /// <summary>1222 — «Lock request time out period exceeded»; -2 — таймаут команди клієнта.</summary>
    internal static bool IsLockWaitTimeout(SqlException ex) => ex.Number is 1222 or -2;

    /// <summary>Виконує <paramref name="body"/> з обмеженим очікуванням блокувань.</summary>
    /// <param name="connection">Відкрите з'єднання.</param>
    /// <param name="transaction">Транзакція, на якій виконуються оператори.</param>
    /// <param name="body">Записові оператори.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConcurrencyConflictException"><c>409 ECR-DOC-4091</c>, <c>lockTimeout</c>.</exception>
    internal static async Task RunAsync(
        SqlConnection connection, SqlTransaction transaction, Func<Task> body, CancellationToken ct)
    {
        await SetAsync(connection, transaction, LockTimeoutMs, ct).ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsLockWaitTimeout(ex))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.SheetBusy,
                "Записати комірки не вдалося: дані зайняті довгою операцією (наприклад, переносом версії "
                + "іншого документа). Нічого не збережено; повторіть дію за мить.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = MessageKey,
                    ["sqlError"] = ex.Number.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    private static async Task SetAsync(SqlConnection connection, SqlTransaction transaction, int ms, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"SET LOCK_TIMEOUT {ms};");
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
