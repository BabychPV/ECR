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

    /// <summary>Скільки задача чекає лок, перш ніж відкластися (O1, I2 ФВ-9.8).</summary>
    /// <remarks>
    /// ⛔ Секунда, а не хвилини. Задача, що чекає лок, ТРИМАЄ СЛОТ виконавця: у
    /// замірі I2 всі 10 потоків Quartz стояли за локом гарячого документа, і
    /// перерахунок проєкту простояв 630 с, не почавшись. Коротке очікування
    /// покриває звичайний випадок (сусідня інкрементна задача пише кілька сотень
    /// мілісекунд), а довгий власник лока (повний перерахунок) — привід звільнити
    /// слот, а не чекати його.
    /// </remarks>
    public static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(1);

    /// <summary>Через скільки відкладена задача пробує знову.</summary>
    public static readonly TimeSpan DeferDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Базова пауза перед повтором після вичерпаного очікування блокування даних (<c>ECR-DOC-4091</c>,
    /// <c>lockTimeout</c>); F1 (WW-02).
    /// </summary>
    /// <remarks>
    /// ⚠ Довша за <see cref="DeferDelay"/>: запис комірок уже чекав <c>LockWaitGuard.LockTimeoutMs</c> (15 с), і
    /// повтор за 5 с вдарив би в ту саму зайняту таблицю. Стеля сумарного очікування — <c>JobDeferral.MaxDeferral</c>
    /// (30 хв), тож ~60 повторів замість трьох ретраїв 30/60/120 с.
    /// </remarks>
    public static readonly TimeSpan LockWaitDeferBase = TimeSpan.FromSeconds(20);

    /// <summary>Верхня межа «розсіювання» повтору між документами, с (<see cref="LockWaitDeferDelay"/>).</summary>
    private const int LockWaitJitterSeconds = 10;

    /// <summary>Пауза перед повтором задачі документа, що не дочекалася блокування.</summary>
    /// <param name="documentId">Документ.</param>
    /// <returns><see cref="LockWaitDeferBase"/> + детерміноване розсіювання 0–9 с за номером документа.</returns>
    /// <remarks>
    /// ⚠ Розсіювання за документом, а не <c>Random</c>: десятки задач, що впали в один момент, інакше
    /// вернулися б до тієї самої таблиці в одну секунду; детермінізм — щоб тест міг його перевірити.
    /// </remarks>
    public static TimeSpan LockWaitDeferDelay(long documentId)
        => LockWaitDeferBase + TimeSpan.FromSeconds(Math.Abs(documentId % LockWaitJitterSeconds));

    /// <summary>
    /// Чи виняток — вичерпане очікування блокування даних запису (<c>ECR-DOC-4091</c> з ключем
    /// <c>lockTimeout</c> або сирий <c>SqlException 1222</c> у ланцюжку).
    /// </summary>
    /// <param name="ex">Виняток задачі.</param>
    /// <returns><c>true</c> — зайнятість, що минає.</returns>
    /// <remarks>
    /// ⚠ За кодом і ключем каталогу, не за текстом: той самий <c>ECR-DOC-4091</c> несе й «аркуш подається»
    /// та «структуру змінено» — їх розбирають окремо (<c>FormulaRecalculationJob.IsSheetBeingSubmitted</c>).
    /// </remarks>
    public static bool IsLockWaitTimeout(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var tagged = ex is Ecr.Application.Errors.ConcurrencyConflictException busy
                     && busy.ErrorCode == Ecr.Domain.Errors.ErrorCodes.SheetBusy
                     && busy.Details?.GetValueOrDefault("messageKey") is LockWaitGuard.MessageKey;

        return tagged || LockWaitGuard.FindLockWaitTimeout(ex) is not null;
    }

    /// <summary>Бере ексклюзивний лок документа, чекаючи не довше за <paramref name="timeout"/>.</summary>
    /// <param name="db">Контекст задачі — джерело рядка з'єднання.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="timeout">Скільки чекати (задачі — <see cref="BusyWait"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c> — лок не потрібен (документа немає, не SQL Server).</returns>
    /// <exception cref="JobDeferredException">
    /// Не дочекалися: документ рахує інша задача. Виконавець (<c>JobWorker</c>,
    /// <c>QuartzJobAdapter</c>) повертає задачу в чергу через <see cref="DeferDelay"/>
    /// БЕЗ спроби ретраю і звільняє слот — перерахунок не пропускається мовчки й
    /// не вичерпує ретраїв, скільки б не тривав чужий прогін.
    /// </exception>
    public static async Task<SqlDistributedLock?> AcquireAsync(
        EcrDbContext db, long documentId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (documentId <= 0 || !db.Database.IsSqlServer())
        {
            return null;
        }

        // ⛔ Лише ПОЗА транзакцією — лок «зовнішній», береться першим. Він живе на
        // ОКРЕМОМУ з'єднанні, тож очікування «applock ↔ рядкові блокування»
        // SQL Server дедлоком не бачить: сесія, що вже тримає блокування даних
        // документа й стала в чергу за цим локом, поки власник лока на робочому
        // з'єднанні чекає ті самі рядки, висіла б до таймауту (15 хв). Тому брати
        // його дозволено лише до першої транзакції задачі; PATCH, імпорт,
        // подання й публікація версії шаблону його не беруть узагалі
        // (`RecalculationDocumentLockCallersTests`, `FormulaRecalculationDocumentLockTests`).
        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException(
                "Лок перерахунку документа береться лише поза транзакцією: усередині вона могла б тримати блокування, яких чекає власник лока.");
        }

        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Немає рядка з'єднання для лока документа.");

        return await SqlDistributedLock
                   .AcquireAsync(connectionString, Resource(documentId), timeout, ct)
                   .ConfigureAwait(false)
               ?? throw new JobDeferredException(
                   DeferDelay,
                   string.Create(
                       CultureInfo.InvariantCulture,
                       $"Документ {documentId} перераховує інша задача; відкладено на {DeferDelay.TotalSeconds:0} с."),
                   Resource(documentId));
    }
}

/// <summary>
/// Задача не може виконуватися ЗАРАЗ (ресурс зайнятий) і просить виконавця
/// повернути її в чергу через <see cref="Delay"/>, звільнивши слот.
/// </summary>
/// <remarks>
/// ⛔ Не провал і не ретрай: спроба (<c>Attempt</c>, <c>RetryAttemptKey</c>) не
/// збільшується, <c>JobRetryPolicy</c> цей виняток не бачить. Інакше задача, що
/// чесно чекає довгий повний перерахунок, вичерпала б ретраї й упала б <c>Failed</c>.
/// Межа відкладень — окрема: <see cref="JobDeferral.MaxDeferral"/> від першого.
/// </remarks>
/// <param name="delay">Через скільки повторити.</param>
/// <param name="message">Причина — для журналу.</param>
/// <param name="resource">Зайнятий ресурс (ім'я лока) — у конверт <see cref="JobDeferral.ExhaustedKey"/>.</param>
public sealed class JobDeferredException(TimeSpan delay, string message, string? resource = null) : Exception(message)
{
    /// <summary>Через скільки задачу варто спробувати знову.</summary>
    public TimeSpan Delay { get; } = delay;

    /// <summary>Зайнятий ресурс; <c>null</c> — не названо.</summary>
    public string? Resource { get; } = resource;
}
