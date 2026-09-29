// src/Ecr.Infrastructure/Jobs/RecalculationDocumentLock.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Сесійний <c>sp_getapplock</c> «документ перераховується» — спільний для
/// повного (<see cref="RecalculationJob"/>) і інкрементного
/// (<see cref="FormulaRecalculationJob"/>) перерахунку.
/// </summary>
/// <remarks>
/// ⛔ Обидві задачі пишуть обчислені комірки того самого документа
/// (<c>doc.CellValue</c>, <c>IsCalculated = 1</c>), рахуючи їх ПОЗА транзакцією
/// запису. Без спільного лока повний перерахунок, що прочитав входи до PATCH,
/// перезаписував свіже число інкрементного старішим
/// (<c>FormulaRecalculationDocumentLockTests</c>). Лок на окремому з'єднанні
/// (<see cref="SqlDistributedLock"/>): задача живе кількома транзакціями, і
/// транзакційний лок не накрив би її цілком.
/// </remarks>
public static class RecalculationDocumentLock
{
    /// <summary>Ресурс <c>sp_getapplock</c> для перерахунку документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <returns>Ім'я ресурсу.</returns>
    public static string Resource(long documentId)
        => string.Create(CultureInfo.InvariantCulture, $"ecr:recalc:doc:{documentId}");

    /// <summary>Бере ексклюзивний лок документа, чекаючи не довше за <paramref name="timeout"/>.</summary>
    /// <param name="db">Контекст задачі — джерело рядка з'єднання.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="timeout">Скільки чекати.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c> — лок не потрібен (документа немає, не SQL Server).</returns>
    /// <exception cref="InvalidOperationException">
    /// Не дочекалися. Навмисно тип, який <c>JobRetryPolicy</c> ретраїть: задача
    /// повертається в чергу з відступом, а не пропускає перерахунок мовчки.
    /// </exception>
    public static async Task<SqlDistributedLock?> AcquireAsync(
        EcrDbContext db, long documentId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (documentId <= 0 || !db.Database.IsSqlServer())
        {
            return null;
        }

        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Немає рядка з'єднання для лока документа.");

        return await SqlDistributedLock
                   .AcquireAsync(connectionString, Resource(documentId), timeout, ct)
                   .ConfigureAwait(false)
               ?? throw new InvalidOperationException(string.Create(
                   CultureInfo.InvariantCulture,
                   $"Документ {documentId} перераховує інша задача довше за {timeout.TotalSeconds:0} с; спробуємо пізніше."));
    }
}
