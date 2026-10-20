using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitStore"/> над <see cref="EcrDbContext"/> (Permits, PM-04).</summary>
public sealed class PermitStore(EcrDbContext db) : IPermitStore
{
    /// <summary>Скільки чекати замка номера, мс: той, хто його тримає, лише читає заголовки й пише один запис.</summary>
    private const int NumberLockTimeoutMs = LockWaitGuard.LockTimeoutMs;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, PermitRowStat>> RowStatsAsync(
        int permitFieldDefId, int? totalFieldDefId, IReadOnlyCollection<long>? permitIds, CancellationToken ct)
    {
        // Рядок таблиці належить дозволу значенням поля композиції; видалені логічно рядки не рахуються.
        var parents = db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == permitFieldDefId && v.ValueRefEntryId != null && !v.Entry!.IsDeleted);
        if (permitIds is not null)
        {
            var wanted = permitIds.ToList();
            parents = parents.Where(v => wanted.Contains(v.ValueRefEntryId!.Value));
        }

        var counts = await parents
            .GroupBy(v => v.ValueRefEntryId!.Value)
            .Select(g => new { Permit = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var totals = new Dictionary<long, decimal>();
        if (totalFieldDefId is { } totalField && counts.Count > 0)
        {
            var sums = await parents
                .Join(
                    db.RegistryValues.AsNoTracking().Where(v => v.RegistryFieldDefId == totalField && v.ValueNumeric != null),
                    parent => parent.RegistryEntryId,
                    total => total.RegistryEntryId,
                    (parent, total) => new { Permit = parent.ValueRefEntryId!.Value, total.ValueNumeric })
                .GroupBy(x => x.Permit)
                .Select(g => new { Permit = g.Key, Sum = g.Sum(x => x.ValueNumeric!.Value) })
                .ToListAsync(ct).ConfigureAwait(false);
            totals = sums.ToDictionary(s => s.Permit, s => s.Sum);
        }

        return counts.ToDictionary(
            c => c.Permit,
            c => new PermitRowStat(c.Count, totalFieldDefId is null ? null : totals.GetValueOrDefault(c.Permit)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Власник - транзакція (<c>@LockOwner = 'Transaction'</c>): замок знімає коміт чи відкат, забути
    /// <c>sp_releaseapplock</c> неможливо; поза транзакцією <c>sp_getapplock</c> відмовляє - викликач мусить її відкрити.
    /// Ім'я ресурсу - хеш (тип + номер): ресурс applock не довший за 255 символів, а номер сам до 200.
    /// </remarks>
    public async Task LockNumberAsync(string typeCode, string number, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        ArgumentException.ThrowIfNullOrEmpty(number);

        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{typeCode.ToUpperInvariant()}\n{number.ToUpperInvariant()}");
        var resource = "ecr.permit-number." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

        var result = new Microsoft.Data.SqlClient.SqlParameter("@rc", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        await db.Database.ExecuteSqlRawAsync(
            "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', "
            + "@LockOwner = N'Transaction', @LockTimeout = @timeout;",
            [
                result,
                new Microsoft.Data.SqlClient.SqlParameter("@res", resource),
                new Microsoft.Data.SqlClient.SqlParameter("@timeout", NumberLockTimeoutMs),
            ],
            ct).ConfigureAwait(false);

        // ⚠ Не дочекалися (-1, -2, -3) - та сама відмова 409, що й на вичерпаному очікуванні блокування: нічого не збережено,
        // дію можна повторити. Це НЕ «дубль»: хто тримає замок, ще міг не закомітити.
        if (result.Value is not int rc || rc < 0)
        {
            throw new Application.Errors.ConcurrencyConflictException(
                Domain.Errors.ErrorCodes.SheetBusy,
                "Дозволи з цим номером саме створюються чи змінюються. Нічого не збережено; повторіть дію за мить.",
                new Dictionary<string, object?> { ["messageKey"] = LockWaitGuard.MessageKey });
        }
    }
}
