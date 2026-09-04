// src/Ecr.Application/Ports/IOrphanScanner.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Обчислення ознаки <c>doc.TableRow.IsOrphaned</c> (<c>ФВ-8.13a</c>,
/// <c>D-98</c>). Викликається з нічної перевірки інваріантів (<c>ФВ-7.7</c>)
/// і при зміні вікна дії запису реєстру — **ніколи при читанні зрізу**:
/// перевірка чинності на кожен рядок зруйнувала б бюджет 400 мс.
/// </summary>
public interface IOrphanScanner
{
    /// <summary>Повний прохід. Повертає кількість змінених рядків.</summary>
    Task<int> ScanAllAsync(CancellationToken ct);

    /// <summary>
    /// Точковий перерахунок після зміни вікна дії запису. **Знімає** ознаку
    /// так само, як ставить: інакше виправлення довідника не розблокувало б
    /// <c>Submit</c>.
    /// </summary>
    Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct);
}
