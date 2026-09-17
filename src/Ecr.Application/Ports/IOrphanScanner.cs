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
    /// <summary>Один нічний прохід за курсором.</summary>
    /// <remarks>
    /// ⚠ Повертає ПІДСУМОК, а не саме́ число змінених рядків. Число нічого не
    /// каже про покриття: «нічого не змінив» і «нічого не оглянув» дають один
    /// і той самий нуль, а це протилежні стани системи. Див.
    /// <see cref="Registries.OrphanScanSummary"/>.
    /// </remarks>
    public Task<Registries.OrphanScanSummary> ScanAllAsync(CancellationToken ct);

    /// <summary>
    /// Точковий перерахунок після зміни вікна дії запису. **Знімає** ознаку
    /// так само, як ставить: інакше виправлення довідника не розблокувало б
    /// <c>Submit</c>.
    /// </summary>
    public Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct);
}
