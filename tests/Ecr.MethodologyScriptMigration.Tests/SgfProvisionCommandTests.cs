// tests/Ecr.MethodologyScriptMigration.Tests/SgfProvisionCommandTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Cli;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// ✎ SGF-flaring: <c>sgf-provision</c> — довідники факелів без повторного імпорту складу. Типово сухий прогін (нічого не
/// пише, звіт «що буде створено»), <c>--apply</c> пише, повтор нічого не створює; без довідників складу — відмова.
/// </summary>
[Collection("SqlServer")]
public sealed class SgfProvisionCommandTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "SGF-FLARING-REGISTRIES")]
    public async Task Сухий_прогін_нічого_не_пише__apply_створює__повтор_нічого_не_створює()
    {
        var gas = new FlertRegistryCodes($"STREAM_{_tag}", $"STREAM_CASE_{_tag}", $"GAS_COMPOSITION_{_tag}", $"COMPONENT_{_tag}");
        var flaring = FlertCompositionSyncJob.FlaringCodes(gas);

        var missing = await CliAsync("sgf-provision", "--connection", sql.ConnectionString, "--registry-suffix", _tag);
        Assert.Equal(MigrationCli.UsageOrEnvironment, missing.Code);
        Assert.Contains(gas.Stream, missing.Error, StringComparison.Ordinal);

        await GasRegistriesAsync(gas);

        var dry = await CliAsync("sgf-provision", "--connection", sql.ConnectionString, "--registry-suffix", _tag);
        Assert.Equal(MigrationCli.Ok, dry.Code);
        Assert.Contains("DRY RUN", dry.Output, StringComparison.Ordinal);
        Assert.Contains(flaring.Pin, dry.Output, StringComparison.Ordinal);
        Assert.Contains("Rows: 26", dry.Output, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(flaring));

        var applied = await CliAsync("sgf-provision", "--connection", sql.ConnectionString, "--registry-suffix", _tag, "--apply");
        Assert.Equal(MigrationCli.Ok, applied.Code);
        Assert.Contains("APPLIED", applied.Output, StringComparison.Ordinal);
        Assert.Equal(3, await CountAsync(flaring));

        var again = await CliAsync("sgf-provision", "--connection", sql.ConnectionString, "--registry-suffix", _tag, "--apply");
        Assert.Equal(MigrationCli.Ok, again.Code);
        Assert.Contains("none", again.Output, StringComparison.Ordinal);
        Assert.Contains("Rows: 0", again.Output, StringComparison.Ordinal);
    }

    private async Task GasRegistriesAsync(FlertRegistryCodes gas)
    {
        await using var provider = SgfProvisionCommand.Build(sql.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(CancellationToken.None);
        await services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            async tx => await services.GetRequiredService<FlertRegistryProvisioner>().EnsureAsync(gas, tx),
            CancellationToken.None);
    }

    private async Task<int> CountAsync(FlaringRegistryCodes codes)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        return await db.RegistryDefs.AsNoTracking().CountAsync(d => d.Code == codes.Location || d.Code == codes.Split || d.Code == codes.Pin);
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await MigrationCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }
}
