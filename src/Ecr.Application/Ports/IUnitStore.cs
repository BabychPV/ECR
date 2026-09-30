// src/Ecr.Application/Ports/IUnitStore.cs

using Ecr.Domain.Entities.Units;

namespace Ecr.Application.Ports;

/// <summary>
/// Заведення нової одиниці довідника <c>uom.Unit</c>.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт від <see cref="IUnitCatalog"/> навмисно: той — знімок для
/// перевірки публікації (кешується на запит, читає лише), цей — звичайний
/// репозиторій для адміністративного запису. Змішати їх означало б, що
/// кешований знімок довелося б інвалідувати з порту, чиє призначення —
/// зовсім інше.
/// </remarks>
public interface IUnitStore
{
    /// <summary>Одиниця за кодом; <c>null</c> — немає.</summary>
    public Task<Unit?> FindUnitByCodeAsync(string code, CancellationToken ct);

    /// <summary>Чи існує розмірність із таким ідентифікатором.</summary>
    public Task<bool> DimensionExistsAsync(byte dimensionId, CancellationToken ct);

    /// <summary>Ставить нову одиницю в чергу на вставку.</summary>
    public void AddUnit(Unit unit);

    /// <summary>Одиниця за ідентифікатором; <c>null</c> — немає.</summary>
    public Task<Unit?> FindUnitByIdAsync(int unitId, CancellationToken ct);

    /// <summary>
    /// Усе, що посилається на одиницю: перші <paramref name="take"/> місць і
    /// загальна кількість (директива №15, BE-15).
    /// </summary>
    /// <remarks>
    /// ⚠ Структурні посилання (колонки, поля довідників, методики, мапінг,
    /// прив'язки PI за вікном рядка, конверсії, складені одиниці, розмірність)
    /// перелічуються поштучно. Таблиці ДАНИХ (комірки, шапки документів,
    /// значення довідників, результати розрахунків, сирі дані збору, провенанс
    /// прив'язок PI) дають по одному рядку на таблицю — «є значення в цій
    /// одиниці»: рахувати їх поштучно означало б сканувати мільйони рядків
    /// заради числа, яке в діалозі видалення нічого не вирішує.
    /// </remarks>
    public Task<Common.UsageResponse> FindUnitUsageAsync(int unitId, int take, CancellationToken ct);

    /// <summary>
    /// Одиниця, прочитана під блокуванням рядка до кінця транзакції; <c>null</c> — немає
    /// (аудит C6).
    /// </summary>
    /// <remarks>
    /// ⛔ Лише всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>: поза транзакцією
    /// блокування звільнилося б одразу. Друга правка чи видалення тієї самої одиниці чекає,
    /// доки перша закомітиться, і тоді читає ВЖЕ змінений рядок — версія з <c>If-Match</c>
    /// більше не збігається, і замість мовчазного перезапису виходить <c>409</c>.
    /// </remarks>
    public Task<Unit?> LockUnitAsync(int unitId, CancellationToken ct);

    /// <summary>
    /// Те саме, що <see cref="FindUnitUsageAsync"/>, але читання тримає діапазони до кінця
    /// транзакції (аудит C6).
    /// </summary>
    /// <remarks>
    /// ⛔ Лише всередині транзакції. Незакомічене посилання, що з'явилося до перевірки,
    /// перевірка чекає й бачить; нове посилання після перевірки чекає кінця транзакції —
    /// тобто «перевірили, що не використовується» і «видалили / змінили множник» стають
    /// однією атомарною дією.
    /// </remarks>
    public Task<Common.UsageResponse> FindUnitUsageForUpdateAsync(int unitId, int take, CancellationToken ct);

    /// <summary>Ставить одиницю в чергу на видалення.</summary>
    public void RemoveUnit(Unit unit);
}
