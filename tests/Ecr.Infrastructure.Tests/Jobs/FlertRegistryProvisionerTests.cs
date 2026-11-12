// tests/Ecr.Infrastructure.Tests/Jobs/FlertRegistryProvisionerTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Крок FLERT F0 (<c>D-476</c>): довідники дзеркала §11.4 заводяться, якщо їх немає, — з джерелом, ключами, композицією,
/// правилом <c>SUM_100</c> і компонентами; другий виклик не змінює нічого; наявний довідник не редагується.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті): прибрати <c>def.SwitchSource(sourceKind)</c> у <c>CreateAsync</c> →
/// <c>Відсутні_довідники_заводяться_за_11_4_а_другий_виклик_нічого_не_змінює</c> червоний (дзеркало стало б Local —
/// ручні правки копії FLERT дозволені).
/// </remarks>
[Collection("SqlServer")]
public sealed class FlertRegistryProvisionerTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private FlertRegistryCodes Codes => new($"STREAM_{_tag}", $"STREAM_CASE_{_tag}", $"GAS_COMPOSITION_{_tag}", $"COMPONENT_{_tag}");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Відсутні_довідники_заводяться_за_11_4_а_другий_виклик_нічого_не_змінює()
    {
        await using var provider = BuildProvider();

        var first = await EnsureAsync(provider);
        Assert.Empty(first.Problems);
        Assert.Equal(new[] { Codes.Component, Codes.Stream, Codes.Case, Codes.Composition }, first.Created);
        Assert.Equal(FlertCompositionSchema.Components.Count, first.ComponentsAdded.Count);

        await using (var db = Context())
        {
            var defs = await db.RegistryDefs.AsNoTracking().Include(d => d.Fields)
                .Where(d => d.Code.EndsWith(_tag)).ToDictionaryAsync(d => d.Code);
            Assert.Equal(RegistrySourceKind.External, defs[Codes.Stream].SourceKind);
            Assert.Equal(RegistrySourceKind.External, defs[Codes.Case].SourceKind);
            Assert.Equal(RegistrySourceKind.External, defs[Codes.Composition].SourceKind);
            Assert.Equal(RegistrySourceKind.Local, defs[Codes.Component].SourceKind);
            Assert.True(defs[Codes.Case].IsTemporal);
            Assert.False(defs[Codes.Composition].IsTemporal);

            var caseStream = defs[Codes.Case].Fields.Single(f => f.Code == FlertCompositionSchema.CaseStream);
            Assert.Equal(defs[Codes.Stream].Id, caseStream.RefRegistryDefId);
            Assert.Equal(RegistryRelationKind.Reference, caseStream.RelationKind);

            var rowCase = defs[Codes.Composition].Fields.Single(f => f.Code == FlertCompositionSchema.RowCase);
            Assert.Equal(defs[Codes.Case].Id, rowCase.RefRegistryDefId);
            Assert.Equal(RegistryRelationKind.Composition, rowCase.RelationKind);
            Assert.Equal(ParentDeletePolicy.Cascade, rowCase.OnParentDelete);

            var keys = await db.RegistryKeyDefs.AsNoTracking()
                .Where(k => k.RegistryDefId == defs[Codes.Case].Id || k.RegistryDefId == defs[Codes.Composition].Id
                            || k.RegistryDefId == defs[Codes.Stream].Id)
                .ToListAsync();
            Assert.Single(keys, k => k.RegistryDefId == defs[Codes.Case].Id && k.IsPrimary);
            Assert.Single(keys, k => k.RegistryDefId == defs[Codes.Composition].Id && k.IsPrimary);
            Assert.Single(keys, k => k.RegistryDefId == defs[Codes.Stream].Id && !k.IsPrimary && k.Code == "BY_LEGACY_ID");

            var rule = await db.RegistryRuleDefs.AsNoTracking().SingleAsync(r => r.RegistryDefId == defs[Codes.Case].Id);
            Assert.Equal(FlertCompositionSchema.Sum100Rule, rule.Code);
            Assert.Equal(RegistryRuleKind.Expression, rule.RuleKind);
            Assert.Contains("\"template\":\"childSum\"", rule.ParametersJson, StringComparison.Ordinal);

            Assert.Equal(46, await db.RegistryEntries.CountAsync(e => e.RegistryDefId == defs[Codes.Component].Id));
        }

        var second = await EnsureAsync(provider);
        Assert.Empty(second.Created);
        Assert.Empty(second.ComponentsAdded);
        Assert.Empty(second.Problems);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Наявний_COMPONENT_доповнюється_відсутніми_кодами_а_наявний_несумісний_STREAM_не_редагується()
    {
        await using (var db = Context())
        {
            var component = new RegistryDef(EcrCode.Create(Codes.Component), Text("Components by hand"), isTemporal: false);
            var stream = new RegistryDef(EcrCode.Create(Codes.Stream), Text("Streams by hand"), isTemporal: false);
            db.RegistryDefs.AddRange(component, stream);
            await db.SaveChangesAsync();
            db.RegistryEntries.Add(new Ecr.Domain.Entities.Dictionaries.RegistryEntry(component.Id, EcrCode.Create("Methane"), Text("CH4")));
            await db.SaveChangesAsync();
        }

        await using var provider = BuildProvider();
        var set = await EnsureAsync(provider);

        Assert.DoesNotContain(Codes.Component, set.Created);
        Assert.DoesNotContain(Codes.Stream, set.Created);
        Assert.Equal(45, set.ComponentsAdded.Count);
        Assert.DoesNotContain("Methane", set.ComponentsAdded);
        Assert.Contains(set.Problems, p => p.StartsWith($"{Codes.Stream}.{FlertCompositionSchema.StreamLegacyId}", StringComparison.Ordinal));
        Assert.Contains(set.Warnings, w => w.StartsWith(Codes.Stream, StringComparison.Ordinal));

        await using var check = Context();
        var untouched = await check.RegistryDefs.AsNoTracking().Include(d => d.Fields).SingleAsync(d => d.Code == Codes.Stream);
        Assert.Empty(untouched.Fields);
        Assert.Equal(RegistrySourceKind.Local, untouched.SourceKind);
    }

    /// <summary>Виклик як у задачі: власна область від <c>svc-integration</c>, одна транзакція.</summary>
    private async Task<FlertRegistrySet> EnsureAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var author = await services.GetRequiredService<IntegrationActor>().EnterAsync(CancellationToken.None);
        FlertRegistrySet? set = null;
        await services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            async tx => set = await services.GetRequiredService<FlertRegistryProvisioner>().EnsureAsync(Codes, tx),
            CancellationToken.None);
        return set!;
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());
        return services.BuildServiceProvider();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
