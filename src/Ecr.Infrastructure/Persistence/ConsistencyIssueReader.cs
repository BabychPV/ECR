using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання <c>aud.ConsistencyIssue</c> прямим ADO.</summary>
/// <remarks>
/// ⚠ Та сама причина, що й у <see cref="AuditReader"/>: таблиці <c>aud.*</c>
/// живуть поза моделлю EF (журнал append-only, обліковий запис застосунку має
/// на ньому лише <c>INSERT</c> і <c>SELECT</c>). Сама задача
/// <c>ConsistencyCheckJob</c> пише туди теж сирим <c>MERGE</c>, не через
/// <c>DbSet</c>, — читач мусить бути з нею з одного боку межі.
/// </remarks>
public sealed class ConsistencyIssueReader(EcrDbContext db) : IConsistencyIssueReader
{
    /// <inheritdoc />
    public async Task<PagedResult<ConsistencyIssueView>> ReadIssuesAsync(
        string? ruleCode, bool openOnly, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Курсор іде ВНИЗ (`Id < @before`), бо й порядок спадний: журнал
        // читають із кінця — «що знайшлося цієї ночі», а не «що знайшлося
        // першого дня». Початок відліку — `long.MaxValue`, а не 0: із нулем
        // перша сторінка була б порожня завжди.
        //
        // ⚠ `TOP (@take)` бере на один більше за розмір сторінки — рівно так
        // само, як `AuditReader`: це і є ознака «є ще», без окремого COUNT.
        command.CommandText = $"""
            SELECT TOP (@take)
                   Id, DetectedAt, Severity, RuleCode, EntityType, EntityId,
                   Message, ResolvedAt, ResolvedByUserId
              FROM aud.ConsistencyIssue
             WHERE Id < @before
                   {(ruleCode is null ? string.Empty : "AND RuleCode = @ruleCode")}
                   {(openOnly ? "AND ResolvedAt IS NULL" : string.Empty)}
             ORDER BY Id DESC;
            """;

        command.Parameters.AddWithValue("@take", page.Limit + 1);
        command.Parameters.AddWithValue("@before", DecodeBefore(page.Cursor));
        if (ruleCode is not null)
        {
            command.Parameters.AddWithValue("@ruleCode", ruleCode);
        }

        var rows = new List<(long Id, ConsistencyIssueView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);

                rows.Add((
                    id,
                    new ConsistencyIssueView(
                        id,
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetByte(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetString(6),
                        reader.IsDBNull(7)
                            ? null
                            : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8))));
            }
        }

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(r => r.View).ToList();

        return new PagedResult<ConsistencyIssueView>(
            items,
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,

            // Підрахунок тут дорогий і нікому не потрібен: адміністратор
            // дивиться, ЩО саме зламано, а скільки всього — уже показує
            // лічильник `ecr.consistency.issues` за типом знахідки.
            TotalCount: null);
    }

    /// <summary>
    /// Межа спадного курсора: <c>long.MaxValue</c> на першій сторінці.
    /// </summary>
    /// <remarks>
    /// ⛔ <see cref="Cursor.Decode"/> віддає <c>0</c> і на порожньому, і на
    /// зіпсованому курсорі — це правильно для ЗРОСТАЮЧОГО порядку, де нуль
    /// означає «з початку». Тут порядок спадний, і нуль означав би «раніше за
    /// найперший рядок», тобто порожню відповідь назавжди. Перетворення живе
    /// в одному місці саме тому, що помилка мовчазна: сторінка просто порожня.
    /// </remarks>
    private static long DecodeBefore(string? cursor)
    {
        var decoded = Cursor.Decode(cursor);

        return decoded == 0 ? long.MaxValue : decoded;
    }
}
