using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-12: «найпізніша» версія бібліотеки в пакеті і в базі — за одним
/// правилом, і це правило не ставить <c>V10</c> перед <c>V9</c>.
/// </summary>
/// <remarks>
/// ⛔ Що було. З пакета версії бібліотеки сортувались порядково
/// (<c>"V10" &lt; "V9"</c>), з бази — за <c>Id</c>. За ≥ 10 версій бібліотеки
/// методологія копіювала константу старішої версії, а той самий вміст,
/// імпортований з бібліотекою і без неї, давав <c>conflict</c> замість
/// <c>unchanged</c>.
/// </remarks>
public sealed class MethodologyPackageLibraryVersionTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly UnitCatalogSnapshot Units = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = new UnitRef(7, "one", 7),
            ["t"] = new UnitRef(8, "t", 1),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static MethodologyPackageFormulaDto Formula(string name, string text, string arguments)
        => new(name, "1", arguments, text, "2023-12-31T19:00:00Z", "9999-02-19T19:00:00Z", IsAvailable: true, Report: string.Empty);

    private static MethodologyPackageVersionDto LibraryVersion(string version, string value)
        => new(
            version,
            [Formula("Shared", "@X", "@X")],
            [new MethodologyPackageConstantDto("EF", string.Empty, "t", [new(string.Empty, "1", value, null, null)])]);

    private static MethodologyPackageMethodologyDto Owner()
        => new("M1", [new MethodologyPackageVersionDto("V1", [Formula("F1", "@X * CST.EF", "@X;CST.EF")], [])]);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("V9", "V10", "V10")]
    [InlineData("V10", "V9", "V10")]
    [InlineData("1.9.0.0", "1.10.0.0", "1.10.0.0")]
    public void Константа_бібліотеки_з_пакета_береться_з_пізнішої_версії(string first, string second, string newest)
    {
        var library = new MethodologyPackageMethodologyDto(
            "Common", [LibraryVersion(first, first == newest ? "2" : "1"), LibraryVersion(second, second == newest ? "2" : "1")]);

        var plan = MethodologyPackagePlanner.Plan(
            new MethodologyPackageDto(MethodologyPackagePlanner.FormatName, 1, "Common", [library, Owner()], []),
            new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase), Units, Atyrau);

        Assert.Empty(plan.Blockers);
        var copied = Assert.Single(plan.Methodologies.Single(m => m.Code == "M1").Versions.Single().Content.Constants);
        Assert.Equal(2m, copied.Value);
        Assert.Equal($"AF Common/{newest}", copied.Source);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Константа_бібліотеки_з_бази_береться_з_V10_а_не_з_більшого_Id()
    {
        static ImportConstantContent Row(decimal value)
            => new("EF", ConstantKind.Numeric, value, null, 8, null, new DateOnly(2024, 1, 1), null, "AF Common");

        var library = new ExistingMethodology(
            90, "Common", MethodologyKind.Library,
            [
                new ExistingMethodologyVersion(91, "V10", IsDraft: false,
                    new ImportVersionContent([new ImportFormulaContent("Shared", "@X", "@X")], [Row(2m)], [])),
                new ExistingMethodologyVersion(92, "V9", IsDraft: false,
                    new ImportVersionContent([new ImportFormulaContent("Shared", "@X", "@X")], [Row(1m)], [])),
            ]);

        var plan = MethodologyPackagePlanner.Plan(
            new MethodologyPackageDto(MethodologyPackagePlanner.FormatName, 1, "Common", [Owner()], []),
            new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase) { ["Common"] = library },
            Units, Atyrau);

        Assert.Empty(plan.Blockers);
        var copied = Assert.Single(plan.Methodologies.Single().Versions.Single().Content.Constants);
        Assert.Equal(2m, copied.Value);
    }
}
