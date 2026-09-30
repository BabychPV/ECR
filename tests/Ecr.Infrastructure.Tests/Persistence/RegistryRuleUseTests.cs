// tests/Ecr.Infrastructure.Tests/Persistence/RegistryRuleUseTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Registries.Rules;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Parsing;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Ребра <c>cfg.RegistryUse</c> правил довідника (<c>SourceKind = 2</c>, RT-17a, FEATURE-REGISTRY-TABLES
/// §3.2, §6 «Момент»): збереження опису переписує їх з розібраних виразів — через
/// <see cref="RegistryStore.ReplaceRuleUsesAsync"/> на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: без <c>RemoveRange</c> старих ребер →
/// <see cref="Змінене_правило_не_лишає_застарілого_ребра"/> червоний; без <c>AddRange</c> нових →
/// <see cref="Правило_записує_ребра_того_що_читає"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRuleUseTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Правило_записує_ребра_того_що_читає()
    {
        var f = await SeedAsync();

        // Шаблон «Сума дочірніх» на кейсі читає склад цілком (REGSUM, REGCOUNT) і два його поля.
        await SaveAsync(f, Rule(null, "SUM_100", "TRUE",
            $$"""{"template":"childSum","child":"{{f.CompositionCode}}","field":"MOL_PCT","target":100,"tolerance":0.5}"""));

        var ruleId = await RuleIdAsync(f, "SUM_100");
        Assert.Equal(
            [$"{f.CompositionId}:", $"{f.CompositionId}:CASE", $"{f.CompositionId}:MOL_PCT"],
            await UsesAsync(ruleId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Змінене_правило_не_лишає_застарілого_ребра()
    {
        var f = await SeedAsync();

        await SaveAsync(f, Rule(null, "T_POSITIVE", "ROW.T_C > 0"));
        var ruleId = await RuleIdAsync(f, "T_POSITIVE");
        Assert.Equal([$"{f.CaseId}:T_C"], await UsesAsync(ruleId));

        // Те саме правило читає інше поле — ребро T_C мусить зникнути, а не лишитися поруч.
        await SaveAsync(f, Rule(ruleId, "T_POSITIVE", "ROW.T_MAX > 0"));
        Assert.Equal([$"{f.CaseId}:T_MAX"], await UsesAsync(ruleId));

        // Правило прибрано з опису (вимкнено) — воно більше нічого не читає.
        await SaveAsync(f);
        Assert.Empty(await UsesAsync(ruleId));
    }

    private async Task SaveAsync(Fixture f, params RegistryRuleSaveDto[] rules)
    {
        await using var db = Context();
        var store = new RegistryStore(db);
        var keys = new RegistryKeyStore(db);
        var uow = new UnitOfWork(db);
        var handler = new SaveRegistryDefinitionHandler(
            store, uow, new AuditWriter(db), Editor(), User(), Clock(), new UnitCatalog(db),
            keys, new RegistryKeyService(keys, uow), new RegistryRuleCompiler(new Parser()));

        await handler.HandleAsync(
            f.CaseCode,
            new SaveRegistryDefinitionDto(
                [
                    new RegistryFieldSaveDto(f.NameFieldId, "NAME", Text("Name"), "String", 1, false, true, null, null),
                    new RegistryFieldSaveDto(f.TemperatureFieldId, "T_C", Text("T"), "Decimal", 2, false, false, null, null),
                    new RegistryFieldSaveDto(f.MaxFieldId, "T_MAX", Text("T max"), "Decimal", 3, false, false, null, null),
                ],
                rules,
                "RT-17a"),
            CancellationToken.None);
    }

    private async Task<int> RuleIdAsync(Fixture f, string code)
    {
        await using var db = Context();
        return await db.RegistryRuleDefs.AsNoTracking()
            .Where(r => r.RegistryDefId == f.CaseId && r.Code == code)
            .Select(r => r.Id)
            .SingleAsync();
    }

    /// <summary>Ребра правила як «довідник:шлях», упорядковано.</summary>
    private async Task<string[]> UsesAsync(int ruleId)
    {
        await using var db = Context();
        var uses = await db.RegistryUses.AsNoTracking()
            .Where(u => u.SourceKind == RegistryUse.RegistryRuleSource && u.SourceId == ruleId)
            .ToListAsync();
        return [.. uses.Select(u => $"{u.RegistryDefId}:{u.FieldPath}").Order(StringComparer.Ordinal)];
    }

    /// <summary>Кейс (NAME — ключ, T_C, T_MAX) і склад (CASE — композиція на кейс, MOL_PCT).</summary>
    private async Task<Fixture> SeedAsync()
    {
        await using var db = Context();

        var @case = new RegistryDef(EcrCode.Create($"UC{_tag}"), Text("Case"), isTemporal: false);
        var composition = new RegistryDef(EcrCode.Create($"UP{_tag}"), Text("Composition"), isTemporal: false);
        db.RegistryDefs.AddRange(@case, composition);
        await db.SaveChangesAsync();

        var name = new RegistryFieldDef(@case.Id, EcrCode.Create("NAME"), Text("Name"), CellDataType.String, 1);
        name.MarkKey(true);
        var temperature = new RegistryFieldDef(@case.Id, EcrCode.Create("T_C"), Text("T"), CellDataType.Decimal, 2);
        var max = new RegistryFieldDef(@case.Id, EcrCode.Create("T_MAX"), Text("T max"), CellDataType.Decimal, 3);
        var link = new RegistryFieldDef(composition.Id, EcrCode.Create("CASE"), Text("Case"), CellDataType.Lookup, 1);
        link.Update(Text("Case"), 1, isRequired: true);
        link.PointTo(@case.Id);
        link.ComposeInto(ParentDeletePolicy.Cascade);
        var molPct = new RegistryFieldDef(composition.Id, EcrCode.Create("MOL_PCT"), Text("mol %"), CellDataType.Decimal, 2);
        db.RegistryFieldDefs.AddRange(name, temperature, max, link, molPct);
        await db.SaveChangesAsync();

        return new Fixture(@case.Id, @case.Code, composition.Id, composition.Code, name.Id, temperature.Id, max.Id);
    }

    private static RegistryRuleSaveDto Rule(int? id, string code, string expression, string? parameters = null)
        => new(id, code, "Expression", expression, "Warning", Text(code), parameters, true);

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static IAccessDecisionService Editor()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }
                  .Permission("Registry.View")
                  .Permission("Registry.EditDefinition")
                  .Permission("Registry.Publish")
                  .Build());
        return access;
    }

    private static IClock Clock()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));
        return clock;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.CorrelationId.Returns("test");
        return user;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Fixture(
        int CaseId, string CaseCode, int CompositionId, string CompositionCode, int NameFieldId, int TemperatureFieldId, int MaxFieldId);
}
