using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IPermitGroupStore"/>. ⚠ Етап «контракт»: схеми груп (міграція <c>PRMG01</c>) ще немає, тому
/// читання повертає «груп немає» (резолвер при цьому коректний - жодна ціль не є групою), а запис кидає
/// <see cref="InvalidOperationException"/>. Наступний коміт lane замінює цей файл справжньою реалізацією.
/// </summary>
public sealed class PermitGroupStore : IPermitGroupStore
{
    private const string NotReady = "Сховище груп дозволів ще не підключено: потрібна міграція PRMG01.";

    /// <inheritdoc />
    public Task<IReadOnlyList<PermitGroup>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermitGroup>>([]);

    /// <inheritdoc />
    public Task<PermitGroup?> FindByCodeAsync(string code, CancellationToken ct)
        => Task.FromResult<PermitGroup?>(null);

    /// <inheritdoc />
    public void Add(PermitGroup group) => throw new InvalidOperationException(NotReady);

    /// <inheritdoc />
    public Task RemoveAsync(PermitGroup group, CancellationToken ct) => throw new InvalidOperationException(NotReady);

    /// <inheritdoc />
    public Task<PermitGroupSnapshot> LoadSnapshotAsync(CancellationToken ct)
        => Task.FromResult(new PermitGroupSnapshot(0, []));

    /// <inheritdoc />
    public Task<long> GetSnapshotRevisionAsync(CancellationToken ct) => Task.FromResult(0L);
}
