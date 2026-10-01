// tests/Ecr.Api.Tests/StructureChangeProbe.cs
using Microsoft.Data.SqlClient;

namespace Ecr.Api.Tests;

/// <summary>Читання <c>aud.StructureChange</c> напряму з бази — для доказів аудиту (<c>ФВ-12.10</c>).</summary>
internal static class StructureChangeProbe
{
    /// <summary>Запис журналу: операція, старий і новий стан.</summary>
    internal sealed record Row(string Operation, string? OldJson, string? NewJson, int ChangedByUserId);

    /// <summary>Усі записи по сутності, від найстарішого до найновішого.</summary>
    internal static async Task<List<Row>> ReadAsync(string connectionString, string entityType, int entityId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Operation, OldJson, NewJson, ChangedByUserId FROM aud.StructureChange "
            + "WHERE EntityType = @type AND EntityId = @id ORDER BY Id;";
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@id", entityId);

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new Row(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
        }

        return rows;
    }
}
