using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання <c>aud.CellChange</c> прямим ADO.</summary>
/// <remarks>
/// ⚠ Таблиці <c>aud.*</c> живуть поза моделлю EF: журнал append-only, і
/// обліковий запис застосунку має на ньому лише <c>INSERT</c> і <c>SELECT</c>.
/// Тримати їх у <c>DbSet</c> означало б дати трекеру змін можливість, якої в
/// нього немає в базі, — і виявилося б це на першому <c>SaveChanges</c>.
/// </remarks>
public sealed class AuditReader(EcrDbContext db) : IAuditReader
{
    /// <inheritdoc />
    public async Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
        DateTime from, DateTime to, long? documentId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Вікно за ChangedAt стоїть ПЕРШИМ у WHERE не заради стилю: саме воно
        // відсікає партиції. Курсор за Id додається до нього, а не замість —
        // інакше сторінка 20 читала б журнал цілком.
        command.CommandText = $"""
            SELECT TOP (@take)
                   Id, ChangedAt, PeriodKey, DocumentId, RowKey, ColumnDefId,
                   OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit
              FROM aud.CellChange
             WHERE ChangedAt >= @from AND ChangedAt < @to
                   AND Id > @after
                   {(documentId is null ? string.Empty : "AND DocumentId = @documentId")}
             ORDER BY Id;
            """;

        command.Parameters.AddWithValue("@take", page.Limit + 1);
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@to", to);
        command.Parameters.AddWithValue("@after", Cursor.Decode(page.Cursor));
        if (documentId is not null)
        {
            command.Parameters.AddWithValue("@documentId", documentId.Value);
        }

        var rows = new List<(long Id, CellChangeView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    new CellChangeView(
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),

                        // Автор — UserId, не SID: у локального користувача SID
                        // не існує взагалі (R-A2, D-86).
                        reader.GetInt32(8),
                        reader.GetString(9),
                        reader.GetBoolean(10))));
            }
        }

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(r => r.View).ToList();

        return new PagedResult<CellChangeView>(
            items,
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,

            // Підрахунок по вікну аудиту дорогий і нікому не потрібен: аудитор
            // гортає, а не рахує (конвенція API — TotalCount може бути null).
            TotalCount: null);
    }

    /// <summary>Формат дати для повідомлень; не для запитів.</summary>
    internal static string Format(DateTime moment)
        => moment.ToString("O", CultureInfo.InvariantCulture);
}
