// tests/Ecr.Infrastructure.Tests/Persistence/FrozenVersionTriggerBulkTests.cs
using System.Diagnostics;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// W15z: тригери незмінності (<c>10-triggers.sql</c>) на БАГАТОРЯДКОВІЙ команді. Замір на копії живої бази 23.11:
/// UPDATE 300 колонок однією командою — 173 с (без тригера 0,3 с): перевірки з'єднували inserted × deleted без індексів.
/// Тепер чернетка — швидкий вихід, заморожена версія — копії з кластерним індексом; семантика та сама
/// (<see cref="FrozenVersionTriggerTests"/>).
/// </summary>
/// <remarks>
/// ⚠ Червоне ДО фіксу на малій тестовій базі не відтворюється (старий тригер: 3000 рядків ~ секунди): квадратичний план
/// з'являється на обсязі живої бази. Доказ «до/після» - замір на копії живої (EcrLandBld 23.11): 300 рядків 173 с -> 2,2 с,
/// а клон LAND_UI з перев'язкою набором - таймаут 60 с -> 2,5 хв без таймауту. Тут - семантика багаторядкових команд
/// (заморожена версія так само відхиляє, чернетка пропускає) і межа часу як храповик.
/// </remarks>
[Collection("SqlServer")]
public sealed class FrozenVersionTriggerBulkTests(SqlServerFixture sql)
{
    private const int Columns = 3000;
    private const int CommandSeconds = 120;

    // Межа чернетки: новий тригер - один прохід (локально ~0,3 с на 3000 рядків); старий - квадратичний (локально ~1 хв).
    private static readonly TimeSpan DraftBudget = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Багаторядковий_UPDATE_колонок_лінійний_а_заморожена_версія_так_само_відхиляє_структурну_зміну()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var tag = Guid.NewGuid().ToString("N")[..8];

        // Чернетка: 3000 колонок однією вставкою (INSERT тригер і до фіксу дешевий — deleted порожня).
        await ExecuteAsync($$"""
            INSERT INTO cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType)
            SELECT TOP ({{Columns}}) {{doc.TableDefId}}, CONCAT(N'B{{tag}}_', n), N'{"en":"bulk"}', 100 + n, 2
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
            """);

        // Чернетка: одна команда на всі колонки (як перев'язка клону й Ordinal збирача) — у межах таймауту.
        var draft = Stopwatch.StartNew();
        var updated = await ExecuteAsync($"UPDATE cfg.ColumnDef SET WidthPx = 120, Ordinal = Ordinal + 1 WHERE TableDefId = {doc.TableDefId}");
        draft.Stop();
        Assert.Equal(Columns + 1, updated);

        // Заморожена версія: презентаційна зміна всіх колонок проходить, структурна — 50001, як і для одного рядка.
        await ExecuteAsync(
            $"UPDATE cfg.TemplateVersion SET Status = 1, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 WHERE Id = {doc.TemplateVersionId}");
        var frozen = Stopwatch.StartNew();
        Assert.Equal(Columns + 1, await ExecuteAsync($"UPDATE cfg.ColumnDef SET WidthPx = 140 WHERE TableDefId = {doc.TableDefId}"));
        frozen.Stop();
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = 3 WHERE TableDefId = {doc.TableDefId}"));
        Assert.Equal(50001, error.Number);
        var insert = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $$"""INSERT INTO cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType) VALUES ({{doc.TableDefId}}, N'X{{tag}}', N'{"en":"x"}', 9999, 2)"""));
        Assert.Equal(50004, insert.Number);

        Assert.True(draft.Elapsed < DraftBudget, $"чернетка: {draft.Elapsed} (межа {DraftBudget})");
        Assert.True(frozen.Elapsed < TimeSpan.FromSeconds(CommandSeconds), $"заморожена: {frozen.Elapsed}");
    }

    private async Task<int> ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.CommandTimeout = CommandSeconds;
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
