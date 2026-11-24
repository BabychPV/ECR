using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// ✎ P-1124-2: остаточне перенесення формульних версій на скрипт (<see cref="FinalizeFormulasHandler"/>). Кожні ворота —
/// окремий тест, що червоніє, коли ворота прибрати: шлях одноразовий, лише для НЕ чинних версій, лише доведений текст,
/// походження формул — у журнал перед перемиканням, атомарно.
/// </summary>
public sealed class FinalizeFormulasHandlerTests
{
    private const int Alpha = 5;
    private const int OldV = 50;
    private const int NewV = 51;
    private const string Text = "rows from T1;\noutput OUT_A : t = row.Hours;";

    private static readonly DateTime Now = new(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _store;
    private readonly IVersionSourceReader _reader = Substitute.For<IVersionSourceReader>();
    private readonly IScriptMigrationStore _migration = Substitute.For<IScriptMigrationStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Methodology _alpha = ScriptTestKit.NewMethodology("ALPHA", Alpha);
    private readonly MethodologyVersion _old;
    private readonly VersionSource _source = new() { MethodologyCode = "ALPHA", Formulas = [new SourceFormula("F1", "@Hours", 1)] };
    private readonly List<PublicationEventRecord> _events = [];

    public FinalizeFormulasHandlerTests()
    {
        _store = ScriptTestKit.Store(ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "OUT_A", CellDataType.Calculated)]));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(FinalizeFormulasHandler.Permission).Build());
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _audit.WritePublicationEventAsync(Arg.Do<PublicationEventRecord>(_events.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _old = ScriptTestKit.NewVersion(_alpha, OldV, MethodologyEngine.Formulas, "V1");
        _old.SetScript(Text, ScriptCompiler.Hash(Text));
        _old.Publish(9, "reason", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        _drafts.FindAsync(Alpha, Arg.Any<CancellationToken>()).Returns(_alpha);
        _drafts.FindVersionAsync(OldV, Arg.Any<CancellationToken>()).Returns(_old);
        _store.FindByVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_alpha);
        _reader.LoadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
            new LoadedVersion(
                new VersionRef(OldV, Alpha, "ALPHA", "V1", new DateOnly(2099, 1, 1), TemplateVersionStatus.Published,
                    CalculationLevel.Configuration, MethodologyEngine.Formulas, MethodologyKind.DataDriven),
                _source,
                []));
        _store.ResolveImportsAsync(Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyLibrary>>([]));
        _store.GetOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyOutput>>([Output("OUT_A")]));
        _store.GetSubstanceCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>()));
        _migration.GetImporterVersionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ImporterVersion>>([]));
        _migration.CountFormulasVersionsAsync(Arg.Any<CancellationToken>()).Returns(1);
    }

    private static MethodologyOutput Output(string code)
    {
        var output = new MethodologyOutput(OldV, EcrCode.Create(code), ScriptTestKit.Tonne, 1);
        output.SetPerSubstance(false);
        return output;
    }

    private void Supersede()
    {
        var newer = ScriptTestKit.NewVersion(_alpha, NewV, MethodologyEngine.Script, "V2");
        newer.SetScript(Text, ScriptCompiler.Hash(Text));
        newer.Publish(9, "reason", new DateOnly(2099, 2, 1), testsPassed: true, Now);
    }

    private FinalizeFormulasHandler Handler()
        => new(
            _store, _migration, _reader,
            new ScriptAnalyzer(new ScriptSourceCompiler(_drafts, _store), ScriptTestKit.Checker(_store, _drafts), _clock),
            new ScriptMaterializer(_store, _drafts, ScriptTestKit.Catalog(), Substitute.For<ICalculationBindingStore>()),
            _uow, _audit, _access, _user, _clock);

    private static string Hash(string text) => Convert.ToHexStringLower(ScriptCompiler.Hash(text));

    private static FinalizeFormulasRequest Request(params FinalizeFormulasEntryDto[] entries) => new("P-1124-2 test", entries);

    private static FinalizeFormulasEntryDto Proven(int id = OldV, string? hash = null, string proof = "reference", int values = 5)
        => new(id, hash ?? Hash(Text), proof, values);

    private static string ProblemsOf(BusinessRuleException error)
        => string.Join(" | ", (IEnumerable<string>)error.Details!["problems"]!);

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_витіснена_доведена_версія_стає_скриптом_тим_самим_Id_а_формули_йдуть_у_журнал()
    {
        Supersede();
        _migration.CountFormulasVersionsAsync(Arg.Any<CancellationToken>()).Returns(1, 0);

        var response = await Handler().HandleAsync(Request(Proven()), CancellationToken.None);

        Assert.Equal(MethodologyEngine.Script, _old.Engine);
        var finalized = Assert.Single(response.Finalized);
        Assert.Equal((OldV, "reference"), (finalized.VersionId, finalized.Proof));
        Assert.Equal(0, response.FormulasVersionsLeft);
        var evt = Assert.Single(_events);
        Assert.Equal((OldV, "P-1124-2 test", 9), (evt.EntityId, evt.ChangeReason, evt.ChangedByUserId));
        using var json = JsonDocument.Parse(evt.ResultDiffJson!);
        Assert.Equal(FinalizeFormulasHandler.Operation, json.RootElement.GetProperty("operation").GetString());
        Assert.Equal(Fingerprint.Canonical(_source), json.RootElement.GetProperty("formulaSource").GetString());
        Assert.Equal(Fingerprint.ComputeHex(_source), json.RootElement.GetProperty("sourceFingerprint").GetString());
        Assert.Equal(Hash(Text), json.RootElement.GetProperty("scriptHash").GetString());
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_коли_формульних_версій_немає_шлях_закритий()
    {
        Supersede();
        _migration.CountFormulasVersionsAsync(Arg.Any<CancellationToken>()).Returns(0);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(Request(Proven()), CancellationToken.None));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.finalizeNothingLeft", error.Details!["messageKey"]);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_чинну_версію_не_бере_лише_звичайне_перемикання()
    {
        // Нічого новішого не опубліковано: V1 — чинна.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(Request(Proven()), CancellationToken.None));

        Assert.Contains("is current", ProblemsOf(error), StringComparison.Ordinal);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
        Assert.Empty(_events);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_хеш_доказу_іншого_тексту_відмова()
    {
        Supersede();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(hash: Hash(Text + " ")) ), CancellationToken.None));

        Assert.Contains("hash differs", ProblemsOf(error), StringComparison.Ordinal);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_без_порівняного_значення_версія_недоведена()
    {
        Supersede();

        var zero = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(values: 0)), CancellationToken.None));
        Assert.Contains("not proven", ProblemsOf(zero), StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(proof: "trust-me")), CancellationToken.None));
        Assert.Contains("not proven", ProblemsOf(unknown), StringComparison.Ordinal);

        // noRules — лише коли у версії справді немає правил рядків; тут вони є.
        var withRules = new VersionSource { MethodologyCode = "ALPHA", Rules = [new SourceRule([])] };
        _reader.LoadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
            new LoadedVersion(
                new VersionRef(OldV, Alpha, "ALPHA", "V1", new DateOnly(2099, 1, 1), TemplateVersionStatus.Published,
                    CalculationLevel.Configuration, MethodologyEngine.Formulas, MethodologyKind.DataDriven),
                withRules,
                []));
        var rules = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(proof: "noRules", values: 0)), CancellationToken.None));
        Assert.Contains("not proven", ProblemsOf(rules), StringComparison.Ordinal);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_склад_зі_скрипта_інший_ніж_наявний_відмова()
    {
        Supersede();
        _store.GetOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyOutput>>([Output("OUT_OTHER")]));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(Request(Proven()), CancellationToken.None));

        Assert.Contains("composition", ProblemsOf(error), StringComparison.Ordinal);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_текст_не_компілюється_відмова()
    {
        Supersede();
        // Розбирається (скрипт є), але має діагностику компіляції: невідома величина.
        const string broken = "rows from T1;\noutput OUT_A : t = row.Hours * notDeclared;";
        _old.SetScript(broken, ScriptCompiler.Hash(broken));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(hash: Hash(broken))), CancellationToken.None));

        Assert.Contains("does not compile", ProblemsOf(error), StringComparison.Ordinal);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_атомарно_одна_недоведена_версія_зупиняє_весь_запит()
    {
        Supersede();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Request(Proven(), Proven(id: 777)), CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.finalizeRefused", error.Details!["messageKey"]);
        Assert.Equal(MethodologyEngine.Formulas, _old.Engine);
        Assert.Empty(_events);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_чернетка_без_правил_переходить_на_скрипт()
    {
        var draft = ScriptTestKit.NewVersion(_alpha, 60, MethodologyEngine.Formulas, "draft");
        draft.SetScript(Text, ScriptCompiler.Hash(Text));
        _old.SwitchEngineForMigration(MethodologyEngine.Script); // V1 уже скрипт; чернетка — єдина формульна

        var response = await Handler().HandleAsync(Request(Proven(id: 60, proof: "noRules", values: 0)), CancellationToken.None);

        Assert.Equal(MethodologyEngine.Script, draft.Engine);
        Assert.Equal(TemplateVersionStatus.Draft, draft.Status);
        Assert.Equal(60, Assert.Single(response.Finalized).VersionId);
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public async Task Finalize_текст_запиту_стає_текстом_версії_лише_з_доведеним_хешем_а_стара_тінь_іде_в_журнал()
    {
        Supersede();
        var bare = ScriptTestKit.NewVersion(_alpha, 52, MethodologyEngine.Formulas, "V0");
        bare.Publish(9, "reason", new DateOnly(2098, 1, 1), testsPassed: true, Now);

        var wrong = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new FinalizeFormulasEntryDto(52, Hash(Text + "//x"), "reference", 3, Text)), CancellationToken.None));
        Assert.Contains("hash differs", ProblemsOf(wrong), StringComparison.Ordinal);
        Assert.Null(bare.ScriptText);

        // Тінь V1 (Text) замінюється повним текстом (Full) — лише доведеним (хеш Full).
        const string Full = "rows from T1;\n// full life\noutput OUT_A : t = row.Hours;";
        var response = await Handler().HandleAsync(
            Request(
                new FinalizeFormulasEntryDto(52, Hash(Text), "reference", 3, Text),
                new FinalizeFormulasEntryDto(OldV, Hash(Full), "reference", 3, Full)),
            CancellationToken.None);

        Assert.Equal(2, response.Finalized.Count);
        Assert.Equal((Text, MethodologyEngine.Script), (bare.ScriptText, bare.Engine));
        Assert.Equal((Full, MethodologyEngine.Script), (_old.ScriptText, _old.Engine));
        var oldEvent = _events.Single(e => e.EntityId == OldV);
        using var json = JsonDocument.Parse(oldEvent.ResultDiffJson!);
        Assert.Equal(Hash(Text), json.RootElement.GetProperty("previousShadowHash").GetString());
        Assert.Equal(Hash(Full), json.RootElement.GetProperty("scriptHash").GetString());
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public void IsCurrent_опублікована_без_новішої_чинна_чернетка_й_витіснена_ні()
    {
        Assert.True(FinalizeFormulasHandler.IsCurrent(_alpha, _old));
        Supersede();
        Assert.False(FinalizeFormulasHandler.IsCurrent(_alpha, _old));
        var draft = ScriptTestKit.NewVersion(_alpha, 61, MethodologyEngine.Formulas, "d");
        Assert.False(FinalizeFormulasHandler.IsCurrent(_alpha, draft));
    }
}
