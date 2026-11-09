// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.Split.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Feature (j) P2: split storage (j2), the j3 refusal of one value in a shared month (ECR-CELL-4225), the PERMIT-SPLIT
/// finding that blocks Submit, the split audit (j6) and the history-aware validity rule.
/// </summary>
/// <remarks>
/// Red proofs (local mutations): without <c>EnsureSharedMonthsAreSplitAsync</c> the single value is saved (200); without
/// <c>HistoryFindings</c> no PERMIT-SPLIT finding and the YEAR permit alone covers January (no PERMIT-VALIDITY).
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    /// <summary>j3: one value typed into January, which the row shares between YEAR and MIDJAN, is refused (ECR-CELL-4225).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j3")]
    public async Task A_single_value_in_a_shared_month_is_refused_with_split_required()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.TableInstanceId,
                periodKey = 202601,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = await RowVersionAsync(s).ConfigureAwait(true),
                        cells = new[] { new { columnCode = s.DataColumnCode, value = (object)20m } },
                    },
                },
            }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CELL-4225", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CELL-4225.splitRequired", problem.GetProperty("messageKey").GetString());
        Assert.Contains("KZ-YEAR / KZ-MIDJAN", problem.GetProperty("detail").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
    }

    /// <summary>j2/j3/j6: the split stores both parts, writes the total, is audited and clears the PERMIT-SPLIT finding.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j2")]
    public async Task Splitting_the_month_stores_both_parts_writes_the_sum_and_clears_the_finding()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        // Before: the unsplit January is an Error finding at the cell (blocks Submit).
        Assert.Contains(
            await FindingsAsync(client, s).ConfigureAwait(true),
            f => f.Rule == "PERMIT-SPLIT" && f.Severity == "Error" && f.Column == s.DataColumnCode);

        // Partial save: part B empty — the old single value stays, still unsplit.
        var partial = await SplitAsync(client, s, 5m, null).ConfigureAwait(true);
        Assert.True(partial.StatusCode == HttpStatusCode.OK, await partial.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));

        var full = await SplitAsync(client, s, 5.5m, 7.25m).ConfigureAwait(true);
        var body = await full.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(full.StatusCode == HttpStatusCode.OK, $"{full.StatusCode}: {body}\n{app.ErrorsText}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, root.GetProperty("cellsNeedingSplit").GetInt32());
        Assert.Equal(12.75m, await CellAsync(s).ConfigureAwait(true));

        var cell = root.GetProperty("rows")[0].GetProperty("splitMonths")[0].GetProperty("cells")[0];
        Assert.True(cell.GetProperty("isSplit").GetBoolean());
        // ⚠ Decimals travel as strings in this API (precision of decimal(34,16)).
        Assert.Equal(
            [5.5m, 7.25m],
            cell.GetProperty("parts").EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String
                ? decimal.Parse(p.GetString()!, CultureInfo.InvariantCulture)
                : p.GetDecimal()));

        var audit = await AuditAsync(s, "Split", "CellPermitPart").ConfigureAwait(true);
        Assert.Contains("\"permit\":\"KZ-YEAR\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"permit\":\"KZ-MIDJAN\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"from\":\"2026-01-15\"", audit, StringComparison.Ordinal);

        Assert.DoesNotContain(await FindingsAsync(client, s).ConfigureAwait(true), f => f.Rule == "PERMIT-SPLIT");
    }

    /// <summary>
    /// The rule is history-aware: once MIDJAN is in force from 15.01, its own validity answers for its days. Made invalid
    /// in January afterwards (registry edit), January is reported naming MIDJAN — the base YEAR alone would cover it.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j4")]
    public async Task The_validity_rule_checks_each_permit_in_force_on_its_own_days()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        await ExecAsync("UPDATE dic.RegistryEntry SET ValidFrom = '2026-03-01' WHERE Id = @id;", s.MidJanId).ConfigureAwait(true);

        var findings = await FindingsAsync(client, s).ConfigureAwait(true);
        var finding = Assert.Single(findings, f => f.Rule == "PERMIT-VALIDITY" && f.Severity == "Error");
        Assert.Contains("KZ-MIDJAN", finding.Message, StringComparison.Ordinal);
        Assert.Contains("01.2026", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coordinator 09.11 (no debt): total and parts are ONE transaction — the parts write failing (a test trigger) leaves
    /// the old total. Red before the fix: the total was written in its own transaction and stayed 12.75 without parts.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j2")]
    public async Task A_failing_parts_write_leaves_the_total_unchanged()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        var trigger = $"TR_TestFailParts_{s.DocumentId.ToString(CultureInfo.InvariantCulture)}";
        await ExecAsync(
            $"CREATE TRIGGER doc.{trigger} ON doc.CellPermitPart AFTER INSERT AS "
            + $"IF EXISTS (SELECT 1 FROM inserted WHERE DocumentId = {s.DocumentId.ToString(CultureInfo.InvariantCulture)}) "
            + "THROW 50999, N'test: parts write fails', 1;",
            0).ConfigureAwait(true);
        try
        {
            var response = await SplitAsync(client, s, 5.5m, 7.25m).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await ExecAsync($"DROP TRIGGER doc.{trigger};", 0).ConfigureAwait(true);
        }

        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await PartCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Clearing a split month cell takes its parts with it (same transaction). Red without the clean-up: 2 parts left.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j2")]
    public async Task Clearing_a_split_cell_deletes_its_parts()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SplitAsync(client, s, 5.5m, 7.25m).ConfigureAwait(true)).StatusCode);
        Assert.Equal(2, await PartCountAsync(s).ConfigureAwait(true));

        var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.TableInstanceId,
                periodKey = 202601,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = await RowVersionAsync(s).ConfigureAwait(true),
                        cells = new[] { new { columnCode = s.DataColumnCode, value = (object?)null } },
                    },
                },
            }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Null(await CellAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await PartCountAsync(s).ConfigureAwait(true));
    }

    private async Task<int> PartCountAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM doc.CellPermitPart WHERE DocumentId = @id;";
        command.Parameters.AddWithValue("@id", s.DocumentId);
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SplitAsync(HttpClient client, Scenario s, decimal? a, decimal? b)
        => await client.PutAsJsonAsync(
            new Uri($"{Base(s)}/splits", UriKind.Relative),
            new
            {
                periodKey = 202601,
                items = new[]
                {
                    new
                    {
                        tableInstanceId = s.TableInstanceId,
                        rowKey = s.RowKey,
                        columnCode = s.DataColumnCode,
                        baseVersion = await RowVersionAsync(s).ConfigureAwait(false),
                        parts = new[] { a, b },
                    },
                },
            }).ConfigureAwait(false);

    private static async Task<List<(string Rule, string Severity, string? Column, string Message)>> FindingsAsync(
        HttpClient client, Scenario s)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/validate", UriKind.Relative),
            new { periodKey = 202601 }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"validate: {response.StatusCode}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Select(f => (
            f.GetProperty("ruleCode").GetString() ?? string.Empty,
            f.GetProperty("severity").GetString() ?? string.Empty,
            f.TryGetProperty("columnCode", out var c) ? c.GetString() : null,
            f.GetProperty("message").GetString() ?? string.Empty))];
    }

    private async Task<string> RowVersionAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RowVersion FROM doc.TableRow WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", s.RowId);
        return Convert.ToBase64String((byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
    }

    private async Task<decimal?> CellAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ValueNumeric FROM doc.CellValue WHERE TableRowId = @row AND ColumnDefId = @col;";
        command.Parameters.AddWithValue("@row", s.RowId);
        command.Parameters.AddWithValue("@col", s.DataColumnId);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as decimal?;
    }

    private async Task ExecAsync(string text, long id)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = text;

        // ⚠ No parameter for DDL: a parameterized command runs via sp_executesql, where CREATE TRIGGER is a syntax error.
        if (text.Contains("@id", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("@id", id);
        }
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
