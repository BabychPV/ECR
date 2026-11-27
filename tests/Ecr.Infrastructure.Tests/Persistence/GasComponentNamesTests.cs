// tests/Ecr.Infrastructure.Tests/Persistence/GasComponentNamesTests.cs
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Назва компонента складу газу (D-NEW, рев'ю 25.11): імпорт FLERT заводить запис <c>COMPONENT</c> з назвою, рівною коду
/// (<c>MMercaptan</c>), — така «назва» мусить відпасти, щоб клієнт показав типову з каталогу рядків; справжня назва, яку
/// адміністратор дав запису, — лишитися.
/// </summary>
/// <remarks>⛔ Доказ: прибрати умову «не дорівнює коду» → перший тест червоний (клієнт показав би «MMercaptan (MMercaptan)»).</remarks>
public sealed class GasComponentNamesTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-476")]
    public void Назва_рівна_коду_відкидається_порожня_теж()
    {
        var names = GasCompositionStore.NamesOf(
            "MMercaptan",
            new LocalizedText(new Dictionary<string, string> { ["en"] = "MMercaptan", ["ru"] = "  mmercaptan ", ["kz"] = " " }));

        Assert.Empty(names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D-476")]
    public void Справжня_назва_запису_лишається_за_мовами()
    {
        var names = GasCompositionStore.NamesOf(
            "MMercaptan",
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Methyl mercaptan ", ["ru"] = "MMercaptan" }));

        Assert.Equal(new Dictionary<string, string> { ["en"] = "Methyl mercaptan" }, names);
        Assert.Empty(GasCompositionStore.NamesOf("CO", display: null));
    }
}
