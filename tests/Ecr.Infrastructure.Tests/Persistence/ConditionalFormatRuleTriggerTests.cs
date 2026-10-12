using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ФВ-2.7: правила умовного форматування — частина структури версії, тригер
/// <c>TR_ConditionalFormatRule_Immutable</c> тримає їх незмінними у
/// Published/Deprecated (прямий SQL повз застосунок, як
/// <see cref="FrozenVersionTriggerTests"/>); чернетка редагується вільно.
/// </summary>
[Collection("SqlServer")]
public sealed class ConditionalFormatRuleTriggerTests(SqlServerFixture sql)
{
    private const int UpdateOrDelete = 50003;
    private const int InsertIntoFrozen = 50004;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Чернетка_приймає_вставку_правку_і_видалення_правила()
    {
        var (versionId, _) = await BuildAsync();
        var id = await InsertAsync(versionId);

        await ExecuteAsync($"UPDATE cfg.ConditionalFormatRule SET Operator = N'lt' WHERE Id = {id}");
        await ExecuteAsync($"DELETE FROM cfg.ConditionalFormatRule WHERE Id = {id}");

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM cfg.ConditionalFormatRule WHERE Id = {id}"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Заморожена_версія_відхиляє_вставку_правку_і_видалення(int status)
    {
        var (versionId, _) = await BuildAsync();
        var id = await InsertAsync(versionId);
        await ExecuteAsync($"UPDATE cfg.TemplateVersion SET Status = {status}, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 WHERE Id = {versionId}");

        var insert = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(versionId, ordinal: 2));
        Assert.Equal(InsertIntoFrozen, insert.Number);
        Assert.Contains("structurallyFrozen", insert.Message, StringComparison.Ordinal);

        var update = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"UPDATE cfg.ConditionalFormatRule SET Operator = N'lt' WHERE Id = {id}"));
        Assert.Equal(UpdateOrDelete, update.Number);

        var delete = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"DELETE FROM cfg.ConditionalFormatRule WHERE Id = {id}"));
        Assert.Equal(UpdateOrDelete, delete.Number);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Невідомий_оператор_відхиляє_CHECK()
    {
        var (versionId, _) = await BuildAsync();

        await Assert.ThrowsAsync<SqlException>(() => InsertAsync(versionId, op: "bogus"));
    }

    [Theory]
    [InlineData("N'COL', 1, N'gt', N'1', N'row', NULL, N'[\"R1\"]'")]    // область «рядки» без таблиці
    [InlineData("N'COL', 2, N'gt', N'1', N'row', N'T1', NULL")]          // область «рядки» без ключів
    [InlineData("N'COL', 3, N'gt', N'1', N'column', N'T1', N'[\"R1\"]'")] // ключі без області «рядки»
    [InlineData("N'*', 4, N'gt', N'1', N'column', NULL, NULL")]          // «усі колонки» без таблиці
    [InlineData("N'COL', 5, N'gt', N'1', N'page', NULL, NULL")]          // невідома область
    [InlineData("N'COL', 6, N'textEq', N'x', N'column', NULL, NULL, N'dots'")] // невідомий штрих
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task L6_CHECK_тримає_інваріанти_області_правила_мимо_конструктора_домену(string values)
    {
        var (versionId, _) = await BuildAsync();
        var pattern = values.Contains("dots", StringComparison.Ordinal);

        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            "INSERT INTO cfg.ConditionalFormatRule "
            + "(TemplateVersionId, ColumnCode, Ordinal, Operator, Value, Scope, TableCode, RowKeys, IsBold"
            + (pattern ? ", Pattern" : string.Empty) + ") "
            + $"SELECT {versionId}, {values.Replace(", N'dots'", string.Empty, StringComparison.Ordinal)}, 0"
            + (pattern ? ", N'dots'" : string.Empty)));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task L6_правило_області_рядків_з_таблицею_і_ключами_приймається()
    {
        var (versionId, _) = await BuildAsync();

        await ExecuteAsync(
            "INSERT INTO cfg.ConditionalFormatRule "
            + "(TemplateVersionId, ColumnCode, Ordinal, Operator, Value, Scope, TableCode, RowKeys, IsBold) "
            + $"VALUES ({versionId}, N'*', 1, N'textEq', N'Removed', N'row', N'T1', N'[\"R1\"]', 0)");

        Assert.Equal(1, await ScalarAsync(
            $"SELECT COUNT(*) FROM cfg.ConditionalFormatRule WHERE TemplateVersionId = {versionId} AND Scope = N'row'"));
    }

    private async Task<(int VersionId, int Unused)> BuildAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        return (doc.TemplateVersionId, 0);
    }

    private Task<int> InsertAsync(int versionId, int ordinal = 1, string op = "gt")
        => ScalarAsync($"""
            INSERT INTO cfg.ConditionalFormatRule (TemplateVersionId, ColumnCode, Ordinal, Operator, Value, BackgroundHex, IsBold)
            VALUES ({versionId}, N'COL', {ordinal}, N'{op}', N'10', '#ff0000', 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}