// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.Quarterly.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// C-10 (аудит 2026-11, рішення PO 11.11): у квартальному проєкті колонка БЕЗ місяця ділиться за ПЕРІОДОМ комірки
/// (квартал цілими календарними місяцями), а не за «місяцем» із <c>PeriodKey.Sequence</c>. Ключ частин — <c>PeriodKey</c>
/// комірки. Місячні проєкти лишаються побайтно як були (їх доводять решта частин цього класу без змін очікувань).
/// </summary>
/// <remarks>
/// Червоне до C-10: зміна шапки з 15.02 у Q1 (202601 = 01.01–31.03) не вимагає поділу (одиниця — «січень» за Sequence = 1) —
/// введення числа приймається одним значенням, 4225 немає.
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C-10")]
    public async Task У_кварталі_зміна_дозволу_усередині_періоду_ділить_комірку_цілого_кварталу()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        await MakeQuarterAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var added = await AddHeaderAsync(client, s, s.FebId, "2026-02-15").ConfigureAwait(true);
        Assert.True(added.StatusCode == HttpStatusCode.OK, $"{added.StatusCode}: {await added.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        // Історія: одиниця — увесь квартал (monthTo — останній день), а не «місяць».
        var history = JsonDocument.Parse(await (await client.GetAsync(new Uri($"{Base(s)}?periodKey=202601", UriKind.Relative)).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(1, history.GetProperty("cellsNeedingSplit").GetInt32());
        var unit = history.GetProperty("rows")[0].GetProperty("splitMonths")[0];
        Assert.Equal("2026-01-01", unit.GetProperty("month").GetString());
        Assert.Equal("2026-03-31", unit.GetProperty("monthTo").GetString());
        var spans = unit.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(("2026-01-01", "2026-02-14"), (spans[0].GetProperty("dayFrom").GetString(), spans[0].GetProperty("dayTo").GetString()));
        Assert.Equal(("2026-02-15", "2026-03-31"), (spans[1].GetProperty("dayFrom").GetString(), spans[1].GetProperty("dayTo").GetString()));

        // Одне значення — 422 4225 з підписом періоду.
        var single = await PatchCellAsync(client, s, 20m).ConfigureAwait(true);
        var singleBody = await single.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(single.StatusCode == HttpStatusCode.UnprocessableEntity, $"{single.StatusCode}: {singleBody}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(singleBody).RootElement;
        Assert.Equal("ECR-CELL-4225", problem.GetProperty("errorCode").GetString());
        Assert.Equal("01.2026–03.2026", problem.GetProperty("month").GetString());
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));

        // Подання заблоковане, поки комірку не поділено.
        Assert.Contains(await FindingsAsync(client, s).ConfigureAwait(true), f => f.Rule == "PERMIT-SPLIT" && f.Column == s.DataColumnCode);

        // Поділ: дві частини за ключем PeriodKey комірки, дні — 01.01–14.02 і 15.02–31.03, сума записана.
        var split = await SplitAsync(client, s, 5m, 7m).ConfigureAwait(true);
        Assert.True(split.StatusCode == HttpStatusCode.OK, $"{split.StatusCode}: {await split.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
        Assert.Equal(
            [(202601, 1, "2026-01-01", "2026-02-14", 5m), (202601, 2, "2026-02-15", "2026-03-31", 7m)],
            await PartsAsync(s).ConfigureAwait(true));
        Assert.DoesNotContain(await FindingsAsync(client, s).ConfigureAwait(true), f => f.Rule == "PERMIT-SPLIT");

        // Очищення комірки забирає частини (ключ — PeriodKey).
        var cleared = await PatchCellAsync(client, s, null).ConfigureAwait(true);
        Assert.True(cleared.StatusCode == HttpStatusCode.OK, $"{cleared.StatusCode}: {await cleared.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Empty(await PartsAsync(s).ConfigureAwait(true));
    }

    /// <summary>Зміна дозволу ПОЗА кварталом (15.05) Q1 не ділить: значення приймається одним числом, PERMIT-SPLIT немає.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C-10")]
    public async Task У_кварталі_зміна_дозволу_поза_періодом_не_вимагає_поділу()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        await MakeQuarterAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var added = await AddHeaderAsync(client, s, s.FebId, "2026-05-15").ConfigureAwait(true);
        Assert.True(added.StatusCode == HttpStatusCode.OK, $"{added.StatusCode}: {await added.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var history = JsonDocument.Parse(await (await client.GetAsync(new Uri($"{Base(s)}?periodKey=202601", UriKind.Relative)).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(0, history.GetProperty("cellsNeedingSplit").GetInt32());
        Assert.Empty(history.GetProperty("rows").EnumerateArray());

        var single = await PatchCellAsync(client, s, 20m).ConfigureAwait(true);
        Assert.True(single.StatusCode == HttpStatusCode.OK, $"{single.StatusCode}: {await single.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Equal(20m, await CellAsync(s).ConfigureAwait(true));
        Assert.DoesNotContain(await FindingsAsync(client, s).ConfigureAwait(true), f => f.Rule == "PERMIT-SPLIT");
    }

    /// <summary>Період 202601 стає кварталом 01.01–31.03 (межі беруться з <c>doc.Period</c>).</summary>
    private async Task MakeQuarterAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE p SET PeriodEnd = '2026-03-31'
              FROM doc.Period AS p JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
             WHERE d.Id = @id AND p.PeriodKey = 202601;
            """;
        command.Parameters.AddWithValue("@id", s.DocumentId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> PatchCellAsync(HttpClient client, Scenario s, decimal? value)
        => await client.PatchAsJsonAsync(
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
                        baseVersion = await RowVersionAsync(s).ConfigureAwait(false),
                        cells = new[] { new { columnCode = s.DataColumnCode, value = (object?)value } },
                    },
                },
            }).ConfigureAwait(false);

    private async Task<List<(int Key, int PartNo, string From, string To, decimal Value)>> PartsAsync(Scenario s)
    {
        var list = new List<(int, int, string, string, decimal)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PeriodKey, PartNo, DayFrom, DayTo, ValueNumeric FROM doc.CellPermitPart WHERE DocumentId = @id ORDER BY PartNo;";
        command.Parameters.AddWithValue("@id", s.DocumentId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add((
                reader.GetInt32(0), reader.GetByte(1), reader.GetDateTime(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDateTime(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetDecimal(4)));
        }

        return list;
    }
}
