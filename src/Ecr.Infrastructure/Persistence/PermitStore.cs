using Ecr.Application.Permits.Dto;
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
    public async Task<PermitUsageDto> FindUsagesAsync(
        long permitId, IReadOnlyCollection<int>? projectIds, int? periodKey, int take, CancellationToken ct)
    {
        var projects = projectIds?.ToList();

        // Шапка: поле Lookup документа з дозволом. Періоду в шапки немає - з фільтром періоду беруться документи, що мають у ньому таблиці.
        var headerQuery =
            from v in db.DocumentHeaderValues.AsNoTracking()
            where v.ValueRegistryEntryId == permitId
            join f in db.HeaderFieldDefs.AsNoTracking() on v.HeaderFieldDefId equals f.Id
            join d in db.Documents.AsNoTracking() on v.DocumentId equals d.Id
            where projects == null || projects.Contains(d.ProjectId)
            where periodKey == null
                  || db.TableInstances.Any(t => t.DocumentId == d.Id && t.PeriodKeyValue == periodKey)
            join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
            select new
            {
                DocumentId = d.Id,
                DocumentKey = d.BusinessKey,
                d.ProjectId,
                ProjectCode = p.Code,
                FieldCode = f.Code,
                FieldLabel = f.LabelL10n,
            };

        // Рядок таблиці: комірка колонки Lookup з дозволом; логічно видалені рядки не рахуються.
        var cellQuery =
            from c in db.CellValues.AsNoTracking()
            where c.ValueRegistryEntryId == permitId && (periodKey == null || c.PeriodKeyValue == periodKey)
            join r in db.TableRows.AsNoTracking()
                on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
            where !r.IsDeleted
            join ti in db.TableInstances.AsNoTracking()
                on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { ti.PeriodKeyValue, ti.Id }
            join d in db.Documents.AsNoTracking() on ti.DocumentId equals d.Id
            where projects == null || projects.Contains(d.ProjectId)
            join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
            join col in db.ColumnDefs.AsNoTracking() on c.ColumnDefId equals col.Id
            join table in db.TableDefs.AsNoTracking() on ti.TableDefId equals table.Id
            join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
            select new
            {
                DocumentId = d.Id,
                DocumentKey = d.BusinessKey,
                d.ProjectId,
                ProjectCode = p.Code,
                PeriodKey = c.PeriodKeyValue,
                SheetCode = sheet.Code,
                SheetName = sheet.NameL10n,
                TableCode = table.Code,
                TableName = table.NameL10n,
                RowKey = r.RowKeyValue,
                RowOrdinal = r.Ordinal,
                FieldCode = col.Code,
                FieldLabel = col.HeaderL10n,
            };

        var headerCount = await headerQuery.CountAsync(ct).ConfigureAwait(false);
        var cellCount = await cellQuery.CountAsync(ct).ConfigureAwait(false);
        var documentCount = await headerQuery.Select(x => x.DocumentId)
            .Union(cellQuery.Select(x => x.DocumentId))
            .CountAsync(ct).ConfigureAwait(false);

        var headers = await headerQuery
            .OrderBy(x => x.DocumentId).ThenBy(x => x.FieldCode)
            .Take(take).ToListAsync(ct).ConfigureAwait(false);
        var cells = await cellQuery
            .OrderBy(x => x.DocumentId).ThenBy(x => x.PeriodKey).ThenBy(x => x.SheetCode).ThenBy(x => x.TableCode)
            .ThenBy(x => x.RowOrdinal).ThenBy(x => x.RowKey).ThenBy(x => x.FieldCode)
            .Take(take).ToListAsync(ct).ConfigureAwait(false);

        static Dictionary<string, string> Copy(Domain.ValueObjects.LocalizedText text)
            => text.Values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var items = headers
            .Select(x => (Order: (x.DocumentId, 0, 0, string.Empty, string.Empty, 0L, x.FieldCode), Item: new PermitUsageItemDto(
                PermitUsageKinds.Header, x.DocumentId, x.DocumentKey, x.ProjectId, x.ProjectCode, null,
                null, null, null, null, null, null, x.FieldCode, Copy(x.FieldLabel))))
            .Concat(cells.Select(x => (Order: (x.DocumentId, 1, x.PeriodKey, x.SheetCode, x.TableCode, (long)x.RowOrdinal, x.FieldCode), Item: new PermitUsageItemDto(
                PermitUsageKinds.Cell, x.DocumentId, x.DocumentKey, x.ProjectId, x.ProjectCode, x.PeriodKey,
                x.SheetCode, Copy(x.SheetName), x.TableCode, Copy(x.TableName), x.RowKey, x.RowOrdinal,
                x.FieldCode, Copy(x.FieldLabel)))))
            .OrderBy(p => p.Order)
            .Take(take)
            .Select(p => p.Item)
            .ToList();

        return new PermitUsageDto(permitId, documentCount, headerCount + cellCount, headerCount + cellCount > items.Count, items);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, int>> CountUsedInDocumentsAsync(
        IReadOnlyCollection<long> permitIds, IReadOnlyCollection<int>? projectIds, CancellationToken ct)
    {
        var permits = permitIds.Distinct().ToList();
        if (permits.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var projects = projectIds?.ToList();

        var fromHeaders = await (
                from v in db.DocumentHeaderValues.AsNoTracking()
                where v.ValueRegistryEntryId != null && permits.Contains(v.ValueRegistryEntryId.Value)
                join d in db.Documents.AsNoTracking() on v.DocumentId equals d.Id
                where projects == null || projects.Contains(d.ProjectId)
                select new { Permit = v.ValueRegistryEntryId!.Value, Document = d.Id })
            .Distinct().ToListAsync(ct).ConfigureAwait(false);

        var fromCells = await (
                from c in db.CellValues.AsNoTracking()
                where c.ValueRegistryEntryId != null && permits.Contains(c.ValueRegistryEntryId.Value)
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where !r.IsDeleted
                join ti in db.TableInstances.AsNoTracking()
                    on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { ti.PeriodKeyValue, ti.Id }
                join d in db.Documents.AsNoTracking() on ti.DocumentId equals d.Id
                where projects == null || projects.Contains(d.ProjectId)
                select new { Permit = c.ValueRegistryEntryId!.Value, Document = d.Id })
            .Distinct().ToListAsync(ct).ConfigureAwait(false);

        return fromHeaders.Concat(fromCells)
            .Distinct()
            .GroupBy(x => x.Permit)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> CountTypeUsedInDocumentsAsync(
        IReadOnlyCollection<int> headerRegistryDefIds, IReadOnlyCollection<int>? projectIds, CancellationToken ct)
    {
        var types = headerRegistryDefIds.Distinct().ToList();
        if (types.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var projects = projectIds?.ToList();

        // Той самий предикат «де обрано значенням», що в CountUsedInDocumentsAsync, але дозвіл → його тип (реєстр шапки).
        var fromHeaders = await (
                from v in db.DocumentHeaderValues.AsNoTracking()
                where v.ValueRegistryEntryId != null
                join e in db.RegistryEntries.AsNoTracking() on v.ValueRegistryEntryId equals e.Id
                where !e.IsDeleted && types.Contains(e.RegistryDefId)
                join d in db.Documents.AsNoTracking() on v.DocumentId equals d.Id
                where projects == null || projects.Contains(d.ProjectId)
                select new { Type = e.RegistryDefId, Document = d.Id })
            .Distinct().ToListAsync(ct).ConfigureAwait(false);

        var fromCells = await (
                from c in db.CellValues.AsNoTracking()
                where c.ValueRegistryEntryId != null
                join e in db.RegistryEntries.AsNoTracking() on c.ValueRegistryEntryId equals e.Id
                where !e.IsDeleted && types.Contains(e.RegistryDefId)
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where !r.IsDeleted
                join ti in db.TableInstances.AsNoTracking()
                    on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { ti.PeriodKeyValue, ti.Id }
                join d in db.Documents.AsNoTracking() on ti.DocumentId equals d.Id
                where projects == null || projects.Contains(d.ProjectId)
                select new { Type = e.RegistryDefId, Document = d.Id })
            .Distinct().ToListAsync(ct).ConfigureAwait(false);

        return fromHeaders.Concat(fromCells)
            .Distinct()
            .GroupBy(x => x.Type)
            .ToDictionary(g => g.Key, g => g.Count());
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
