// src/Ecr.Application/Ports/IPermitGroupStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Доступ до груп типів дозволів і їхнього складу (Permits, PRMG).</summary>
/// <remarks>
/// ⚠ Окремий порт: ядро реєстрів про групи не знає. Груп десятки - вибірки без сторінок.
/// </remarks>
public interface IPermitGroupStore
{
    /// <summary>Усі групи зі складом (без відстеження), за кодом.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitGroup>> ListAsync(CancellationToken ct);

    /// <summary>Група зі складом за кодом (відстежувана); <c>null</c> - немає.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitGroup?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>Ставить групу в чергу на вставку.</summary>
    /// <param name="group">Група.</param>
    public void Add(PermitGroup group);

    /// <summary>Прибирає групу разом зі складом і прихованим реєстром-«ідентичністю».</summary>
    /// <param name="group">Група.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RemoveAsync(PermitGroup group, CancellationToken ct);

    /// <summary>
    /// Знімок для <see cref="Permits.Groups.IPermitGroupResolver"/>: по рядку на групу з реєстрами шапок типів складу.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitGroupSnapshot> LoadSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Дешева «ревізія» складу всіх груп (сума <c>DataRevision</c> прихованих реєстрів груп і їхня кількість):
    /// змінюється, коли будь-яку групу створили, видалили чи змінили склад. Один агрегатний запит.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<long> GetSnapshotRevisionAsync(CancellationToken ct);
}

/// <summary>Група у знімку резолвера.</summary>
/// <param name="GroupRegistryDefId">Прихований реєстр-«ідентичність» групи (ціль Lookup).</param>
/// <param name="IsActive">Чи група активна.</param>
/// <param name="MemberRegistryDefIds">Реєстри шапок типів складу в порядку складу.</param>
public sealed record PermitGroupSnapshotItem(int GroupRegistryDefId, bool IsActive, IReadOnlyList<int> MemberRegistryDefIds);

/// <summary>Знімок усіх груп разом із ревізією, за якою він знятий.</summary>
/// <param name="Revision">Значення <see cref="IPermitGroupStore.GetSnapshotRevisionAsync"/> на момент читання.</param>
/// <param name="Items">Групи.</param>
public sealed record PermitGroupSnapshot(long Revision, IReadOnlyList<PermitGroupSnapshotItem> Items);
