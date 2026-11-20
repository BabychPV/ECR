using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Авторські обробники скрипта (MS 5) НА РІВНІ ОБРОБНИКІВ, повз контролер: право, стан версії, звірка хеша,
/// матеріалізація лише з чистим текстом, доступ до документа й обмеження колонок у перегляді та паритеті.
/// </summary>
/// <remarks>
/// Мутаційний доказ (F13): без цих тестів прибрання перевірки з обробника лишалося непоміченим, бо контролер звіряє
/// право ще раз (<c>MethodologyVersionScope</c>), а API-тести б'ють лише через нього.
/// </remarks>
public sealed class ScriptAuthoringHandlerTests
{
    private const int MethodologyId = 5;
    private const int VersionId = 50;
    private const long DocumentId = 700;
    private const string Clean = "rows from T1;\noutput OUT_A : t = row.Hours;";

    private static readonly DateTime Now = new(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _store;
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Methodology _methodology = ScriptTestKit.NewMethodology("ALPHA", MethodologyId);
    private readonly MethodologyVersion _version;

    public ScriptAuthoringHandlerTests()
    {
        _store = ScriptTestKit.Store(ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "OUT_A", CellDataType.Calculated)]));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _version = ScriptTestKit.NewVersion(_methodology, VersionId);
        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns(_methodology);
        Grant();
    }

    private void Grant(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private void GrantRestricted(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        builder.Grant(ResourceKind.Column, 40, GrantLevel.Read);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private ScriptAnalyzer Analyzer()
        => new(new ScriptSourceCompiler(_drafts, _store), ScriptTestKit.Checker(_store, _drafts), _clock);

    private SaveMethodologyScriptHandler Save()
        => new(
            _drafts,
            Analyzer(),
            new ScriptMaterializer(_store, _drafts, ScriptTestKit.Catalog(), Substitute.For<ICalculationBindingStore>()),
            _uow,
            _access,
            _user);

    private static string HashOf(string text) => ScriptAnalyzer.Hex(ScriptCompiler.Hash(text));

    private static async Task<BusinessRuleException> RefusedAsync(Task task)
        => await Assert.ThrowsAsync<BusinessRuleException>(() => task);

    // ---------- Save ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_без_права_EditFormula_відмовляє_і_нічого_не_пише()
    {
        Grant("Calculation.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(VersionId, Clean, null, CancellationToken.None));

        Assert.Null(_version.ScriptText);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_опублікована_версія_скрипта_не_редагується()
    {
        Grant(SaveMethodologyScriptHandler.Permission);
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));
        _version.Publish(9, "reason", new DateOnly(2099, 1, 1), testsPassed: true, Now);

        var refused = await RefusedAsync(Save().HandleAsync(VersionId, Clean + "\n// x", null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0409.scriptNotEditable", refused.Details!["messageKey"]);
        Assert.Equal(Clean, _version.ScriptText);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_чернетка_з_рушієм_формул_не_редагується_як_скрипт()
    {
        Grant(SaveMethodologyScriptHandler.Permission);
        var formulas = ScriptTestKit.NewVersion(_methodology, 51, MethodologyEngine.Formulas, "2.0");
        _drafts.FindVersionAsync(51, Arg.Any<CancellationToken>()).Returns(formulas);

        var refused = await RefusedAsync(Save().HandleAsync(51, Clean, null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0409.scriptNotEditable", refused.Details!["messageKey"]);
        Assert.Null(formulas.ScriptText);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_хеш_що_розійшовся_відмовляє_409_і_лишає_чужий_текст_а_збіжний_пише()
    {
        Grant(SaveMethodologyScriptHandler.Permission);
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));
        var next = Clean + "\n// next";

        var refused = await RefusedAsync(Save().HandleAsync(VersionId, next, HashOf("// чужий текст"), CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0409.scriptChanged", refused.Details!["messageKey"]);
        Assert.Equal(Clean, _version.ScriptText);

        // Регістр і пробіли навколо хеша не заважають; той самий хеш — запис.
        var saved = await Save().HandleAsync(VersionId, next, " " + HashOf(Clean).ToUpperInvariant() + " ", CancellationToken.None);
        Assert.Equal(next, _version.ScriptText);
        Assert.Equal(HashOf(next), saved.Hash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_порожній_текст_відмовляє_422_і_лишає_попередній()
    {
        Grant(SaveMethodologyScriptHandler.Permission);
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));

        var refused = await RefusedAsync(Save().HandleAsync(VersionId, "  \n ", null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.scriptEmpty", refused.Details!["messageKey"]);
        Assert.Equal(Clean, _version.ScriptText);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_текст_із_діагностиками_зберігається_але_не_матеріалізується()
    {
        Grant(SaveMethodologyScriptHandler.Permission);
        const string broken = "rows from NO_SUCH_TABLE;\noutput OUT_A : t = row.Hours;";

        var saved = await Save().HandleAsync(VersionId, broken, null, CancellationToken.None);

        Assert.NotEmpty(saved.Diagnostics);
        Assert.Equal(broken, _version.ScriptText);
        await _store.DidNotReceiveWithAnyArgs().ReplaceScriptOutputsAsync(default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().ReplaceScriptReadTablesAsync(default, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Save_чистий_текст_матеріалізує_виходи_й_таблиці_читання_без_залежностей_публікації()
    {
        Grant(SaveMethodologyScriptHandler.Permission);

        var saved = await Save().HandleAsync(VersionId, Clean, null, CancellationToken.None);

        Assert.Empty(saved.Diagnostics);
        await _store.Received(1).ReplaceScriptOutputsAsync(
            VersionId, Arg.Is<IReadOnlyList<ScriptOutputRow>>(r => r.Count == 1 && r[0].Code == "OUT_A"), Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceScriptReadTablesAsync(VersionId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        // Збереження чернетки не пише залежностей: вони з'являються лише з публікацією.
        await _store.DidNotReceiveWithAnyArgs().ReplaceDependenciesAsync(default, default!, default);
    }

    // ---------- Get / Validate ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Get_і_Validate_без_права_View_відмовляють()
    {
        Grant();
        var analyzer = Analyzer();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => new GetMethodologyScriptHandler(_drafts, analyzer, _access, _user).HandleAsync(VersionId, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => new ValidateMethodologyScriptHandler(_drafts, analyzer, _access, _user).HandleAsync(VersionId, Clean, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => new FormatScriptConstHandler(_access, _user).HandleAsync("5", CancellationToken.None));
    }

    // ---------- Parity ----------

    private ParityMethodologyScriptHandler Parity(IScriptParityRunner? runner)
        => new(_drafts, _access, _user, runner);

    private IScriptParityRunner Runner()
    {
        var runner = Substitute.For<IScriptParityRunner>();
        runner.RunAsync(Arg.Any<ScriptParityRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScriptParityResult(1, 2, 3, []));
        return runner;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Parity_без_права_Publish_відмовляє_а_з_правом_бере_ЗБЕРЕЖЕНИЙ_текст()
    {
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));
        var runner = Runner();
        Grant("Calculation.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, 209901), CancellationToken.None));
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);

        Grant(ParityMethodologyScriptHandler.Permission, "Document.View");
        var result = await Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, 209901), CancellationToken.None);

        Assert.Equal(3, result.ValuesCompared);
        await runner.Received(1).RunAsync(
            Arg.Is<ScriptParityRequest>(r => r.Text == Clean && r.DocumentId == DocumentId && r.PeriodKey == 209901),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Parity_читач_з_обмеженим_доступом_до_колонок_не_бачить_значень_входів()
    {
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));
        var runner = Runner();
        GrantRestricted(ParityMethodologyScriptHandler.Permission, "Document.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, 209901), CancellationToken.None));

        Assert.Equal("err.ECR-AUTH-0403.scriptPreviewRestricted", denied.Details!["messageKey"]);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Parity_невидимий_документ_404_не_місяць_422_без_тексту_422()
    {
        Grant(ParityMethodologyScriptHandler.Permission, "Document.View");
        var runner = Runner();

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), 701, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));
        await Assert.ThrowsAsync<NotFoundException>(
            () => Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(701, 209901), CancellationToken.None));

        foreach (var badPeriod in new[] { 209913, 209900, 2099, 0 })
        {
            var refused = await RefusedAsync(
                Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, badPeriod), CancellationToken.None));
            Assert.Equal("err.ECR-CALC-0422.simulatePeriodInvalid", refused.Details!["messageKey"]);
        }

        // Версія без тексту.
        var empty = await RefusedAsync(
            Parity(runner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, 209901), CancellationToken.None));
        Assert.Equal("err.ECR-CALC-0422.scriptEmpty", empty.Details!["messageKey"]);

        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    // ---------- Preview ----------

    private PreviewMethodologyScriptHandler Preview(IScriptPreviewRunner? runner)
        => new(_drafts, Analyzer(), _access, _user, runner);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.3")]
    public async Task Preview_обмежений_доступ_відмова_діагностики_без_прогону_а_чистий_текст_кличе_рушій_з_межею_рядків()
    {
        var runner = Substitute.For<IScriptPreviewRunner>();
        runner.RunAsync(Arg.Any<ScriptPreviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScriptPreviewResult([]));

        GrantRestricted("Calculation.View", "Document.View");
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Preview(runner).HandleAsync(
                VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901), CancellationToken.None));

        Grant("Calculation.View", "Document.View");
        var broken = await Preview(runner).HandleAsync(
            VersionId,
            new PreviewMethodologyScriptRequest("rows from NO_SUCH_TABLE;\noutput OUT_A : t = 1;", DocumentId, 209901),
            CancellationToken.None);
        Assert.NotEmpty(broken.Diagnostics);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);

        await Preview(runner).HandleAsync(
            VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901, MaxRows: 100_000), CancellationToken.None);
        await runner.Received(1).RunAsync(
            Arg.Is<ScriptPreviewRequest>(r => r.MaxRows == PreviewMethodologyScriptHandler.MaxRowsLimit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-25")]
    public async Task Preview_і_Parity_без_права_Document_View_у_проєкті_документа_403_і_рушій_не_викликається()
    {
        var runner = Substitute.For<IScriptPreviewRunner>();
        runner.RunAsync(Arg.Any<ScriptPreviewRequest>(), Arg.Any<CancellationToken>()).Returns(new ScriptPreviewResult([]));
        var parityRunner = Runner();
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));
        _access.DocumentProjectIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns((int?)3);

        // Документ видимий (грант), Calculation.View є, Document.View — ні: перегляд віддав би значення входів рядків.
        // Мутація: прибрати `RequirePermissionAsync(… DocumentPermission …)` з обробника — рушій викликається, 200.
        Grant("Calculation.View", ParityMethodologyScriptHandler.Permission);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Preview(runner).HandleAsync(
                VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901), CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Parity(parityRunner).HandleAsync(VersionId, new ScriptParityRequestDto(DocumentId, 209901), CancellationToken.None));
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        await parityRunner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);

        // З правом — рушій викликається.
        Grant("Calculation.View", ParityMethodologyScriptHandler.Permission, PreviewMethodologyScriptHandler.DocumentPermission);
        await Preview(runner).HandleAsync(VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901), CancellationToken.None);
        await runner.Received(1).RunAsync(Arg.Any<ScriptPreviewRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Preview_що_не_вклався_в_час_дає_422_scriptPreviewTimeout_і_скасовує_прогін()
    {
        var cancelled = false;
        var runner = Substitute.For<IScriptPreviewRunner>();
        runner.RunAsync(Arg.Any<ScriptPreviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }

                return new ScriptPreviewResult([]);
            });
        Grant("Calculation.View", "Document.View");
        var handler = new PreviewMethodologyScriptHandler(_drafts, Analyzer(), _access, _user, runner)
        {
            RunTimeout = TimeSpan.FromMilliseconds(200),
        };

        // Мутація: прибрати CancelAfter — прогін ніхто не перериває (тест не завершується).
        var refused = await RefusedAsync(
            handler.HandleAsync(VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901), CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.scriptPreviewTimeout", refused.Details!["messageKey"]);
        Assert.True(cancelled, "токен прогону мав бути скасований");

        // Скасування самим клієнтом — не «час вийшов», а звичайне скасування.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(VersionId, new PreviewMethodologyScriptRequest(Clean, DocumentId, 209901), aborted.Token));
    }

    // ---------- розмір тексту ----------

    private static string Huge() => new('x', ScriptAnalyzer.MaxTextLength + 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-5.1")]
    public async Task Завеликий_текст_відмовляє_422_у_Save_Validate_Preview_FormatConst_і_нічого_не_компілює()
    {
        Grant(SaveMethodologyScriptHandler.Permission, "Calculation.View", "Document.View");
        var runner = Substitute.For<IScriptPreviewRunner>();
        _version.SetScript(Clean, ScriptCompiler.Hash(Clean));

        var save = await RefusedAsync(Save().HandleAsync(VersionId, Huge(), null, CancellationToken.None));
        var validate = await RefusedAsync(
            new ValidateMethodologyScriptHandler(_drafts, Analyzer(), _access, _user).HandleAsync(VersionId, Huge(), CancellationToken.None));
        var preview = await RefusedAsync(
            Preview(runner).HandleAsync(VersionId, new PreviewMethodologyScriptRequest(Huge(), DocumentId, 209901), CancellationToken.None));
        var format = await RefusedAsync(new FormatScriptConstHandler(_access, _user).HandleAsync(Huge(), CancellationToken.None));

        foreach (var refused in new[] { save, validate, preview, format })
        {
            Assert.Equal("err.ECR-CALC-0422.scriptTooLarge", refused.Details!["messageKey"]);
        }

        Assert.Equal(Clean, _version.ScriptText);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().FindLiveTablesByCodeAsync(default!, default, default);

        // Рівно на межі — ще текст (діагностики можливі, відмови за розміром немає).
        var edge = new string(' ', ScriptAnalyzer.MaxTextLength - 1) + "x";
        var saved = await Save().HandleAsync(VersionId, edge, null, CancellationToken.None);
        Assert.Equal(edge, _version.ScriptText);
        Assert.NotNull(saved);
    }
}
