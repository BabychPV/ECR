using System.Xml.Linq;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// Безпекові властивості MSI, які видно зі складу <c>.wxs</c> без збірки
/// (аудит 2026-10-03, L10-02/L10-03). Справжня установка з перевіркою прав і
/// типу запуску — <c>tools/verify-msi.ps1</c> у джобі <c>msi-install (windows)</c>.
/// </summary>
public sealed class InstallerSecurityTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тека_config_має_захищений_DACL_без_запису_для_Users()
    {
        var components = Load("Folders.wxs").Descendants(Wix + "Component")
            .Where(c => (string?)c.Attribute("Directory") == "CONFIGFOLDER")
            .ToList();

        var sddl = components
            .SelectMany(c => c.Descendants(Wix + "PermissionEx"))
            .Select(p => (string?)p.Attribute("Sddl"))
            .SingleOrDefault(s => s is not null);

        Assert.NotNull(sddl);
        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);   // P — без успадкування від %ProgramData%
        Assert.Contains("(A;OICI;0x1200a9;;;BU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BU)", sddl.Replace("(A;OICI;0x1200a9;;;BU)", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // util:PermissionEx пише DACL без PROTECTED_DACL і знову вмикав би успадкування.
        Assert.Empty(components.SelectMany(c => c.Descendants(Util + "PermissionEx")));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Service.wxs", "EcrApiDemandStart", "EcrApi", "EcrService")]
    [InlineData("Worker.wxs", "EcrWorkerDemandStart", "EcrWorker", "EcrWorkerService")]
    public void Без_облікового_запису_служба_стає_Manual_а_не_Auto_під_LocalSystem(
        string fileName, string action, string service, string component)
    {
        // L10-03: Start="auto" + порожній SERVICE_ACCOUNT = LocalSystem після перезавантаження.
        var document = Load(fileName);

        var custom = document.Descendants(Wix + "Custom").Single(c => (string?)c.Attribute("Action") == action);
        var condition = (string?)custom.Attribute("Condition") ?? string.Empty;
        Assert.Contains("NOT SERVICE_ACCOUNT", condition, StringComparison.Ordinal);
        Assert.Contains($"${component} = 3", condition, StringComparison.Ordinal);

        var setProperty = document.Descendants(Wix + "SetProperty").Single(p => (string?)p.Attribute("Id") == action);
        Assert.Contains($"config {service} start= demand", (string?)setProperty.Attribute("Value"), StringComparison.Ordinal);

        var customAction = document.Descendants(Wix + "CustomAction").Single(a => (string?)a.Attribute("Id") == action);
        Assert.Equal("deferred", (string?)customAction.Attribute("Execute"));
        Assert.Equal("no", (string?)customAction.Attribute("Impersonate"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Майстер_типово_пропонує_gMSA()
    {
        // L10-03, D-282: типовий стан майстра — gMSA, не Local System.
        Assert.Equal(Ecr.Setup.ServiceAccountMode.Gmsa, new Ecr.Setup.WizardState().ServiceAccountMode);
    }

    private static XDocument Load(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "installer")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "installer", "Ecr.Installer", fileName));
    }
}
