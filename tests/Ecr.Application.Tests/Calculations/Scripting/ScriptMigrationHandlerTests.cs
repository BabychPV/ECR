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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Перенесення на живій базі (MS 8.5) на рівні обробників: <c>apply</c> (відбиток джерела з бази, сухий прогін,
/// відмови по версіях, журнал) і <c>switch</c> (причина, стан версій, група бібліотека+імпортери, склад зі скрипта
/// проти наявного, повернення на формули).
/// </summary>
public sealed class ScriptMigrationHandlerTests
{
    private const int Alpha = 5;
    private const int AlphaV = 50;
    private const int Library = 6;
    private const int LibraryV = 60;
    private const string Text = "rows from T1;\noutput OUT_A : t = row.Hours;";

    private static readonly DateTime Now = new(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _store;
    private readonly IVersionSourceReader _reader = Substitute.For<IVersionSourceReader>();
    private readonly IScriptMigrationStore _migration = Substitute.For<IScriptMigrationStore>();
    private readonly IFormulaLinkRebuilder _links = Substitute.For<IFormulaLinkRebuilder>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Methodology _alpha = ScriptTestKit.NewMethodology("ALPHA", Alpha);
    private readonly Methodology _library = ScriptTestKit.NewMethodology("LIB", Library, MethodologyKind.Library);
    private readonly MethodologyVersion _alphaVersion;
    private readonly MethodologyVersion _libraryVersion;
    private readonly VersionSource _source = new() { MethodologyCode = "ALPHA" };

    public ScriptMigrationHandlerTests()
    {
        _store = ScriptTestKit.Store(ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "OUT_A", CellDataType.Calculated)]));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(ApplyScriptPackageHandler.Permission).Build());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _alphaVersion = ScriptTestKit.NewVersion(_alpha, AlphaV, MethodologyEngine.Formulas);
        _libraryVersion = ScriptTestKit.NewVersion(_library, LibraryV, MethodologyEngine.Formulas);

        _drafts.FindByCodeAsync("ALPHA", Arg.Any<CancellationToken>()).Returns(_alpha);
        _drafts.FindAsync(Alpha, Arg.Any<CancellationToken>()).Returns(_alpha);
        _drafts.FindByCodeAsync("LIB", Arg.Any<CancellationToken>()).Returns(_library);
        _drafts.FindAsync(Library, Arg.Any<CancellationToken>()).Returns(_library);
        _drafts.FindVersionAsync(AlphaV, Arg.Any<CancellationToken>()).Returns(_alphaVersion);
        _drafts.FindVersionAsync(LibraryV, Arg.Any<CancellationToken>()).Returns(_libraryVersion);
        _store.FindByVersionAsync(AlphaV, Arg.Any<CancellationToken>()).Returns(_alpha);
        _store.FindByVersionAsync(LibraryV, Arg.Any<CancellationToken>()).Returns(_library);
        _reader.LoadAsync(AlphaV, Arg.Any<CancellationToken>()).Returns(_ =>
            new LoadedVersion(
                new VersionRef(AlphaV, Alpha, "ALPHA", "1.0", null, TemplateVersionStatus.Draft, CalculationLevel.Configuration,
                    MethodologyEngine.Formulas, MethodologyKind.DataDriven),
                _source,
                []));
        _store.ResolveImportsAsync(Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyLibrary>>([]));
        _store.GetOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyOutput>>([]));
        _store.GetSubstanceCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>()));
        _migration.GetImporterVersionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ImporterVersion>>([]));
    }

    private ScriptAnalyzer Analyzer()
        => new(new ScriptSourceCompiler(_drafts, _store), ScriptTestKit.Checker(_store, _drafts), _clock);

    private ApplyScriptPackageHandler Apply()
        => new(_drafts, _reader, Analyzer(), _uow, _audit, _access, _user, _clock);

    private SwitchScriptEngineHandler Switch()
        => new(_store, _drafts, _migration, Analyzer(),
            new ScriptMaterializer(_store, _drafts, ScriptTestKit.Catalog(), Substitute.For<ICalculationBindingStore>()),
            _links,
            _uow, _audit, _access, _user, _clock);

    private void Publish(MethodologyVersion version, string? shadow = Text)
    {
        if (shadow is not null)
        {
            version.SetScript(shadow, [1, 2, 3]);
        }

        version.Publish(9, "reason", new DateOnly(2099, 1, 1), testsPassed: true, Now);
    }

    private ApplyScriptPackageRequest Package(string? fingerprint = null, string text = Text, string? effectiveFrom = null)
        => new(
            "ecr-methodology-script-package", 3,
            [new ScriptPackageEntryDto("ALPHA", "1.0", effectiveFrom, text, fingerprint ?? Fingerprint.ComputeHex(_source))]);

    // ---------- apply ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_сухий_прогін_нічого_не_пише_а_запис_кладе_тінь_і_подію_журналу()
    {
        var dry = await Apply().HandleAsync(Package(), dryRun: true, CancellationToken.None);

        var entry = Assert.Single(dry.Entries);
        Assert.Equal(("wouldApply", null), (entry.Status, entry.Reason));
        Assert.Equal(1, dry.Applied);
        Assert.Null(_alphaVersion.ScriptText);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        await _audit.DidNotReceiveWithAnyArgs().WritePublicationEventAsync(default!, default);

        var applied = await Apply().HandleAsync(Package(), dryRun: false, CancellationToken.None);

        Assert.Equal("applied", Assert.Single(applied.Entries).Status);
        Assert.Equal(Text, _alphaVersion.ScriptText);
        Assert.Equal(MethodologyEngine.Formulas, _alphaVersion.Engine);
        await _audit.Received(1).WritePublicationEventAsync(
            Arg.Is<PublicationEventRecord>(e => e.EntityId == AlphaV && e.ChangeReason == "ScriptMigration.apply" && e.ChangedByUserId == 9),
            Arg.Any<CancellationToken>());
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_відбиток_джерела_не_збігся_версію_відхилено_з_причиною_і_нічого_не_записано()
    {
        var response = await Apply().HandleAsync(Package(fingerprint: new string('0', 64)), dryRun: false, CancellationToken.None);

        var entry = Assert.Single(response.Entries);
        Assert.Equal("refused", entry.Status);
        Assert.Equal("scriptMigration.refused.fingerprintMismatch", entry.Reason);
        Assert.Equal(1, response.Refused);
        Assert.Null(_alphaVersion.ScriptText);
        await _audit.DidNotReceiveWithAnyArgs().WritePublicationEventAsync(default!, default);

        // Зміна самої бази (інше джерело) дає інший відбиток — відмова й за збігу з пакетом ДО зміни.
        var packed = Package();
        _source.Constants.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var changed = new VersionSource { MethodologyCode = "ALPHA", Substances = ["NOX"] };
        _reader.LoadAsync(AlphaV, Arg.Any<CancellationToken>()).Returns(_ =>
            new LoadedVersion(
                new VersionRef(AlphaV, Alpha, "ALPHA", "1.0", null, TemplateVersionStatus.Draft, CalculationLevel.Configuration,
                    MethodologyEngine.Formulas, MethodologyKind.DataDriven),
                changed,
                []));
        var after = await Apply().HandleAsync(packed, dryRun: false, CancellationToken.None);
        Assert.Equal("scriptMigration.refused.fingerprintMismatch", Assert.Single(after.Entries).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_відмови_по_версіях_не_зупиняють_решту()
    {
        var fingerprint = Fingerprint.ComputeHex(_source);
        ApplyScriptPackageRequest Of(params ScriptPackageEntryDto[] entries) => new(null, 3, entries);

        var mixed = await Apply().HandleAsync(
            Of(
                new ScriptPackageEntryDto("NOPE", "1.0", null, Text, "x"),
                new ScriptPackageEntryDto("ALPHA", "9.9", null, Text, "x"),
                new ScriptPackageEntryDto("ALPHA", "1.0", null, Text, fingerprint),
                new ScriptPackageEntryDto("ALPHA", "1.0", null, Text, fingerprint)),
            dryRun: true,
            CancellationToken.None);
        Assert.Equal(
            [
                "scriptMigration.refused.versionNotFound",
                "scriptMigration.refused.versionNotFound",
                null,
                "scriptMigration.refused.duplicateEntry",
            ],
            mixed.Entries.Select(e => e.Reason));
        Assert.Equal((1, 3), (mixed.Applied, mixed.Refused));

        var effective = await Apply().HandleAsync(
            Of(new ScriptPackageEntryDto("ALPHA", "1.0", "2020-05-05", Text, fingerprint)), dryRun: true, CancellationToken.None);
        Assert.Equal("scriptMigration.refused.effectiveFromMismatch", Assert.Single(effective.Entries).Reason);

        var invalid = await Apply().HandleAsync(
            Of(new ScriptPackageEntryDto("ALPHA", "1.0", null, "rows from NO_TABLE;\noutput O : t = 1;", fingerprint)),
            dryRun: true,
            CancellationToken.None);
        var refused = Assert.Single(invalid.Entries);
        Assert.Equal("scriptMigration.refused.textInvalid", refused.Reason);
        Assert.Contains(refused.Details!, d => d.StartsWith("unknownTable", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_завеликий_текст_відхиляється_до_компіляції_з_назвою_межі()
    {
        var fingerprint = Fingerprint.ComputeHex(_source);
        var huge = "//" + new string('a', ScriptAnalyzer.MaxTextLength);

        var response = await Apply().HandleAsync(
            new ApplyScriptPackageRequest(null, 3, [new ScriptPackageEntryDto("ALPHA", "1.0", null, huge, fingerprint)]),
            dryRun: true,
            CancellationToken.None);

        var refused = Assert.Single(response.Entries);
        Assert.Equal("scriptMigration.refused.textInvalid", refused.Reason);
        Assert.Equal(
            [$"limit: {ScriptAnalyzer.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters"],
            refused.Details);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_імпортер_бачить_бібліотеку_з_того_самого_пакета_незалежно_від_порядку()
    {
        Publish(_libraryVersion, shadow: null);
        var libSource = new VersionSource { MethodologyCode = "LIB", IsLibrary = true };
        _reader.LoadAsync(LibraryV, Arg.Any<CancellationToken>()).Returns(_ =>
            new LoadedVersion(
                new VersionRef(LibraryV, Library, "LIB", "1.0", new DateOnly(2099, 1, 1), TemplateVersionStatus.Published,
                    CalculationLevel.Configuration, MethodologyEngine.Formulas, MethodologyKind.Library),
                libSource,
                []));

        var request = new ApplyScriptPackageRequest(
            null, 3,
            [
                // Імпортер ПЕРЕД бібліотекою: перший прохід його відхиляє, другий — приймає.
                new ScriptPackageEntryDto("ALPHA", "1.0", null, "import LIB;\n" + Text, Fingerprint.ComputeHex(_source)),
                new ScriptPackageEntryDto("LIB", "1.0", null, "function twice(a) = a * 2;", Fingerprint.ComputeHex(libSource)),
            ]);

        var dry = await Apply().HandleAsync(request, dryRun: true, CancellationToken.None);
        Assert.Equal(["wouldApply", "wouldApply"], dry.Entries.Select(e => e.Status));

        var applied = await Apply().HandleAsync(request, dryRun: false, CancellationToken.None);
        Assert.Equal(["applied", "applied"], applied.Entries.Select(e => e.Status));
        Assert.Equal("function twice(a) = a * 2;", _libraryVersion.ScriptText);
        Assert.StartsWith("import LIB;", _alphaVersion.ScriptText, StringComparison.Ordinal);

        // Без бібліотеки в пакеті (і без її тіні в базі) імпортер не скомпілюється.
        var lone = await Apply().HandleAsync(
            new ApplyScriptPackageRequest(null, 3, [request.Entries![0]]), dryRun: true, CancellationToken.None);
        var refused = Assert.Single(lone.Entries);
        Assert.Equal("scriptMigration.refused.textInvalid", refused.Reason);
        Assert.Contains(refused.Details!, d => d.StartsWith("libraryNotScript", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_версія_не_на_формулах_і_чужий_формат_пакета_відхиляються()
    {
        Publish(_alphaVersion);
        _alphaVersion.SwitchEngineForMigration(MethodologyEngine.Script);

        var response = await Apply().HandleAsync(Package(), dryRun: false, CancellationToken.None);
        Assert.Equal("scriptMigration.refused.notFormulaVersion", Assert.Single(response.Entries).Reason);

        var wrong = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Apply().HandleAsync(new ApplyScriptPackageRequest("other", 3, Package().Entries), false, CancellationToken.None));
        Assert.Equal("err.ECR-CALC-0422.migrationPackageInvalid", wrong.Details!["messageKey"]);

        var empty = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Apply().HandleAsync(new ApplyScriptPackageRequest(null, 3, []), false, CancellationToken.None));
        Assert.Equal("err.ECR-CALC-0422.migrationPackageInvalid", empty.Details!["messageKey"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(null)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_пакет_іншого_номера_формату_відхиляється_явною_помилкою_і_нічого_не_пише(int? formatVersion)
    {
        var request = new ApplyScriptPackageRequest("ecr-methodology-script-package", formatVersion, Package().Entries);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Apply().HandleAsync(request, dryRun: false, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.migrationPackageFormatUnsupported", error.Details!["messageKey"]);
        Assert.Equal("3", error.Details["supported"]);
        Assert.Null(_alphaVersion.ScriptText);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_пакет_формату_3_приймається()
    {
        var request = new ApplyScriptPackageRequest("ecr-methodology-script-package", 3, Package().Entries);

        var response = await Apply().HandleAsync(request, dryRun: true, CancellationToken.None);

        Assert.Equal("wouldApply", Assert.Single(response.Entries).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Без_права_Calculation_Publish_обидва_обробники_відмовляють()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Apply().HandleAsync(Package(), true, CancellationToken.None));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "why"), CancellationToken.None));
    }

    // ---------- switch ----------

    private static async Task<string?> KeyAsync(Func<Task> call)
    {
        var error = await Record.ExceptionAsync(call);
        return error switch
        {
            BusinessRuleException business => business.Details?["messageKey"] as string,
            DomainException domain => domain.Details?["messageKey"] as string,
            _ => throw new Xunit.Sdk.XunitException($"Очікувалась відмова з ключем, є: {error?.GetType().Name ?? "нічого"}."),
        };
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_вимагає_причину_рушій_версії_й_опубліковану_версію()
    {
        Assert.Equal(
            "err.ECR-CALC-0422.switchReasonRequired",
            await KeyAsync(() => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "  "), default)));
        Assert.Equal(
            "err.ECR-CALC-0422.switchEngineInvalid",
            await KeyAsync(() => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Cobol", "why"), default)));
        Assert.Equal(
            "err.ECR-CALC-0422.switchVersionsRequired",
            await KeyAsync(() => Switch().HandleAsync(new SwitchScriptEngineRequest([], "Script", "why"), default)));

        // Чернетка не перемикається цим шляхом.
        Assert.Equal(
            "err.ECR-CALC-0409.scriptSwitchNotPublished",
            await KeyAsync(() => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "why"), default)));

        await Assert.ThrowsAsync<NotFoundException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([404], "Script", "why"), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_на_скрипт_без_тексту_відмовляє_з_переліком_проблем()
    {
        Publish(_alphaVersion, shadow: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "why"), default));

        Assert.Equal("err.ECR-CALC-0422.switchChecksFailed", error.Details!["messageKey"]);
        var problem = Assert.Single((IReadOnlyList<PublishProblem>)error.Details["problems"]!);
        Assert.Equal("switch.problem.scriptMissing", problem.MessageKey);
        Assert.Equal(MethodologyEngine.Formulas, _alphaVersion.Engine);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_склад_зі_скрипта_відрізняється_від_наявного_відмова_називає_різницю()
    {
        Publish(_alphaVersion);
        _store.GetOutputsAsync(AlphaV, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyOutput>>([Output("OUT_OTHER", ScriptTestKit.Tonne, false)]));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "why"), default));

        Assert.Equal("err.ECR-CALC-0422.switchChecksFailed", error.Details!["messageKey"]);
        var problem = Assert.Single((IReadOnlyList<PublishProblem>)error.Details["problems"]!);
        Assert.Equal("switch.problem.outputsDiffer", problem.MessageKey);
        Assert.Contains("OUT_A", problem.Args["onlyInScript"], StringComparison.Ordinal);
        Assert.Contains("OUT_OTHER", problem.Args["onlyStored"], StringComparison.Ordinal);
        await _store.DidNotReceiveWithAnyArgs().ReplaceScriptReadTablesAsync(default, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WritePublicationEventAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_туди_й_назад_пише_таблиці_читання_залежності_і_журнал_із_причиною()
    {
        Publish(_alphaVersion);
        _store.GetOutputsAsync(AlphaV, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyOutput>>([Output("OUT_A", ScriptTestKit.Tonne, false)]));

        var response = await Switch().HandleAsync(
            new SwitchScriptEngineRequest([AlphaV, AlphaV], "script", " планова заміна "), default);

        Assert.Equal("Script", response.Engine);
        Assert.Equal([AlphaV], response.Versions.Select(v => v.VersionId));
        Assert.Equal(MethodologyEngine.Script, _alphaVersion.Engine);
        await _store.Received(1).ReplaceScriptReadTablesAsync(AlphaV, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceDependenciesAsync(Alpha, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WritePublicationEventAsync(
            Arg.Is<PublicationEventRecord>(e => e.EntityId == AlphaV && e.ChangeReason == "планова заміна" && e.ChangedByUserId == 9),
            Arg.Any<CancellationToken>());

        // Назад: рушій формул, таблиці читання знято, текст лишився.
        _store.ClearReceivedCalls();
        var back = await Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Formulas", "відкат"), default);

        Assert.Equal("Formulas", back.Engine);
        Assert.Equal(MethodologyEngine.Formulas, _alphaVersion.Engine);
        Assert.Equal(Text, _alphaVersion.ScriptText);
        await _store.Received(1).ReplaceScriptReadTablesAsync(
            AlphaV, Arg.Is<IReadOnlyList<string>>(t => t.Count == 0), Arg.Any<CancellationToken>());

        // F13a: ребра методологій і довідників, переписані скриптом, перебудовано за формулами — і лише при поверненні
        // (на Script їх пише матеріалізація).
        await _links.Received(1).RebuildAsync(_alpha, _alphaVersion, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_бібліотека_й_імпортери_лише_разом_відмова_називає_відсутні_версії()
    {
        Publish(_alphaVersion, "import LIB;\nrows from T1;\noutput OUT_A : t = row.Hours;");
        Publish(_libraryVersion, "function twice(a) = a * 2;");

        // Імпортер alpha оголосив бібліотеку; версія бібліотеки ще на формулах.
        _store.ResolveImportsAsync(AlphaV, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MethodologyLibrary>>([new MethodologyLibrary(Library, "LIB", LibraryV, [])]));
        _migration.GetImporterVersionsAsync(Library, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ImporterVersion>>(
                [new ImporterVersion(AlphaV, Alpha, "ALPHA", "1.0", MethodologyEngine.Formulas, TemplateVersionStatus.Published)]));

        // Лише імпортер: відсутня версія бібліотеки.
        var onlyImporter = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([AlphaV], "Script", "why"), default));
        Assert.Equal("err.ECR-CALC-0422.switchGroupIncomplete", onlyImporter.Details!["messageKey"]);
        Assert.Contains("LIB 1.0", Assert.Single((IReadOnlyList<string>)onlyImporter.Details["missing"]!), StringComparison.Ordinal);

        // Лише бібліотека: відсутній імпортер.
        var onlyLibrary = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Switch().HandleAsync(new SwitchScriptEngineRequest([LibraryV], "Script", "why"), default));
        Assert.Equal("err.ECR-CALC-0422.switchGroupIncomplete", onlyLibrary.Details!["messageKey"]);
        Assert.Contains("ALPHA 1.0", Assert.Single((IReadOnlyList<string>)onlyLibrary.Details["missing"]!), StringComparison.Ordinal);

        Assert.Equal(MethodologyEngine.Formulas, _alphaVersion.Engine);
        Assert.Equal(MethodologyEngine.Formulas, _libraryVersion.Engine);
    }

    private static MethodologyOutput Output(string code, int unitId, bool perSubstance)
    {
        var output = new MethodologyOutput(AlphaV, EcrCode.Create(code), unitId, 1);
        output.SetPerSubstance(perSubstance);
        return output;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Apply_текст_понад_ліміт_відмова_по_версії_без_запису_тіні()
    {
        var huge = new string('x', ScriptAnalyzer.MaxTextLength + 1);

        var result = await Apply().HandleAsync(Package(text: huge), dryRun: false, CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(("refused", "scriptMigration.refused.textInvalid"), (entry.Status, entry.Reason));
        Assert.Null(_alphaVersion.ScriptText);
        await _audit.DidNotReceiveWithAnyArgs().WritePublicationEventAsync(default!, default);
    }
}
