using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ФВ-6.8: храповик на прямі <c>profile.Has(</c> в Application. Рішення
/// «чи можна» мусить іти через <c>PermissionCheck</c>/<c>AccessDecisionService</c>
/// (там повертається причина відмови), а не через голе <c>bool</c> з профілю.
/// </summary>
/// <remarks>
/// ⚠ Поточний борг — ПОІМЕННИЙ перелік <see cref="Debt"/>. Новий файл із прямим
/// <c>profile.Has(</c> — червоний; файл, що позбувся виклику, — теж червоний
/// (перелік мусить зменшуватись, а не гнити). Пошук по <c>CodeLines</c>:
/// коментарі й рядкові літерали ігноруються.
/// Мутація (прогнано): додати <c>profile.Has(</c> у новий файл Application —
/// перший тест червоний.
/// </remarks>
public sealed partial class DirectProfileHasRatchetTests
{
    /// <summary>Файли з прямим <c>profile.Has(</c> на момент введення храповика.</summary>
    private static readonly string[] Debt =
    [
        "src/Ecr.Application/Search/SearchHandler.cs",
        "src/Ecr.Application/Security/AccessDiagnostics.cs",
        "src/Ecr.Application/Security/PermissionCheck.cs",
        "src/Ecr.Application/Security/ResourceGrantHandlers.cs",
        "src/Ecr.Application/Security/RoleAndUserHandlers.cs",
    ];

    [GeneratedRegex(@"\bprofile\.Has\(")]
    private static partial Regex DirectHas();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.8")]
    public void Нові_файли_Application_не_кличуть_profile_Has_напряму()
    {
        var fresh = Offenders().Except(Debt, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            fresh.Count == 0,
            "Прямий profile.Has( поза переліком боргу (ФВ-6.8): йди через PermissionCheck / "
            + "AccessDecisionService, щоб відмова мала причину:"
            + Environment.NewLine + string.Join(Environment.NewLine, fresh));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-6.8")]
    public void Перелік_боргу_не_містить_файлів_без_прямого_profile_Has()
    {
        var actual = Offenders();
        var gone = Debt.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(actual);
        Assert.True(
            gone.Count == 0,
            "Файл більше не кличе profile.Has( напряму — прибери його з Debt (борг зменшився):"
            + Environment.NewLine + string.Join(Environment.NewLine, gone));
    }

    private static HashSet<string> Offenders()
        => SourceTree.Production("Ecr.Application")
            .Where(f => f.CodeLines().Any(l => DirectHas().IsMatch(l.Text)))
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);
}
