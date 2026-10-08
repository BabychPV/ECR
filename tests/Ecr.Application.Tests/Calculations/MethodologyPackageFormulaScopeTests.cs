// tests/Ecr.Application.Tests/Calculations/MethodologyPackageFormulaScopeTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// L2-1: область формули (<c>scope</c>) у пакеті імпорту AF — розбирається планувальником, невідоме значення
/// блокує, а сама область входить у вміст версії, який порівнює повторний імпорт.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати область з <c>ImportVersionContent.Keys</c> —
/// <see cref="Пакет_з_іншою_областю_формули_конфліктує_з_наявною_чернеткою"/> червоний;
/// не розбирати <c>scope</c> у планувальнику — <see cref="Невідома_область_формули_блокує_пакет"/> червоний.
/// Пакети будуються з JSON, як їх шле зовнішній експортер (формат <c>ecr-methodology-package v1</c>).
/// </remarks>
public sealed class MethodologyPackageFormulaScopeTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, ExistingMethodology> Nothing =
        new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

    private static MethodologyPackageDto Package(string? scopeJson)
    {
        var scope = scopeJson is null ? string.Empty : $",\"scope\":{scopeJson}";
        var json = $$"""
            {"format":"ecr-methodology-package","version":1,"library":"Common","methodologies":[
              {"name":"M1","versions":[{"version":"V1","constants":[],"formulas":[
                {"name":"F","version":"1","arguments":"","text":"1","startDate":"2023-12-31T19:00:00Z",
                 "endDate":"9999-02-19T19:00:00Z","isAvailable":true,"report":""{{scope}}}
              ]}]}],"blockers":[]}
            """;

        return JsonSerializer.Deserialize<MethodologyPackageDto>(json, Web)!;
    }

    private static UnitCatalogSnapshot Units()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.Ordinal) { ["one"] = new UnitRef(7, "one", 7) },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private static MethodologyImportPlan Plan(MethodologyPackageDto package, IReadOnlyDictionary<string, ExistingMethodology>? existing = null)
        => MethodologyPackagePlanner.Plan(package, existing ?? Nothing, Units(), Atyrau);

    private static Dictionary<string, ExistingMethodology> DraftFrom(MethodologyImportPlan plan)
    {
        var version = plan.Methodologies.Single().Versions.Single();

        return new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase)
        {
            ["M1"] = new ExistingMethodology(
                5, "M1", MethodologyKind.DataDriven, [new ExistingMethodologyVersion(50, "V1", true, version.Content)]),
        };
    }

    [Theory]
    [InlineData("\"Row\"")]
    [InlineData("\"row\"")]
    [InlineData("\"Substance\"")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відома_область_формули_або_її_відсутність_не_блокує_пакет(string scopeJson)
    {
        var plan = Plan(Package(scopeJson));

        Assert.Empty(plan.Blockers);
        Assert.Equal(MethodologyPackagePlanner.Create, plan.Methodologies.Single().Versions.Single().Action);
    }

    [Theory]
    [InlineData("\"Bogus\"")]
    [InlineData("\"Both\"")]
    [InlineData("\"1\"")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Невідома_область_формули_блокує_пакет(string scopeJson)
    {
        var plan = Plan(Package(scopeJson));

        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal("invalidFormulaScope", blocker.Kind);
        Assert.Equal("F", blocker.Subject);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Пакет_з_іншою_областю_формули_конфліктує_з_наявною_чернеткою()
    {
        var existing = DraftFrom(Plan(Package("\"Row\"")));

        var sameScope = Plan(Package("\"row\""), existing);
        Assert.Equal("unchanged", sameScope.Methodologies.Single().Versions.Single().Action);

        var otherScope = Plan(Package(null), existing);
        Assert.Equal("conflict", otherScope.Methodologies.Single().Versions.Single().Action);
        Assert.Contains(otherScope.Conflicts, c => c.Kind == "draftDiffers");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Пакет_без_області_збігається_з_пакетом_Substance_бо_це_типове_значення()
    {
        var existing = DraftFrom(Plan(Package(null)));

        var explicitSubstance = Plan(Package("\"Substance\""), existing);

        Assert.Equal("unchanged", explicitSubstance.Methodologies.Single().Versions.Single().Action);
    }
}
