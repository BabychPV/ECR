// tests/Ecr.Calculations.Tests/Library/LibraryPublishGoldenTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Library;

/// <summary>
/// HSE301 L4, золотий тест: методологія з <c>!Common_M</c> і <c>!Common_W</c> публікується
/// СПРАВЖНІМ обробником над СПРАВЖНІМ модулем, і золотий набір сходиться з числами,
/// порахованими вручну, — без допуску.
/// </summary>
/// <remarks>
/// Руками: Common_M = 100 · 0.8 = 80 (RHO бібліотеки); M_total = 2 · 80 = 160;
/// Common_W = 80 · EF (0.1 для 901, 0.2 для 902); tons = Common_W + 0.5 (RHO викликача) —
/// 8.5 і 16.5. До HSE301 L ця публікація падала на <c>importedFormulaNotEvaluated</c>, а
/// прогін давав <c>#REF</c> і не писав жодного виходу.
/// </remarks>
public sealed class LibraryPublishGoldenTests
{
    private const int Author = 7;
    private const int Reviewer = 9;

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateTime Now = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.12")]
    public async Task Методологія_з_посиланням_у_бібліотеку_публікується_і_дає_точне_число()
    {
        var stand = new LibraryStand();
        var (methodology, version) = Aggregate();
        var edges = new List<IReadOnlyCollection<int>>();

        stand.Store.FindByVersionAsync(LibraryStand.CallerVersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        stand.Store.GetRulesAsync(LibraryStand.CallerVersionId, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyRule>());
        stand.Store.GetTestCasesAsync(LibraryStand.CallerVersionId, Arg.Any<CancellationToken>()).Returns(
        [
            new MethodologyTestCase(
                "golden-l",
                LibraryStand.Input(TraceLevel.Off),
                new Dictionary<string, decimal>
                {
                    ["M_total"] = 160m,
                    ["tons@901"] = 8.5m,
                    ["tons@902"] = 16.5m,
                },
                Tolerance: 0m),
        ]);

        // На дату чинності — та сама версія Common, що й на бізнес-дату прогону.
        var common = LibraryStand.CommonContent();
        stand.Store.ResolveImportsAsync(LibraryStand.CallerVersionId, From, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyLibrary> { common.Library });
        stand.Store.ResolveImportsAsync(LibraryStand.CommonVersionId, From, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyLibrary>());
        stand.Imports(LibraryStand.CallerVersionId, common);
        stand.Store.GetLibraryContentsAsync(LibraryStand.CallerVersionId, From, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyLibraryContent> { common });
        stand.Store.ReplaceDependenciesAsync(
                 Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 edges.Add([.. call.ArgAt<IReadOnlyCollection<int>>(1)]);
                 return Task.CompletedTask;
             });

        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<CalculationBinding>());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Reviewer);
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var handler = new PublishMethodologyHandler(
            stand.Module(), stand.Store, new RealFormulaEngine(), bindings, units,
            Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), access, user, clock);

        // Червоний золотий набір тут кинув би goldenSetDiverged з обома числами.
        await handler.HandleAsync(LibraryStand.CallerVersionId, "Посилання на Common", From, CancellationToken.None);

        Assert.True(version.IsPublished);
        Assert.Equal([LibraryStand.CommonId], Assert.Single(edges));

        // Ті самі числа — прогоном опублікованої версії, точно.
        var output = await stand.RunAsync(TraceLevel.Off);
        Assert.Equal(160m, Assert.Single(output.Values, v => v.OutputCode == "M_total").Value);
        Assert.Equal(
            [8.5m, 16.5m],
            output.Values.Where(v => v.OutputCode == "tons").OrderBy(v => v.SubstanceEntryId).Select(v => v.Value));
    }

    private static (Methodology Methodology, MethodologyVersion Version) Aggregate()
    {
        var methodology = new Methodology(
            EcrCode.Create("HSE400"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Gas" }));
        SetId(methodology, LibraryStand.CallerId);

        var version = new MethodologyVersion(LibraryStand.CallerId, "1.0", CalculationLevel.Configuration, Author, Now);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(version, LibraryStand.CallerVersionId);
        methodology.AddVersion(version);

        return (methodology, version);
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = Reviewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.Publish" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };
}
