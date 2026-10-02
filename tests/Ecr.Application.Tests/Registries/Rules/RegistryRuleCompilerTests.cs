// tests/Ecr.Application.Tests/Registries/Rules/RegistryRuleCompilerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Registries.Rules;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries.Rules;

/// <summary>
/// Збереження опису довідника компілює правила (RT-17a, FEATURE-REGISTRY-TABLES §6, «Публікація
/// опису»; <c>R-5</c>): неправильний вираз — 422 з діагностикою, нове <c>UniqueWithin</c> — 422,
/// шаблон «Сума дочірніх» — вираз із параметрів.
/// </summary>
public sealed class RegistryRuleCompilerTests
{
    private const int CaseId = 32;
    private const int CompositionId = 33;

    private static readonly DateTime Now = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly List<RegistryRuleDef> _stored = [];
    private readonly List<RegistryRuleDef> _added = [];

    private readonly RegistryDef _case = Registry("STREAM_CASE", CaseId);
    private readonly RegistryDef _composition = Registry("GAS_COMPOSITION", CompositionId);

    public RegistryRuleCompilerTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        var profile = new AccessBuilder { UserId = 9 }
            .Permission("Registry.EditDefinition")
            .Permission("Registry.Publish")
            .Build();
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);

        var temperature = new RegistryFieldDef(CaseId, EcrCode.Create("T_C"), Text("T"), CellDataType.Decimal, 2);
        SetId(temperature, 322);
        _case.AddField(temperature);

        var link = new RegistryFieldDef(CompositionId, EcrCode.Create("CASE"), Text("CASE"), CellDataType.Lookup, 2);
        SetId(link, 331);
        link.Update(Text("CASE"), 2, isRequired: true);
        link.PointTo(CaseId);
        link.ComposeInto(ParentDeletePolicy.Cascade);
        _composition.AddField(link);

        var molPct = new RegistryFieldDef(CompositionId, EcrCode.Create("MOL_PCT"), Text("MOL_PCT"), CellDataType.Decimal, 3);
        SetId(molPct, 332);
        _composition.AddField(molPct);

        _registries.FindDefinitionAsync("STREAM_CASE", Arg.Any<CancellationToken>()).Returns(_case);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([_case, _composition]);
        _registries.ListRulesAsync(CaseId, Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<RegistryRuleDef>)_stored.ToList());
        _registries.When(r => r.AddRule(Arg.Any<RegistryRuleDef>())).Do(call => _added.Add(call.Arg<RegistryRuleDef>()));
        _keys.ListKeysForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _keys.ListActiveKeysAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    [InlineData("ROW.T_C >", "expr.")]
    [InlineData("[Limit] > 0", RegistryRuleCompiler.ReferenceNotAllowedKey)]
    [InlineData("ROW.T_CX > 0", "expr.registryFieldUnknown")]
    [InlineData("ROW.T_C + 1", RegistryRuleCompiler.NotConditionKey)]
    public async Task Неправильний_вираз_правила_відхиляється_з_діагностикою(string expression, string diagnosticKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM_CASE", Request(NewRule("BAD", "Expression", expression)), default, "\"1\""));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(RegistryRuleCompiler.ExpressionInvalidKey, error.Details!["messageKey"]);
        Assert.Equal("BAD", error.Details["ruleCode"]);
        var diagnostics = (IReadOnlyList<RegistryRuleDiagnosticDto>)error.Details["diagnostics"]!;
        Assert.Contains(diagnostics, d => d.MessageKey.StartsWith(diagnosticKey, StringComparison.Ordinal));
        Assert.Empty(_added);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Параметри_виду_правила_перевіряються()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync(
                "STREAM_CASE",
                Request(NewRule("NEED", "RequiredWhen", "TRUE", """{"field":"NOPE"}""")),
                default,
                "\"1\""));

        var diagnostic = Assert.Single((IReadOnlyList<RegistryRuleDiagnosticDto>)error.Details!["diagnostics"]!);
        Assert.Equal(RegistryRuleCompiler.ParameterInvalidKey, diagnostic.MessageKey);
        Assert.Equal("field", diagnostic.Params!["parameter"]);
        Assert.Equal("NOPE", diagnostic.Params["value"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Нове_UniqueWithin_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync("STREAM_CASE", Request(NewRule("UNIQUE", "UniqueWithin", "ROW.T_C")), default, "\"1\""));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal(RegistryRuleCompiler.UniqueWithinReplacedKey, error.Details!["messageKey"]);
        Assert.Equal("UNIQUE", error.Details["ruleCode"]);
        Assert.Empty(_added);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Шаблон_суми_дочірніх_розгортається_у_вираз()
    {
        // Вираз із запиту ігнорується: його генерують параметри, які читає і сітка (§8.4).
        await Saves().HandleAsync(
            "STREAM_CASE",
            Request(NewRule(
                "SUM_100", "Expression", "TRUE",
                """{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}""")),
            default,
            "\"1\"");

        var rule = Assert.Single(_added);
        Assert.Equal(
            "ABS(REGSUM('GAS_COMPOSITION', ROW.CASE = THIS, ROW.MOL_PCT) - 100) <= 0.5"
            + " OR REGCOUNT('GAS_COMPOSITION', ROW.CASE = THIS) = 0",
            rule.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Шаблон_на_довідник_що_не_є_дитиною_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Saves().HandleAsync(
                "STREAM_CASE",
                Request(NewRule(
                    "SUM_100", "Expression", "TRUE",
                    """{"template":"childSum","child":"STREAM_CASE","field":"T_C","target":100}""")),
                default,
                "\"1\""));

        var diagnostic = Assert.Single((IReadOnlyList<RegistryRuleDiagnosticDto>)error.Details!["diagnostics"]!);
        Assert.Equal("child", diagnostic.Params!["parameter"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Незмінене_старе_правило_не_блокує_збереження()
    {
        // Правило до RT-17a з посиланням на комірку: його виконання дає порушення `invalid`, але
        // правка ІНШОГО правила чи поля не мусить через нього падати.
        var legacy = new RegistryRuleDef(
            CaseId, EcrCode.Create("LEGACY"), RegistryRuleKind.Expression, "[Limit] > 0",
            ValidationSeverity.Warning, Text("legacy"));
        SetId(legacy, 501);
        _stored.Add(legacy);

        var request = Request(
            new RegistryRuleSaveDto(501, "LEGACY", "Expression", "[Limit] > 0", "Warning", Text("legacy"), null, true),
            NewRule("T_POSITIVE", "Expression", "ROW.T_C > 0"));

        await Saves().HandleAsync("STREAM_CASE", request, default, "\"1\"");

        Assert.Equal("ROW.T_C > 0", Assert.Single(_added).Expression);
    }

    private SaveRegistryDefinitionHandler Saves()
        => new(
            _registries, _uow, _audit, _access, _user, _clock, _units, _keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(_keys, _uow),
            new RegistryRuleCompiler(new Parser()));

    private SaveRegistryDefinitionDto Request(params RegistryRuleSaveDto[] rules)
        => new(
            [.. _case.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldSaveDto(
                f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal,
                f.IsRequired, f.IsKey, f.RefRegistryDefId, f.UnitId))],
            rules,
            "RT-17a");

    private static RegistryRuleSaveDto NewRule(string code, string kind, string expression, string? parameters = null)
        => new(null, code, kind, expression, "Error", Text(code), parameters, true);

    /// <summary>Довідник із ключовим полем <c>NAME</c> (без ключового поля опис не зберігається).</summary>
    private static RegistryDef Registry(string code, int id)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        SetId(registry, id);

        var name = new RegistryFieldDef(id, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        SetId(name, (id * 10) + 9);
        name.MarkKey(true);
        registry.AddField(name);
        return registry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
