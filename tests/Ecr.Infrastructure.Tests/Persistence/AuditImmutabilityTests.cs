using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ФВ-5.21 / REQ-CLOSURE №4: журнали <c>aud.*</c> незмінні НА РІВНІ БД —
/// тригери <c>TR_*_Immutable</c> (<c>11-audit-tables.sql</c>) відхиляють
/// UPDATE і DELETE прямим SQL, повз застосунок.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>DROP TRIGGER aud.TR_CellChange_Immutable</c> (або
/// <c>DISABLE TRIGGER</c>) — тести відхилення червоніють; INSERT-тести лишаються
/// зеленими, тобто самі по собі не доводять нічого — доводять саме відхилення.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditImmutabilityTests(SqlServerFixture sql)
{
    private const int ImmutableError = 50060;

    [Theory]
    [InlineData("aud.CellChange", "UPDATE aud.CellChange SET NewValue = N'x' WHERE Id = {0}")]
    [InlineData("aud.CellChange", "DELETE FROM aud.CellChange WHERE Id = {0}")]
    [InlineData("aud.StructureChange", "UPDATE aud.StructureChange SET Operation = N'x' WHERE Id = {0}")]
    [InlineData("aud.StructureChange", "DELETE FROM aud.StructureChange WHERE Id = {0}")]
    [InlineData("aud.SecurityEvent", "UPDATE aud.SecurityEvent SET EventType = N'x' WHERE Id = {0}")]
    [InlineData("aud.SecurityEvent", "DELETE FROM aud.SecurityEvent WHERE Id = {0}")]
    [InlineData("aud.PublicationEvent", "UPDATE aud.PublicationEvent SET ChangeReason = N'x' WHERE Id = {0}")]
    [InlineData("aud.PublicationEvent", "DELETE FROM aud.PublicationEvent WHERE Id = {0}")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task UPDATE_і_DELETE_журналу_аудиту_відхиляються_тригером_і_рядок_лишається(
        string table, string statementTemplate)
    {
        var id = await InsertAsync(table);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, statementTemplate, id)));

        Assert.Equal(ImmutableError, error.Number);
        // Рядок цілий: відхилення відкотило команду, а не лише «повідомило».
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE Id = {id}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task Сеанс_симуляції_закривається_один_раз_але_не_видаляється_і_не_переписується()
    {
        var actor = await CreateUserAsync("sim-actor");
        var subject = await CreateUserAsync("sim-subject");
        var id = await ScalarAsync($"""
            INSERT INTO aud.SimulationSession (ActorUserId, SubjectUserId, Reason, StartedAt)
            OUTPUT INSERTED.Id
            VALUES ({actor}, {subject}, N'перевірка', SYSUTCDATETIME())
            """);

        // Легальний шлях `SimulationService.EndAsync`.
        await ExecuteAsync($"UPDATE aud.SimulationSession SET EndedAt = SYSUTCDATETIME() WHERE Id = {id} AND EndedAt IS NULL");

        // Друге закриття/переписування закритого — відхилено.
        var rewrite = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"UPDATE aud.SimulationSession SET EndedAt = SYSUTCDATETIME() WHERE Id = {id}"));
        Assert.Equal(ImmutableError, rewrite.Number);

        var reason = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"UPDATE aud.SimulationSession SET Reason = N'інша' WHERE Id = {id}"));
        Assert.Equal(ImmutableError, reason.Number);

        var delete = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"DELETE FROM aud.SimulationSession WHERE Id = {id}"));
        Assert.Equal(ImmutableError, delete.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task П_ять_тригерів_незмінності_створені_і_ввімкнені()
    {
        var enabled = await ScalarAsync("""
            SELECT COUNT(*) FROM sys.triggers
            WHERE name IN (N'TR_CellChange_Immutable', N'TR_StructureChange_Immutable',
                           N'TR_SecurityEvent_Immutable', N'TR_PublicationEvent_Immutable',
                           N'TR_SimulationSession_Immutable')
              AND is_disabled = 0
            """);
        Assert.Equal(5, enabled);
    }

    private async Task<long> InsertAsync(string table)
    {
        var insert = table switch
        {
            "aud.CellChange" => "INSERT INTO aud.CellChange (ChangedAt, PeriodKey, TableRowId, ColumnDefId, DocumentId, RowKey, NewValue, ChangedByUserId, Origin) OUTPUT INSERTED.Id VALUES (SYSUTCDATETIME(), 202601, 1, 1, 1, N'R', N'v', 42, N'UserEdit')",
            "aud.StructureChange" => "INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId) OUTPUT INSERTED.Id VALUES (SYSUTCDATETIME(), 1, N'T', 1, 0, N'Add', 42)",
            "aud.SecurityEvent" => "INSERT INTO aud.SecurityEvent (ChangedAt, EventType, ChangedByUserId) OUTPUT INSERTED.Id VALUES (SYSUTCDATETIME(), N'Test', 42)",
            "aud.PublicationEvent" => "INSERT INTO aud.PublicationEvent (ChangedAt, EntityType, EntityId, ChangeReason, ChangedByUserId) OUTPUT INSERTED.Id VALUES (SYSUTCDATETIME(), N'TemplateVersion', 1, N'r', 42)",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };

        // INSERT — легальний шлях: тригери його не чіпають.
        return await ScalarAsync(insert);
    }

    private async Task<int> CreateUserAsync(string name)
    {
        var unique = Guid.NewGuid().ToString("N");
        return (int)await ScalarAsync($"""
            INSERT INTO sec.[User] (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (N'{name}-{unique}', N'{name}', 0, N'S-1-5-21-{unique}', N'{unique}', SYSUTCDATETIME())
            """);
    }

    private async Task ExecuteAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
