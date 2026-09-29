// tests/Ecr.Application.Tests/Registries/Rules/RegistryRuleEngineTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Rules;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries.Rules;

/// <summary>
/// Рушій правил довідника без бази (RT-17a, <c>ФВ-8.18</c>, FEATURE-REGISTRY-TABLES §6; Д-4):
/// семантика видів правил, рівні, правило батька композиції при зміні дитини.
/// </summary>
/// <remarks>
/// Знімок довідників — <see cref="InMemoryRegistrySnapshot"/> (його віддає завантажувач-заглушка):
/// тут перевіряється рушій, а не SQL. Наскрізний шлях — <c>RegistryRulesHttpTests</c>.
/// </remarks>
public sealed class RegistryRuleEngineTests
{
    private const int CaseId = 32;
    private const int CompositionId = 33;
    private const int CaseFieldId = 331;
    private const int MolPctFieldId = 332;
    private const long CaseEntry = 4411;

    private static readonly DateTime Now = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly IRegistrySnapshotLoader _loader = Substitute.For<IRegistrySnapshotLoader>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly RegistryDef _case = Registry("STREAM_CASE", CaseId);
    private readonly RegistryDef _composition = Registry("GAS_COMPOSITION", CompositionId);
    private readonly List<RegistryRuleDef> _caseRules = [];
    private readonly List<RegistryRuleDef> _compositionRules = [];
    private readonly InMemoryRegistrySnapshot _snapshot = new();

    public RegistryRuleEngineTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.Language.Returns("en");

        var link = new RegistryFieldDef(CompositionId, EcrCode.Create("CASE"), Text("CASE"), CellDataType.Lookup, 2);
        SetId(link, CaseFieldId);
        link.Update(Text("CASE"), 2, isRequired: true);
        link.PointTo(CaseId);
        link.ComposeInto(ParentDeletePolicy.Cascade);
        _composition.AddField(link);

        var molPct = new RegistryFieldDef(CompositionId, EcrCode.Create("MOL_PCT"), Text("MOL_PCT"), CellDataType.Decimal, 3);
        SetId(molPct, MolPctFieldId);
        _composition.AddField(molPct);

