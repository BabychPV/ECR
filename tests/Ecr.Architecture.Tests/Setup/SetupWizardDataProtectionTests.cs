using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// S11, майстер встановлення: вибір сертифіката Data Protection і передача
/// його відбитка в <c>deploy-ecr.ps1</c>.
/// </summary>
/// <remarks>
/// ⚠ Код майстра підключено посиланням на файли (див.
/// <c>Ecr.Architecture.Tests.csproj</c>): сховище сертифікатів — через шов
/// <see cref="ICertificateSource"/>, тож тести не залежать від
/// <c>Cert:\LocalMachine\My</c> машини, на якій ідуть.
/// </remarks>
public sealed class SetupWizardDataProtectionTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Local);

    private static readonly CertificateInfo Valid = new(
        "AA11BB22CC33DD44EE55FF6600112233445566AA", "CN=ecr-dp", Now.AddYears(-1), Now.AddYears(2), HasPrivateKey: true);

    private static readonly CertificateInfo NoKey = new(
        "BB11BB22CC33DD44EE55FF6600112233445566BB", "CN=public-only", Now.AddYears(-1), Now.AddYears(3), HasPrivateKey: false);

    private static readonly CertificateInfo Expired = new(
        "CC11BB22CC33DD44EE55FF6600112233445566CC", "CN=old", Now.AddYears(-3), Now.AddDays(-1), HasPrivateKey: true);

    private static readonly CertificateInfo NotYet = new(
        "DD11BB22CC33DD44EE55FF6600112233445566DD", "CN=future", Now.AddDays(1), Now.AddYears(3), HasPrivateKey: true);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Список_пропонує_лише_сертифікати_із_закритим_ключем()
    {
        var selectable = DataProtectionCertificateRules.Selectable([NoKey, Valid, Expired]);

        Assert.DoesNotContain(selectable, c => !c.HasPrivateKey);
        Assert.Equal([Valid, Expired], selectable);   // найдовший строк першим
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Опис_показує_суб_єкт_строк_і_відбиток()
    {
        var text = DataProtectionCertificateRules.Describe(Valid);

        Assert.Contains("CN=ecr-dp", text, StringComparison.Ordinal);
        Assert.Contains(Valid.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Contains(Valid.Thumbprint, text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Стан_зберігає_відбиток_нормалізованим()
    {
        // Відбиток копіюють із вікна сертифіката Windows групами по два
        // символи, інколи з нерозривним пробілом — застосунок і deploy-ecr.ps1
        // нормалізують так само.
        var state = new WizardState { DataProtectionThumbprint = "aa 11 bb\u00A022" };

        Assert.Equal("AA11BB22", state.DataProtectionThumbprint);

        state.DataProtectionThumbprint = "   ";
        Assert.Null(state.DataProtectionThumbprint);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Стан_із_чинним_сертифікатом_проходить_перевірку()
    {
        var state = new WizardState { DataProtectionThumbprint = Valid.Thumbprint.ToLowerInvariant() };

        Assert.True(state.TryValidateDataProtection(new FakeSource(Valid, NoKey), Now, out var error), error);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("порожньо", "Select the Data Protection certificate")]
    [InlineData("зник", "no longer in Cert:\\LocalMachine\\My")]
    [InlineData("без закритого ключа", "has no private key")]
    [InlineData("прострочений", "expired")]
    [InlineData("ще не чинний", "is not valid until")]
    public void Стан_дає_зрозумілу_відмову(string @case, string expected)
    {
        // ⚠ Випадок — рядком, а не CertificateInfo в InlineData: тип майстра
        // internal, а публічний метод тесту не може його приймати.
        var (thumbprint, store) = @case switch
        {
            "порожньо" => ((string?)null, new[] { Valid }),
            "зник" => (Valid.Thumbprint, new[] { NoKey }),
            "без закритого ключа" => (NoKey.Thumbprint, new[] { NoKey }),
            "прострочений" => (Expired.Thumbprint, new[] { Expired }),
            "ще не чинний" => (NotYet.Thumbprint, new[] { NotYet }),
            _ => throw new ArgumentOutOfRangeException(nameof(@case), @case, null),
        };

        var state = new WizardState { DataProtectionThumbprint = thumbprint };

        Assert.False(state.TryValidateDataProtection(new FakeSource(store), Now, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сертифікат_що_зник_після_вибору_ловиться_перед_розгортанням()
    {
        // Той самий стан перевіряється двічі: на кроці вибору й на «Огляді».
        var state = new WizardState { DataProtectionThumbprint = Valid.Thumbprint };
        var store = new FakeSource(Valid);

        Assert.True(state.TryValidateDataProtection(store, Now, out _));

        store.Items = [];
        Assert.False(state.TryValidateDataProtection(store, Now, out var error));
        Assert.Contains(Valid.Thumbprint, error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Параметри_розгортання_містять_відбиток_сертифіката()
    {
        var state = new WizardState { DataProtectionThumbprint = Valid.Thumbprint, MsiPath = @"C:\payload\Ecr.msi" };

        var arguments = DeployArguments.Build(state);

        var thumbprint = Assert.Single(arguments, a => a.Key == "DataProtectionThumbprint");
        Assert.Equal(Valid.Thumbprint, thumbprint.Value);
    }

    private sealed class FakeSource(params CertificateInfo[] items) : ICertificateSource
    {
        public CertificateInfo[] Items { get; set; } = items;

        public IReadOnlyList<CertificateInfo> List() => Items;
    }
}
