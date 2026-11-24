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

    [Theory]
    [InlineData("apply")]
    [InlineData("switch")]
    public async Task D571_apply_і_switch_інструмента_відмовляють_на_базі_що_не_схожа_на_копію(string command)
    {
        var file = Path.GetTempFileName();
        try
        {
            var run = command == "apply"
                ? await RunAsync("apply", "--connection", ConnectionToNowhere, "--in", file, "--as-user", "1")
                : await RunAsync("switch", "--connection", ConnectionToNowhere, "--in", file, "--engine", "Script", "--reason", "r", "--as-user", "1");

            Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
            Assert.Contains("не схоже на копію", run.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("EcrLandMs", true)]
    [InlineData("EcrLandUiCopy", true)]
    [InlineData("EcrLandUi", false)]
    [InlineData("ECR", false)]
    [InlineData("EcrLegacyProd", false)]
    public void D571_ім_я_копії_для_apply_switch(string database, bool copy)
        => Assert.Equal(copy, CopyHandlerCommands.LooksLikeCopy(database));

    [Theory]
    [InlineData(new[] { "V1", "V2", "test" }, "V3")]
    [InlineData(new[] { "V1", "V6", "V10" }, "V11")]
    [InlineData(new[] { "test", "1.0.1.0" }, "V1")]
    public void D571_номер_нової_версії_з_еталоном(string[] existing, string expected)
        => Assert.Equal(expected, GoldenVersionsCommand.NextVersionNumber(existing));

    [Theory]
    [InlineData("{}", true)]
    [InlineData(" { \n } ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("{\"OUT\":1.5}", false)]
    public void D571_порожні_очікування_золотого_випадку(string? expected, bool empty)
        => Assert.Equal(empty, GoldenVersionsCommand.IsEmptyExpectation(expected));

    [Fact]
    public async Task D571_golden_versions_відмовляє_на_базі_що_не_схожа_на_копію()
    {
        var file = Path.GetTempFileName();
        try
        {
            var run = await RunAsync("golden-versions", "--connection", ConnectionToNowhere, "--in", file, "--as-user", "1");
            Assert.Equal(MigrationCli.UsageOrEnvironment, run.Code);
            Assert.Contains("не схоже на копію", run.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("{\"documentId\":96,\"periodKey\":{\"value\":202609}}", 96L)]
    [InlineData("{\"periodKey\":{\"value\":202609}}", 0L)]
    [InlineData("not json", 0L)]
    [InlineData(null, 0L)]
    public void D571_документ_золотого_входу(string? input, long expected)
        => Assert.Equal(expected, GoldenVersionsCommand.DocumentOf(input));
}