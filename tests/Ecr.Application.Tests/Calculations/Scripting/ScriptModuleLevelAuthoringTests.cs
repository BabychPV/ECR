using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Звичайний авторинг версії рівня <c>Module</c> (HSE400): скрипт без <c>rows</c> не дає хибного <c>missingRows</c> ні в
/// редакторі (зберегти / перевірити / перегляд), ні в публікації; матеріалізація виходів проходить; перегляд на документі
/// для Module — зрозуміла відмова (набір аргументів складає модуль, а не рядки скрипта). Мутаційний доказ: прибрати ознаку
/// у <see cref="ScriptSourceCompiler.CompileForVersionAsync"/> чи в <see cref="ScriptSourceCompiler.IsModuleLevel"/> — тести червоніють.
/// </summary>
public sealed class ScriptModuleLevelAuthoringTests
{
    private const int MethodologyId = 5;
    private const int VersionId = 50;
    private const long DocumentId = 700;
    private static readonly DateOnly From = new(2099, 6, 1);
    private static readonly DateTime Now = new(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // Мінімальний Module-текст за зразком конвертера HSE400: константа, вихід-крок, вихід від місячних входів (`row.…`).
    private const string ModuleText =
        "const Factor = 1.5;\n"
        + "output Common_K : kg = Factor * 2;\n"
        + "output X400_TA_Sum : kg = row.GCV_X400_TA_Methane + row.GCV_X400_TA_Nitrogen;\n";

    private static readonly string[] OutputCodes = ["Common_K", "X400_TA_Sum"];

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _store = ScriptTestKit.Store();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Methodology _methodology = ScriptTestKit.NewMethodology("HSE400_T", MethodologyId);
    private readonly MethodologyVersion _version;

    public ScriptModuleLevelAuthoringTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationBinding>>([new CalculationBinding(100, 7, MethodologyId, "X400_TA_Sum", "{}")]));

        _version = ScriptTestKit.NewVersion(_methodology, VersionId, level: CalculationLevel.Module);
        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns(_methodology);
        var builder = new AccessBuilder { UserId = 9 };
        builder.Permission("Calculation.View");
        builder.Permission("Document.View"); // PSS-25: перегляд вимагає й права читання документа
        builder.Permission(SaveMethodologyScriptHandler.Permission);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private ScriptAnalyzer Analyzer()
        => new(new ScriptSourceCompiler(_drafts, _store), ScriptTestKit.Checker(_store, _drafts), _clock);

    private ScriptMaterializer Materializer() => new(_store, _drafts, ScriptTestKit.Catalog(), _bindings);

    private static string[] Codes(IEnumerable<ScriptDiagnosticDto> diagnostics) => [.. diagnostics.Select(d => d.Code)];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Validate_Module_без_rows_не_дає_діагностик_а_звичайна_версія_дає_missingRows()
    {
        var handler = new ValidateMethodologyScriptHandler(_drafts, Analyzer(), _access, _user);

        var module = await handler.HandleAsync(VersionId, ModuleText, CancellationToken.None);
        Assert.Empty(module.Diagnostics);
        Assert.Equal(["Common_K", "X400_TA_Sum"], module.Outline.Outputs.Select(o => o.Code));

        // Контроль: той самий текст у версії НЕ рівня Module відмовляє — отже, тест перевіряє саме ознаку.
        var plainMethodology = ScriptTestKit.NewMethodology("PLAIN", 6);
        var plain = ScriptTestKit.NewVersion(plainMethodology, 60);
        _drafts.FindVersionAsync(60, Arg.Any<CancellationToken>()).Returns(plain);
        _drafts.FindAsync(6, Arg.Any<CancellationToken>()).Returns(plainMethodology);
        var notModule = await handler.HandleAsync(60, ModuleText, CancellationToken.None);
        Assert.Contains("missingRows", Codes(notModule.Diagnostics));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_Module_зберігає_текст_і_матеріалізує_виходи_без_хибного_missingRows()
    {
        var save = new SaveMethodologyScriptHandler(_drafts, Analyzer(), Materializer(), _uow, _access, _user);

        var saved = await save.HandleAsync(VersionId, ModuleText, null, CancellationToken.None);

        Assert.Empty(saved.Diagnostics);
        Assert.Equal(ModuleText, _version.ScriptText);
        await _store.Received(1).ReplaceScriptOutputsAsync(
            VersionId,
            Arg.Is<IReadOnlyList<ScriptOutputRow>>(o => o.Select(x => x.Code).SequenceEqual(OutputCodes)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Get_Module_віддає_текст_без_діагностик()
    {
        _version.SetScript(ModuleText, ScriptCompiler.Hash(ModuleText));
        var get = new GetMethodologyScriptHandler(_drafts, Analyzer(), _access, _user);

        var dto = await get.HandleAsync(VersionId, CancellationToken.None);

        Assert.Empty(dto.Diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.3")]
    public async Task Preview_Module_дає_зрозумілу_відмову_без_прогону()
    {
        var runner = Substitute.For<IScriptPreviewRunner>();
        var preview = new PreviewMethodologyScriptHandler(_drafts, Analyzer(), _access, _user, runner);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => preview.HandleAsync(VersionId, new PreviewMethodologyScriptRequest(ModuleText, DocumentId, 209901), CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.scriptPreviewModuleLevel", refused.Details!["messageKey"]);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Публікація_Module_проходить_перевірки_і_рахує_збережені_привязки_а_без_привязки_відмовляє_noBoundOutput()
    {
        _store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(new MethodologyScript(ModuleText, null, MethodologyEngine.Script, false, NumericMode.Strict));
        var checks = new ScriptPublishChecks(
            new ScriptSourceCompiler(_drafts, _store), ScriptTestKit.Checker(_store, _drafts), Materializer(), _store);

        var result = await checks.RunAsync(_methodology, _version, From, default);

        // Прив'язки Module живуть на методології (редактор прив'язок): нові не з'являються, наявна рахується.
        Assert.Equal(["X400_TA_Sum"], result.BoundOutputCodes);
        Assert.Equal(0, result.AddedBindings);

        // Без жодної прив'язки методологія «нічого не запише» — справжня відмова, а не missingRows.
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationBinding>>([]));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => checks.RunAsync(_methodology, _version, From, default));
        var keys = ((IReadOnlyList<PublishProblem>)ex.Details!["problems"]!).Select(p => p.MessageKey).ToArray();
        Assert.Equal(["script.noBoundOutput"], keys);
    }
}
