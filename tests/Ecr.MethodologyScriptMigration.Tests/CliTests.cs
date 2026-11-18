using Ecr.MethodologyScriptMigration.Cli;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>Розбір аргументів, коди виходу й запобіжники — без бази.</summary>
public sealed class CliTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await MigrationCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private const string ConnectionToNowhere = "Server=127.0.0.1,1;Database=EcrProduction;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=True";

    [Fact]
    public async Task Без_аргументів_і_невідома_команда_дають_код_2()
    {
        Assert.Equal(MigrationCli.UsageOrEnvironment, (await RunAsync()).Code);
        var unknown = await RunAsync("explode");
        Assert.Equal(MigrationCli.UsageOrEnvironment, unknown.Code);
        Assert.Contains("explode", unknown.Error);
    }

    [Fact]
    public async Task Parity_без_рядка_підключення_дає_код_2_і_називає_параметр()
    {
        var run = await RunAsync("parity", "--out", "x.json");

        Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
        Assert.Contains("--connection", run.Error);
    }

    [Theory]
    [InlineData("convert")]
    [InlineData("convert", "--connection", "x")]
    [InlineData("convert", "--out", "x")]
    [InlineData("shadow", "--connection", "x")]
    [InlineData("pack", "--connection", "x", "--in", "x")]
    [InlineData("convert", "--connection", "x", "--out", "y", "--bogus")]
    [InlineData("pack", "--connection", "x", "--in", "x", "--out", "p", "--i-know-this-is-a-copy")]
    [InlineData("parity")]
    [InlineData("parity", "--connection", "x")]
    [InlineData("parity", "--connection", "x", "--out", "y", "--document", "abc")]
    [InlineData("parity", "--connection", "x", "--out", "y", "--bogus")]
    [InlineData("convert", "--connection", "x", "--out", "y", "--document", "5")]
    public async Task Неповні_й_невідомі_параметри_дають_код_2(params string[] args)
    {
        var run = await RunAsync(args);

        Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
    }

    [Fact]
    public async Task Shadow_відмовляє_для_бази_без_безпечного_слова_в_імені_до_будь_якого_доступу()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var run = await RunAsync(
            "shadow", "--connection", ConnectionToNowhere,
            "--in", directory);

        Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
        Assert.Contains("shadow відмовлено", run.Error);
        Assert.Contains(MigrationCli.CopyFlag, run.Error);
    }

    [Fact]
    public async Task Shadow_із_прапорцем_копії_доходить_до_бази_і_падає_вже_на_середовищі()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var run = await RunAsync(
            "shadow", "--connection", ConnectionToNowhere,
            "--in", directory, MigrationCli.CopyFlag);

        Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
        Assert.DoesNotContain("shadow відмовлено", run.Error);
        Assert.Contains("Помилка середовища", run.Error);
    }

    [Theory]
    [InlineData("EcrCopy_2026", true)]
    [InlineData("ecr_TEST", true)]
    [InlineData("Ecr_upg_check", true)]
    [InlineData("EcrDev", true)]
    [InlineData("ecr-stand-1", true)]
    [InlineData("Ecr", false)]
    [InlineData("EcrProduction", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Безпечні_слова_в_імені_бази(string? name, bool expected)
        => Assert.Equal(expected, MigrationCli.LooksSafe(name));
}
