using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Одиниці на межі інтеграції (ФВ-16.9, чекпойнт 07 §5).
/// </summary>
/// <remarks>
/// ⛔ Головне тут — не конверсія, а <b>зупинка</b>. Мовчазна конверсія «як
/// здається» дає правдоподібні числа, помилку в яких знайдуть через місяць на
/// звірці — коли звіт уже подано.
/// </remarks>
public sealed class SourceUnitConverterTests
{
    private const int KilogramId = 1;
    private const int TonneId = 2;
    private const int CubicMetreId = 3;

    private static UnitCatalogSnapshot Catalog() => new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["kg"] = new(KilogramId, "kg", DimensionId: 1, FactorToBase: 1m),
            ["t"] = new(TonneId, "t", DimensionId: 1, FactorToBase: 1000m),
            ["m3"] = new(CubicMetreId, "m3", DimensionId: 2, FactorToBase: 1m),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Конвертер із порожнім каталогом: знімок передається явно.</summary>
    /// <remarks>
    /// ⚠ Порт каталогу тут не використовується навмисно: усі перевірки
    /// працюють над ЯВНО переданим знімком. Тест, який залежав би від
    /// походження знімка, перевіряв би завантаження, а не конверсію.
    /// </remarks>
    private static SourceUnitConverter Converter()
        => new(new UnitConverter(), NSubstitute.Substitute.For<IUnitCatalog>());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Зміна_UOM_атрибута_в_джерелі_зупиняє_збір()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => SourceUnitConverter.EnsureDeclaredUnit(KilogramId, "t", Catalog(), @"\Site\Stack|Flow"));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Незнайома_одиниця_джерела_теж_зупиняє_збір()
    {
        // ⚠ Нерозпізнаний символ не можна вважати збігом: довести рівність
        // невідомого оголошеному неможливо, а «продовжимо, раптом те саме» —
        // це і є мовчазна конверсія навмання.
        var error = Assert.Throws<BusinessRuleException>(
            () => SourceUnitConverter.EnsureDeclaredUnit(KilogramId, "lb", Catalog(), "tag"));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Збіг_оголошеної_і_фактичної_одиниці_збір_не_зупиняє()
        => SourceUnitConverter.EnsureDeclaredUnit(KilogramId, "KG", Catalog(), "tag");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Джерело_без_UOM_збір_не_зупиняє()
    {
        // Порівнювати нема з чим: джерело одиниці не повідомило. Це не
        // «збіглося» і не привід підставляти безрозмірність (ФВ-16.12).
        SourceUnitConverter.EnsureDeclaredUnit(KilogramId, null, Catalog(), "tag");
        SourceUnitConverter.EnsureDeclaredUnit(null, "kg", Catalog(), "tag");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Конверсія_на_межі_іде_через_доменний_конвертер()
    {
        var result = Converter().Convert(2.5m, TonneId, "t", KilogramId, Catalog());

        Assert.Equal(2500m, result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Тотожна_конверсія_значення_не_змінює()
        => Assert.Equal(7m, Converter().Convert(7m, KilogramId, "kg", KilogramId, Catalog()));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Різні_розмірності_на_межі_відмовляють_а_не_вгадують()
    {
        // ⛔ м³ у кг не переводяться: коефіцієнт залежить від речовини й умов
        // і живе в методології, а не в довіднику одиниць (ФВ-16.3).
        Assert.Throws<Ecr.Domain.Abstractions.DomainException>(
            () => Converter().Convert(1m, CubicMetreId, "m3", KilogramId, Catalog()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Одиниці_поза_довідником_дають_відмову_а_не_множення_на_одиницю()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => Converter().Convert(1m, 99, null, KilogramId, Catalog()));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
    }
}
