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
}
