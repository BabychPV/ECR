using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Одиниця роботи поверх <see cref="EcrDbContext"/>.
/// </summary>
/// <remarks>
/// ⚠ Довгі транзакції заборонені (<c>D-29</c>): архівація, міграція документа
/// і масовий імпорт ідуть батчами з окремим commit — інакше version store під
/// RCSI росте необмежено.
///
/// Постановка перерахунку в чергу відбувається <b>поза</b> транзакцією запису:
/// інакше воркер починає читати рядки, яких ще не видно, і отримує або старі
/// значення, або блокування на піку останнього дня періоду.
/// </remarks>
public sealed class UnitOfWork(EcrDbContext db) : IUnitOfWork
{
    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Клієнт має побачити, ЩО саме розійшлося: «409» без переліку не
            // дає йому нічого, крім пропозиції спробувати ще раз наосліп.
            var conflicts = ex.Entries
                .Select(e => new
                {
                    entity = e.Metadata.DisplayName(),
                    key = string.Join(
                        ",",
                        e.Metadata.FindPrimaryKey()?.Properties
                            .Select(p => $"{p.Name}={e.Property(p.Name).CurrentValue}") ?? []),
                })
                .ToList();

            throw new Application.Errors.ConcurrencyConflictException(
                "ECR-CELL-0409",
                "Дані змінилися після того, як ви їх прочитали.",
                new Dictionary<string, object?> { ["conflicts"] = conflicts });
        }
        catch (DbUpdateException ex) when (SqlConflict.IsUniqueConstraintViolation(ex))
        {
            // ⛔ Integration-pending фікс findings 2-3: `CreateProjectHandler`
            // не має перевірки коду заздалегідь (`UQ_Project_Code`), а
            // `UpsertRegistryEntryHandler.CreateAsync` перевіряє дублікат
            // проти знімка, прочитаного НА ПОЧАТКУ обробки запиту (TOCTOU,
            // той самий клас, що й Q-241/Q-245) — подвійний клік на
            // «Зберегти» посилає два запити тим самим кодом майже одночасно,
            // обидва проходять перевірку ДО того, як перший закомітиться, і
            // другий падає на `UQ_RegistryEntry` тут. В обох випадках без
            // цієї гілки виняток ішов НЕОБРОБЛЕНИМ до
            // `ExceptionHandlingMiddleware` голим `500 ECR-SYS-0500`.
            //
            // ⚠ Мапиться за ТИПОМ доданої сутності (`ex.Entries`), а не за
            // назвою індексу з тексту `SqlException.Message`: розбір рядка
            // повідомлення СУБД крихкий до локалізації сервера й версії, тип
            // .NET-об'єкта — ні.
            if (TryMapDuplicateKey(ex) is { } mapped)
            {
                throw mapped;
            }

            // Дублікат ключа сутності, для якої немає доменного
            // повідомлення, — краще необроблений 500 із CorrelationId, ніж
            // вигадана відповідь про те, чого перевірка тут не знає.
            throw;
        }
    }

    /// <summary>
    /// Дублікат унікального коду ролі/проєкту/запису довідника — у чисту
    /// доменну відмову. <c>null</c>, якщо серед доданих сутностей немає
    /// жодної з відомих (виклик мусить перекинути оригінальний виняток).
    /// </summary>
    private static BusinessRuleException? TryMapDuplicateKey(DbUpdateException ex)
    {
        foreach (var entry in ex.Entries)
        {
            switch (entry.Entity)
            {
                case Domain.Entities.Documents.Project project:
                    return new BusinessRuleException(
                        ErrorCodes.ProjectDuplicate, $"Проєкт із кодом «{project.Code}» уже існує.",
                        // ⛔ Q-30x: узагальнений шлях ExceptionHandlingMiddleware
                        // (messageKey), не точковий арм на цей один код —
                        // без нього подробиця доїжджала клієнту сирим
                        // українським реченням незалежно від мови інтерфейсу.
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-PRJ-0409.projectCodeTaken", ["code"] = project.Code,
                        });

                case Domain.Entities.Dictionaries.RegistryEntry registryEntry:
                    // ⚠ Той самий код, що й перевірка «до запису» в
                    // `UpsertRegistryEntryHandler.CreateAsync` (`ECR-REG-0409`):
                    // клієнт бачить ОДНУ причину незалежно від того, який із
                    // двох одночасних запитів програв гонитву за унікальним
                    // індексом.
                    return new BusinessRuleException(
                        ErrorCodes.RegistryEntryInUse,
                        $"Запис із кодом «{registryEntry.Code}» у цьому довіднику вже існує.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-REG-0409.entryCodeTaken", ["code"] = registryEntry.Code,
                        });
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Q-243. <c>EnableRetryOnFailure</c> увімкнено на реальному
    /// DbContext (`DependencyInjection.cs`), і EF Core забороняє ручний
    /// <c>Database.BeginTransactionAsync</c> ПОЗА <c>ExecuteAsync</c>
    /// стратегії повторів — кидає <c>InvalidOperationException</c> одразу
    /// на виклику (точно той самий прийом, що вже рятує
    /// <c>UserStore.CreateRoleAsync</c> від тієї ж помилки). До цього рядка
    /// порт ніхто не викликав, тому дефект був живий, але не спостережний:
    /// перший-таки виклик у проді впав би на самому <c>BeginTransaction</c>.
    ///
    /// ⚠ Стратегія тут обгортає ЛИШЕ сам <c>BeginTransactionAsync</c>, не
    /// решту блоку роботи виклику: подальші кроки (кілька збережень,
    /// сторонні виклики портів) виконуються ПОЗА цим <c>ExecuteAsync</c>, і
    /// це свідомо — ретрай усього блоку тут неможливий без зміни контракту
    /// порту (він розділяє «відкрити» і «зробити роботу» на різні виклики,
    /// на відміну від <c>UserStore</c>, де все одним замиканням). Наслідок:
    /// транзієнтний збій ПІСЛЯ відкриття не ретраїться автоматично — весь
    /// виклик просто впаде, а <c>TransactionScope.DisposeAsync</c> відкотить
    /// незакомічене. Це не регресія: до цієї правки виклику не було взагалі.
    /// </remarks>
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var transaction = await strategy
            .ExecuteAsync(() => db.Database.BeginTransactionAsync(ct))
            .ConfigureAwait(false);
        return new TransactionScope(transaction);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Q-243. На відміну від <see cref="BeginTransactionAsync"/> (де
    /// стратегія обгортає ЛИШЕ сам <c>BeginTransaction</c>), тут стратегія
    /// обгортає ВЕСЬ блок: відкриття, <paramref name="operation"/> і коміт —
    /// одним замиканням, точно як уже робить <c>UserStore.CreateRoleAsync</c>.
    /// Без цього перший-таки EF-виклик (<c>ExecuteUpdateAsync</c>,
    /// <c>SaveChangesAsync</c>) усередині <paramref name="operation"/> кидає
    /// <c>InvalidOperationException</c> проти реального DbContext з
    /// <c>EnableRetryOnFailure</c> — виміряно прогоном
    /// <c>Ecr.Scenarios.Tests</c> проти реально піднятого <c>Ecr.Api</c>
    /// (перший варіант фіксу з окремими Begin/Commit впав РІВНО на цьому).
    ///
    /// ⛔ Якщо транзакція вже відкрита ЗОВНІ (вкладений виклик у межах
    /// ширшого блоку) — просто виконуємо: другий <c>BeginTransaction</c> на
    /// тому самому <c>DbContext</c> SQL Server не підтримує (`Вкладені
    /// транзакції заборонені`, <c>UnitOfWorkTests</c>), і коміт/відкат тоді
    /// належить ЗОВНІШНЬОМУ виклику.
    /// </remarks>
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (db.Database.CurrentTransaction is not null)
        {
            await operation(ct).ConfigureAwait(false);
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await operation(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Обгортка, чий <c>DisposeAsync</c> відкочує незакомічену транзакцію.
    /// </summary>
    /// <remarks>
    /// «Забули закомітити» має відкотитися, а не лишити відкриту транзакцію:
    /// під RCSI забута транзакція тримає версії рядків у tempdb і псує життя
    /// всій базі, а не тільки своєму запиту.
    /// </remarks>
    private sealed class TransactionScope(IDbContextTransaction transaction) : IAsyncDisposable, IEcrTransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Транзакція, яку можна закомітити явно.</summary>
/// <remarks>
/// <see cref="IUnitOfWork.BeginTransactionAsync"/> за контрактом повертає
/// <see cref="IAsyncDisposable"/>. Щоб закомітити, виклик приводить результат
/// до цього інтерфейсу — так контракт лишається незмінним, а «забули
/// закомітити» і далі означає відкат.
/// </remarks>
public interface IEcrTransaction
{
    /// <summary>Фіксує транзакцію.</summary>
    public Task CommitAsync(CancellationToken ct);
}
