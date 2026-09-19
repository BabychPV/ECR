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
    /// конверсії, складені одиниці, розмірність) перелічуються поштучно.
    /// Таблиці ДАНИХ (комірки, значення довідників, результати розрахунків,
    /// сирі дані збору) дають по одному рядку на таблицю — «є значення в цій
    /// одиниці»: рахувати їх поштучно означало б сканувати мільйони рядків
    /// заради числа, яке в діалозі видалення нічого не вирішує.
    /// </remarks>
    public Task<Common.UsageResponse> FindUnitUsageAsync(int unitId, int take, CancellationToken ct);

    /// <summary>Ставить одиницю в чергу на видалення.</summary>
    public void RemoveUnit(Unit unit);
}
