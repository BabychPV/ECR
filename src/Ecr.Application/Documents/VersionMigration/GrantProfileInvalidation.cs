using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Скидання кешу профілів після переносу версії: завжди fail-closed, ніколи не кидає.</summary>
/// <remarks>
/// ⛔ Викликається ПІСЛЯ коміту: виняток тут дав би клієнтові 500 при вже
/// комітнутій міграції, а мовчазний пропуск — застарілий профіль без заборони.
/// Тому будь-який збій і переповнення переліку скидають УВЕСЬ кеш.
/// </remarks>
public static partial class GrantProfileInvalidation
{
    /// <summary>Скидає профілі користувачів із <paramref name="users"/>.</summary>
    /// <param name="invalidator">Кеш профілів.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="users">Користувачі з прямою роллю, що має гранти на перенесені ресурси.</param>
    public static void Run(IAccessProfileInvalidator invalidator, ILogger log, GrantedUsers users)
    {
        ArgumentNullException.ThrowIfNull(invalidator);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(users);

        if (users.Overflow)
        {
            LogOverflow(log, users.Ids.Count);
            ClearAll(invalidator, log);
            return;
        }

        try
        {
            foreach (var userId in users.Ids)
            {
                invalidator.InvalidateUser(userId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUserFailed(log, ex);
            ClearAll(invalidator, log);
        }
    }

    private static void ClearAll(IAccessProfileInvalidator invalidator, ILogger log)
    {
        try
        {
            invalidator.InvalidateAll();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogClearFailed(log, ex);
            // fail-closed: кеш більше не віддає й не зберігає профілі до успішного повного скидання
            try
            {
                invalidator.MarkInvalidationFailed();
            }
            catch (Exception markEx) when (markEx is not OutOfMemoryException)
            {
                LogClearFailed(log, markEx);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Перелік користувачів для скидання кешу профілів перевищив стелю ({Count}+): скидаю весь кеш профілів.")]
    private static partial void LogOverflow(ILogger log, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Скидання кешу профілів після переносу версії не вдалося: скидаю весь кеш профілів.")]
    private static partial void LogUserFailed(ILogger log, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Не вдалося скинути весь кеш профілів після переносу версії: кеш переведено в fail-closed (профілі не кешуються).")]
    private static partial void LogClearFailed(ILogger log, Exception ex);
}