        _registries.ListRulesAsync(CaseId, Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<RegistryRuleDef>)_caseRules.ToList());
        _registries.ListRulesAsync(CompositionId, Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<RegistryRuleDef>)_compositionRules.ToList());
        _registries.FindDefinitionByIdAsync(CaseId, Arg.Any<CancellationToken>()).Returns(_case);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([_case, _composition]);
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0)
                .Select(id => new RegistryEntryStanding(id, 0, IsActive: true, IsDeleted: false, null, null))
                .ToList());
        _registries.ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0)
                .Where(id => id != CaseEntry)
                .Select(id => Link(id, CaseEntry))
                .ToList());
        _keys.FindEntryCodesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToDictionary(id => id, id => $"E{id}"));
        _loader.LoadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(_snapshot);

        _snapshot
            .AddRegistry("STREAM_CASE", ["NAME", "T_C", "COMPONENT"])
            .AddRegistry("GAS_COMPOSITION", ["CASE", "MOL_PCT", "COMPONENT"], lookupFields: ["CASE"])
            .AddRegistry("COMPONENT", ["NAME"])
            .AddEntry("STREAM_CASE", CaseEntry, "E4411", new() { ["NAME"] = ExpressionValue.Text("1D-2") })
            .AddEntry("COMPONENT", 31, "CH4")
            .AddEntry("GAS_COMPOSITION", 9001, "C1", Row(60m), ordinal: 1)
            .AddEntry("GAS_COMPOSITION", 9002, "C2", Row(30m), ordinal: 2);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Зміна_дитини_перевіряє_правило_батька_що_читає_дитину()
    {
        // Σ складу кейсу 4411 = 60 + 30 = 90 ≠ 100 ± 0.5. Змінено РЯДОК СКЛАДУ, правило — на кейсі.
        _caseRules.Add(SumRule(ValidationSeverity.Error));

        var check = await Engine().CheckAsync(_composition, [9002], [], default);

        var error = Assert.Single(check.Errors);
        Assert.Equal(CaseEntry, error.EntryId);
        Assert.Equal("E4411", error.EntryCode);
        Assert.Equal("SUM_100", error.Rule);
        Assert.Equal(RegistryRuleEngine.ViolatedKey, error.MessageKey);
        Assert.Equal("90", error.Params["value"]);
        Assert.Equal("Composition must add up to 100 %", error.Params["message"]);

        var thrown = Assert.Throws<BusinessRuleException>(check.ThrowIfErrors);
        Assert.Equal("ECR-REG-4221", thrown.ErrorCode);
        Assert.Equal(RegistryRuleEngine.RuleViolatedErrorKey, thrown.Details!["messageKey"]);
        Assert.Equal("SUM_100", thrown.Details["rule"]);
        Assert.Equal("E4411", thrown.Details["entryCode"]);
        Assert.Single((IReadOnlyList<RegistryRuleViolationDto>)thrown.Details["violations"]!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Видалення_дитини_теж_перевіряє_правило_батька()
    {
        // Видалений рядок (10 %) уже невидимий у знімку; сума кейсу без нього — 90. Його власні
        // правила не виконуються, правило батька — так.
        _caseRules.Add(SumRule(ValidationSeverity.Error));
        _compositionRules.Add(Rule(CompositionId, "ALWAYS", RegistryRuleKind.Expression, "FALSE", ValidationSeverity.Error));
        _snapshot.AddEntry("GAS_COMPOSITION", 9007, "C7", Row(10m), ordinal: 7, visible: false);

        var check = await Engine().CheckAsync(_composition, [], [9007], default);

        Assert.Equal(CaseEntry, Assert.Single(check.Errors).EntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Виконане_правило_батька_порушень_не_дає()
    {
        _caseRules.Add(SumRule(ValidationSeverity.Error));
        _snapshot.AddEntry("GAS_COMPOSITION", 9003, "C3", Row(10.3m), ordinal: 3);

        var check = await Engine().CheckAsync(_composition, [9003], [], default);

        Assert.Empty(check.Violations);
        check.ThrowIfErrors();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Кейс_без_складу_не_порушує_суму()
    {
        // Батько зберігається раніше за дітей (пакет — один довідник): порожній склад — ще не
        // введений, а не «Σ = 0». Правило рівня Error інакше не дало б створити жодного кейсу.
        _caseRules.Add(SumRule(ValidationSeverity.Error));
        _snapshot.AddEntry("STREAM_CASE", 4412, "E4412");

        var check = await Engine().CheckAsync(_case, [4412], [], default);

        Assert.Empty(check.Violations);

        // Власне правило кейсу виконується й на ЙОГО зміні — кейс із неповним складом відхиляється.
        var own = await Engine().CheckAsync(_case, [CaseEntry], [], default);
        Assert.Equal(CaseEntry, Assert.Single(own.Errors).EntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Warning_не_блокує_а_попереджає()
    {
        _caseRules.Add(SumRule(ValidationSeverity.Warning));

        var check = await Engine().CheckAsync(_composition, [9001], [], default);

        Assert.Empty(check.Errors);
        var warning = Assert.Single(check.Warnings);
        Assert.Equal("Warning", warning.Severity);
        check.ThrowIfErrors();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Правило_батька_що_не_читає_дитину_на_зміну_дитини_не_виконується()
    {
        // Правило кейсу про його власне поле — зміна складу його результату не змінює.
        _caseRules.Add(Rule(CaseId, "NAME_SET", RegistryRuleKind.Expression, "ROW.NAME = 'nothing'", ValidationSeverity.Error));

        var check = await Engine().CheckAsync(_composition, [9001], [], default);

        Assert.Empty(check.Violations);
        await _loader.DidNotReceive().LoadAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Вираз_null_не_порушення_а_помилка_значення_порушення()
    {
        // §6: null — не порушення (як CHECK); помилка-значення — порушення з її кодом.
        _compositionRules.Add(Rule(CompositionId, "PCT_POSITIVE", RegistryRuleKind.Expression, "ROW.MOL_PCT > 0", ValidationSeverity.Error));
        _compositionRules.Add(Rule(CompositionId, "PCT_DIVIDE", RegistryRuleKind.Expression, "100 / (ROW.MOL_PCT - 60) > 0", ValidationSeverity.Warning));
        _snapshot.AddEntry("GAS_COMPOSITION", 9004, "C4", new() { ["CASE"] = ExpressionValue.Number(CaseEntry) }, ordinal: 4);

        var check = await Engine().CheckAsync(_composition, [9001, 9004], [], default);

        // 9004: MOL_PCT порожнє → `ROW.MOL_PCT > 0` дає null, і це НЕ порушення. Ділення: 9001 —
        // на нуль (60 − 60), 9004 — на порожнє (граматика шаблону, як Excel) — обидва #DIV/0,
        // і помилка-значення — порушення, а не тиша.
        Assert.Equal(
            ["PCT_DIVIDE@9001:#DIV/0", "PCT_DIVIDE@9004:#DIV/0"],
            check.Violations.Select(v => $"{v.Rule}@{v.EntryId}:{v.Params["errorCode"]}").ToArray());
        Assert.All(check.Violations, v => Assert.Equal("Warning", v.Severity));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task RequiredWhen_вимагає_поле_лише_коли_умова_істинна()
    {
        _compositionRules.Add(Rule(
            CompositionId, "COMPONENT_WHEN_PCT", RegistryRuleKind.RequiredWhen, "ROW.MOL_PCT > 50",
            ValidationSeverity.Error, """{"field":"COMPONENT"}"""));

        var check = await Engine().CheckAsync(_composition, [9001, 9002], [], default);

        // 9001 (60 %) без компонента — порушення; 9002 (30 %) — умова хибна.
        var violation = Assert.Single(check.Errors);
        Assert.Equal(9001, violation.EntryId);
        Assert.Equal("COMPONENT", violation.Params["field"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task CrossRegistry_шукає_значення_в_іншому_довіднику()
    {
        _compositionRules.Add(Rule(
            CompositionId, "COMPONENT_KNOWN", RegistryRuleKind.CrossRegistry, "TRUE",
            ValidationSeverity.Error, """{"field":"COMPONENT","registry":"COMPONENT"}"""));
        _snapshot
            .AddEntry("GAS_COMPOSITION", 9005, "C5", new() { ["CASE"] = ExpressionValue.Number(CaseEntry), ["COMPONENT"] = ExpressionValue.Text("CH4") }, ordinal: 5)
            .AddEntry("GAS_COMPOSITION", 9006, "C6", new() { ["CASE"] = ExpressionValue.Number(CaseEntry), ["COMPONENT"] = ExpressionValue.Text("XX9") }, ordinal: 6);

        var check = await Engine().CheckAsync(_composition, [9001, 9005, 9006], [], default);

        // 9001: поле порожнє — не порушення; 9005: CH4 є; 9006: XX9 немає → #N/A.
        var violation = Assert.Single(check.Errors);
        Assert.Equal(9006, violation.EntryId);
        Assert.Equal("#N/A", violation.Params["errorCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Правило_що_не_розбирається_не_мовчить()
    {
        // Д-4: правило, якого рушій не може виконати, мусить бути видно, а не «виглядати налаштованим».
        _compositionRules.Add(Rule(CompositionId, "BROKEN", RegistryRuleKind.Expression, "ROW.MOL_PCT >", ValidationSeverity.Error));

        var check = await Engine().CheckAsync(_composition, [9001], [], default);

        var violation = Assert.Single(check.Errors);
        Assert.Equal(RegistryRuleEngine.InvalidKey, violation.MessageKey);
        Assert.Equal("BROKEN", violation.Params["rule"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task UniqueWithin_і_вимкнені_правила_не_виконуються()
    {
        _compositionRules.Add(Rule(CompositionId, "UNIQUE", RegistryRuleKind.UniqueWithin, "ROW.MOL_PCT", ValidationSeverity.Error));
        var off = Rule(CompositionId, "OFF", RegistryRuleKind.Expression, "FALSE", ValidationSeverity.Error);
        off.SetActive(false);
        _compositionRules.Add(off);

        var check = await Engine().CheckAsync(_composition, [9001], [], default);

        Assert.Empty(check.Violations);
    }

    private RegistryRuleEngine Engine()
        => new(
            _registries, _keys, _loader, new RegistryRuleCompiler(new Parser()),
            new Evaluator(new FunctionRegistry()), _user, _clock);

    private static RegistryRuleDef SumRule(ValidationSeverity severity)
        => Rule(
            CaseId, "SUM_100", RegistryRuleKind.Expression,
            RegistryRuleTemplates.ChildSumExpression("GAS_COMPOSITION", "CASE", "MOL_PCT", 100m, 0.5m),
            severity,
            """{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}""");

    private static RegistryRuleDef Rule(
        int registryId, string code, RegistryRuleKind kind, string expression, ValidationSeverity severity, string? parameters = null)
        => new(registryId, EcrCode.Create(code), kind, expression, severity, Text("Composition must add up to 100 %"), parameters);

    private static Dictionary<string, ExpressionValue> Row(decimal molPct)
        => new() { ["CASE"] = ExpressionValue.Number(CaseEntry), ["MOL_PCT"] = ExpressionValue.Number(molPct) };

    private static RegistryValue Link(long child, long parent)
    {
        var value = new RegistryValue(child, CaseFieldId);
        value.Set(CellDataType.Lookup, parent, unitId: null);
        return value;
    }

    private static RegistryDef Registry(string code, int id)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        SetId(registry, id);
        return registry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
