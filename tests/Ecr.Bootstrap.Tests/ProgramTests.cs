using ClosedXML.Excel;
using Ecr.Bootstrap.Excel;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>
/// Інструмент цілком: сухий прогін нічого не пише, звіт з помилками забороняє запис.
/// </summary>
/// <remarks>
/// ⛔ Мутаційна точка: прибери в <c>RunAsync</c> гілку <c>report.HasErrors</c>
/// перед записом — і <see cref="Помилки_у_звіті_забороняють_запис"/> червоніє:
/// сервер отримав би запити за книгою, яку сам звіт назвав непридатною.
/// </remarks>
public sealed class ProgramTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ecr-bootstrap-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Workbook(Action<XLWorkbook> build)
    {
        var path = Path.Combine(_dir, "book.xlsx");
        using var wb = new XLWorkbook();
        build(wb);
        wb.SaveAs(path);
        return path;
    }

    private static void GoodSheet(XLWorkbook wb)
    {
        var ws = wb.AddWorksheet("Викиди");
        ws.Cell("A1").Value = "Речовина";
        ws.Cell("B1").Value = "Викид (т)";
        ws.Cell("A2").Value = "Пил";
    }

    private CommandLineOptions Options(string workbook, params string[] extra)
    {
        var (options, error) = CommandLine.Parse(["--workbook", workbook, "--out-report", Path.Combine(_dir, "report.md"), .. extra]);
        Assert.Null(error);
        return options!;
    }

    [Fact]
    public async Task Сухий_прогін_пише_звіт_і_не_звертається_до_сервера()
    {
        var handler = new RecordingHandler();
        var options = Options(Workbook(GoodSheet), "--out-plan", Path.Combine(_dir, "plan.json"));
        using var log = new StringWriter();

        var code = await Program.RunAsync(options, log, CancellationToken.None, handler);

        Assert.Equal(0, code);
        Assert.Empty(handler.Requests);
        var report = await File.ReadAllTextAsync(options.ReportPath);
        Assert.Contains("сухий прогін; нічого не записано", report, StringComparison.Ordinal);
        Assert.Contains("- [ ] **Викиди /", report, StringComparison.Ordinal);
        Assert.Contains("\"UnitCode\": \"t\"", await File.ReadAllTextAsync(Path.Combine(_dir, "plan.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Помилки_у_звіті_забороняють_запис()
    {
        var handler = new RecordingHandler();
        var options = Options(
            Workbook(wb => wb.AddWorksheet("Порожній")),
            "--apply", "--api", "http://ecr.test", "--version", "1.0.0.0", "--template-code", "T", "--user", "u", "--password-env", "X");
        using var log = new StringWriter();

        var code = await Program.RunAsync(options, log, CancellationToken.None, handler);

        Assert.Equal(1, code);
        Assert.Empty(handler.Requests);
        Assert.Contains("запис НЕ виконано", await File.ReadAllTextAsync(options.ReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Запис_входить_звіряє_одиниці_і_створює_чернетку()
    {
        var handler = new RecordingHandler();
        var options = Options(
            Workbook(GoodSheet),
            "--apply", "--api", "http://ecr.test", "--version", "1.0.0.0", "--template-code", "T", "--user", "u", "--password-env", "ECR_TEST_NO_SUCH_VAR");
        using var log = new StringWriter();

        var code = await Program.RunAsync(options, log, CancellationToken.None, handler);

        Assert.Equal(0, code);
        Assert.Equal("/api/v1/login/local", handler.Requests[0].Path);
        Assert.Equal("/api/v1/units", handler.Requests[1].Path);
        Assert.Contains(handler.Requests, r => r.Path.EndsWith("/columns/VYKYD", StringComparison.Ordinal)
            && r.Body!.Value.GetProperty("unitId").GetInt32() == 1);
        Assert.Contains("версія-чернетка 42", await File.ReadAllTextAsync(options.ReportPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--apply")]
    [InlineData("--apply", "--api", "http://x", "--version", "1.0.0.0", "--template-code", "T")]
    [InlineData("--apply", "--api", "http://x", "--version", "1.0.0.0", "--template-id", "1", "--template-code", "T", "--windows")]
    [InlineData("--vba", "dir")]
    [InlineData("--row-mode", "sideways")]
    [InlineData("--apply", "--dry-run")]
    public void Неправильні_аргументи_відхиляються(params string[] extra)
    {
        var (options, error) = CommandLine.Parse(["--workbook", Workbook(GoodSheet), .. extra]);

        Assert.Null(options);
        Assert.NotNull(error);
    }
}
