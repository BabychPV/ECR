using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-09: межі, які відхиляє запис, план імпорту пакета називає блокером
/// ДО застосування, а дубль назви в іншому регістрі — блокер, а не 500.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>LoadExistingAsync</c> будував словник «назва → версії» через
/// <c>ToDictionary(OrdinalIgnoreCase)</c> ДО планувальника (і в сухому прогоні):
/// пакет з <c>HSE400</c> і <c>hse400</c> падав ArgumentException → 500, а блокер
/// <c>duplicateMethodology</c> був недосяжний. Номер версії понад 20 символів і
/// вираз понад 4000 сухий прогін показував як <c>created</c>, застосування — 500/422.
/// </remarks>
public sealed class MethodologyPackageLimitsTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly UnitCatalogSnapshot Units = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["one"] = new UnitRef(7, "one", 7) },
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, ExistingMethodology> Nothing =
        new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

    private static MethodologyPackageFormulaDto Formula(string name, string text)
        => new(name, "1", "@X", text, "2023-12-31T19:00:00Z", "9999-02-19T19:00:00Z", IsAvailable: true, Report: string.Empty);

    private static MethodologyPackageMethodologyDto Methodology(string name, string version, params MethodologyPackageFormulaDto[] formulas)
        => new(name, [new MethodologyPackageVersionDto(version, formulas, [])]);

    private static MethodologyPackageDto Package(params MethodologyPackageMethodologyDto[] methodologies)
        => new(MethodologyPackagePlanner.FormatName, 1, "Common", methodologies, []);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Дубль_методології_в_іншому_регістрі_дає_блокер_а_не_500()
    {
        var package = Package(
            Methodology("HSE400", "V1", Formula("F1", "@X")),
            Methodology("hse400", "V2", Formula("F1", "@X")));

        var report = await Handler().HandleAsync(package, dryRun: true, timeZoneId: null, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.Kind == "duplicateMethodology");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Задовгий_номер_версії_дає_блокер()
    {
        var version = new string('V', CreateMethodologyVersionHandler.MaxVersionLength + 1);

        var plan = MethodologyPackagePlanner.Plan(Package(Methodology("M1", version, Formula("F1", "@X"))), Nothing, Units, Atyrau);

        Assert.Contains(plan.Blockers, b => b.Kind == "invalidVersion" && b.Version == version);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Номер_версії_на_межі_приймається()
    {
        var version = new string('V', CreateMethodologyVersionHandler.MaxVersionLength);

        var plan = MethodologyPackagePlanner.Plan(Package(Methodology("M1", version, Formula("F1", "@X"))), Nothing, Units, Atyrau);

        Assert.DoesNotContain(plan.Blockers, b => b.Kind == "invalidVersion");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Задовгий_вираз_формули_дає_блокер()
    {
        var text = "@X" + string.Concat(Enumerable.Repeat(" + 1", MethodologyFormula.MaxExpressionLength / 4));

        var plan = MethodologyPackagePlanner.Plan(Package(Methodology("M1", "V1", Formula("F1", text))), Nothing, Units, Atyrau);

        Assert.True(text.Length > MethodologyFormula.MaxExpressionLength);
        Assert.Contains(plan.Blockers, b => b.Kind == "formulaTooLong" && b.Subject == "F1");
    }

    private static ImportMethodologyPackageHandler Handler()
    {
        var drafts = Substitute.For<IMethodologyDraftStore>();
        drafts.FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Methodology?)null);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(Units);

        var access = Substitute.For<Ecr.Application.Security.IAccessDecisionService>();
        access.BuildProfileAsync(1, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 1 }
            .Permission(ImportMethodologyPackageHandler.Permission)
            .Permission(ImportMethodologyPackageHandler.ConstantPermission)
            .Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        return new ImportMethodologyPackageHandler(
            drafts,
            Substitute.For<IMethodologyStore>(),
            units,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IAuditWriter>(),
            access,
            user,
            Substitute.For<IClock>());
    }
}
