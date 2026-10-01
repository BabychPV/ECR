using System.Net;
using System.Text;
using System.Text.Json;
using Ecr.Bootstrap.Excel;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>Сервер-заглушка: записує запити і відповідає за правилами тесту.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage?>? respond = null) : HttpMessageHandler
{
    private int _nextId = 100;

    public List<(string Method, string Path, JsonElement? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path, body is null ? null : JsonDocument.Parse(body).RootElement.Clone()));

        if (respond?.Invoke(request, body) is { } custom)
        {
            return custom;
        }

        var json = path switch
        {
            "/api/v1/units" => """[{"id":1,"code":"t"},{"id":7,"code":"g_per_s"},{"id":9,"code":"t_per_year"}]""",
            "/api/v1/templates" => """{"templateId":5}""",
            _ when path.EndsWith("/versions", StringComparison.Ordinal) => """{"versionId":42}""",
            _ => $$"""{"id":{{_nextId++}}}""",
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Запис плану в чернетку через API застосунку.</summary>
/// <remarks>
/// ⛔ Мутаційні точки: переставити цикли рядків і колонок нічого не зламає, а
/// ось запис рядка-нащадка перед батьком — зламає: сервер не знайде
/// <c>ParentRowKey</c>. Тому <see cref="Порядок_запису_аркуш_таблиця_рядки_колонки_формула"/>
/// звіряє саме послідовність. Прибери <c>units[column.UnitCode]</c> — червоніє
/// перевірка <c>unitId</c>.
/// </remarks>
public sealed class TemplateApiWriterTests
{
    private static StructurePlan Plan(string? unit = "g_per_s") => new(
        "x.xlsx",
        [
            new PlannedSheet("S", "Аркуш", 1,
            [
                new PlannedTable(
                    "T", "Таблиця", 1, "Аркуш!A1:C4", TableOrigin.ExcelTable, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed,
                    [
                        new PlannedColumn("P", "Потужність", 1, CellDataType.Decimal, 3, unit, false, null),
                        new PlannedColumn("V", "Викид", 2, CellDataType.Formula, null, null, true, "[P]*2"),
                    ],
                    [
                        new PlannedRow("G", "Цех", 1, RowKind.Group, null),
                        new PlannedRow("I", "Труба", 2, RowKind.Item, "G"),
                    ]),
            ]),
        ]);

    private static readonly ApplyTarget NewTemplate = new(null, "TPL", "Шаблон", "1.0.0.0", "ru");

    [Fact]
    public async Task Порядок_запису_аркуш_таблиця_рядки_колонки_формула()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ecr.test/") };
        var writer = new TemplateApiWriter(http);

        var units = await writer.GetUnitsAsync(CancellationToken.None);
        var result = await writer.ApplyAsync(Plan(), NewTemplate, units, CancellationToken.None);

        Assert.Equal(5, result.TemplateId);
        Assert.Equal(42, result.VersionId);
        Assert.Equal(
            [
                "GET /api/v1/units",
                "POST /api/v1/templates",
                "POST /api/v1/templates/5/versions",
                "PUT /api/v1/template-versions/42/sheets/S",
                "PUT /api/v1/template-versions/42/sheets/S/tables/T",
                "PUT /api/v1/template-versions/42/tables/101/rows/G",
                "PUT /api/v1/template-versions/42/tables/101/rows/I",
                "PUT /api/v1/template-versions/42/tables/101/columns/P",
                "PUT /api/v1/template-versions/42/tables/101/columns/V",
                "PUT /api/v1/template-versions/42/tables/101/formulas/column/105",
            ],
            handler.Requests.Select(r => $"{r.Method} {r.Path}"));
    }

    [Fact]
    public async Task Тіла_запитів_несуть_план_у_формі_контракту()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ecr.test/") };
        var writer = new TemplateApiWriter(http);

        await writer.ApplyAsync(Plan(), NewTemplate, new Dictionary<string, int> { ["g_per_s"] = 7 }, CancellationToken.None);

        JsonElement Body(string suffix) => handler.Requests.Single(r => r.Path.EndsWith(suffix, StringComparison.Ordinal)).Body!.Value;

        Assert.Equal("1.0.0.0", Body("/versions").GetProperty("versionNumber").GetString());
        Assert.Equal("Таблиця", Body("/tables/T").GetProperty("nameL10n").GetProperty("ru").GetString());
        Assert.Equal("Fixed", Body("/tables/T").GetProperty("rowMode").GetString());
        Assert.Equal("G", Body("/rows/I").GetProperty("parentRowKey").GetString());
        Assert.Equal("Group", Body("/rows/G").GetProperty("rowKind").GetString());

        var column = Body("/columns/P");
        Assert.Equal(7, column.GetProperty("unitId").GetInt32());
        Assert.Equal(3, column.GetProperty("scale").GetInt32());
        Assert.Equal("Decimal", column.GetProperty("dataType").GetString());

        var formula = Body("/formulas/column/105");
        Assert.Equal("[P]*2", formula.GetProperty("expression").GetString());
        Assert.Equal("Template", formula.GetProperty("dialect").GetString());
    }

    [Fact]
    public async Task Наявний_шаблон_не_створюється_вдруге()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ecr.test/") };

        var result = await new TemplateApiWriter(http).ApplyAsync(
            Plan(unit: null), new ApplyTarget(9, null, null, "2.0.0.0", "ru"), new Dictionary<string, int>(), CancellationToken.None);

        Assert.Equal(9, result.TemplateId);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/v1/templates");
        Assert.Equal("/api/v1/templates/9/versions", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Відмова_сервера_зупиняє_запис_і_називає_крок_і_чернетку()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/columns/V", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"code":"ECR-TMPL-0422"}""") }
                : null);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ecr.test/") };

        var ex = await Assert.ThrowsAsync<ApplyFailedException>(() => new TemplateApiWriter(http)
            .ApplyAsync(Plan(), NewTemplate, new Dictionary<string, int> { ["g_per_s"] = 7 }, CancellationToken.None));

        Assert.Equal(42, ex.VersionId);
        Assert.Contains("колонка T.V", ex.Message, StringComparison.Ordinal);
        Assert.Contains("422", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ECR-TMPL-0422", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("/formulas/", StringComparison.Ordinal));
    }

    [Fact]
    public void Одиниця_якої_немає_в_каталозі_сервера_помилка_звіту()
    {
        var report = new ImportReport();

        TemplateApiWriter.CheckUnits(Plan("kg_per_TJ"), new Dictionary<string, int> { ["t"] = 1 }, report);

        var error = Assert.Single(report.Issues);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Equal("S.T.P", error.Location);
        Assert.Contains("kg_per_TJ", error.Message, StringComparison.Ordinal);
    }
}
