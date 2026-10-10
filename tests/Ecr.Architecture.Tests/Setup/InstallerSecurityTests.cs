using System.Xml.Linq;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// Безпекові властивості MSI, які видно зі складу <c>.wxs</c> без збірки
/// (аудит 2026-10-03, L10-02/L10-03; 2026-10-09, N5-02/N5-05). Справжня установка з перевіркою прав і
/// типу запуску — <c>tools/verify-msi.ps1</c> у джобі <c>msi-install (windows)</c>.
/// </summary>
public sealed class InstallerSecurityTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    /// <remarks>
    /// L10-02/N5-05/N5-02: logs, config і батьківська ECR — ядровий <c>PermissionEx</c> з SDDL
    /// <c>O:BAG:SYD:P…</c> (власник Administrators, захищений DACL), Users — лише читання, жодного
    /// <c>util:PermissionEx</c> (він знову вмикав би успадкування). Мутація (CI, локально не
    /// запускалась): повернути <c>util:PermissionEx</c> у <c>LogsFolder</c> → тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("CONFIGFOLDER")]
    [InlineData("LOGSFOLDER")]
    [InlineData("DATAFOLDER")]
    public void Теки_ECR_мають_захищений_DACL_із_власником_Administrators_без_запису_для_Users(string directory)
    {
        var components = Load("Folders.wxs").Descendants(Wix + "Component")
            .Where(c => (string?)c.Attribute("Directory") == directory)
            .ToList();

        var sddl = components
            .SelectMany(c => c.Descendants(Wix + "PermissionEx"))
            .Select(p => (string?)p.Attribute("Sddl"))
            .SingleOrDefault(s => s is not null);

        Assert.NotNull(sddl);
        Assert.StartsWith("O:BAG:SYD:P", sddl, StringComparison.Ordinal);   // O:BA — власник; P — без успадкування від %ProgramData%
        Assert.Contains("(A;OICI;0x1200a9;;;BU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BU)", sddl.Replace("(A;OICI;0x1200a9;;;BU)", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // util:PermissionEx пише DACL без PROTECTED_DACL і знову вмикав би успадкування.
        Assert.Empty(components.SelectMany(c => c.Descendants(Util + "PermissionEx")));
    }

    /// <remarks>
    /// L10-02/N5-05: запис служби в logs — відкладена icacls-дія ПІСЛЯ InstallServices (icacls /grant
    /// не знімає захисту DACL), лише з SERVICE_ACCOUNT, і право Modify, а не повний доступ.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Запис_служби_в_теку_логів_видає_icacls_після_InstallServices_лише_за_SERVICE_ACCOUNT()
    {
        var document = Load("Folders.wxs");

        var setProperty = document.Descendants(Wix + "SetProperty")
            .Single(p => (string?)p.Attribute("Id") == "EcrLogsServiceAccountGrant");
        var command = (string?)setProperty.Attribute("Value") ?? string.Empty;
        Assert.Contains("icacls.exe", command, StringComparison.Ordinal);
        Assert.Contains("/grant", command, StringComparison.Ordinal);
        Assert.Contains("[SERVICE_ACCOUNT]:(OI)(CI)M", command, StringComparison.Ordinal);

        var action = document.Descendants(Wix + "CustomAction")
            .Single(a => (string?)a.Attribute("Id") == "EcrLogsServiceAccountGrant");
        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("check", (string?)action.Attribute("Return"));

        var custom = document.Descendants(Wix + "Custom")
            .Single(c => (string?)c.Attribute("Action") == "EcrLogsServiceAccountGrant");
        Assert.Equal("InstallServices", (string?)custom.Attribute("After"));
        var condition = (string?)custom.Attribute("Condition") ?? string.Empty;
        Assert.StartsWith("SERVICE_ACCOUNT", condition, StringComparison.Ordinal);
        Assert.Contains("$LogsFolder = 3", condition, StringComparison.Ordinal);
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

    /// <remarks>
    /// R5-U1/U1-01: служба, стартована всередині <c>msiexec</c>, ще не має Environment (рядок
    /// підключення й відбиток DP <c>deploy-ecr.ps1</c> пише ПІСЛЯ MSI) — падає до звіту SCM →
    /// <c>Error 1920</c> → відкат установки. Старт під час MSI — лише за явним <c>START_SERVICES=1</c>,
    /// який скрипт розгортання не передає. Мутація (CI, локально не запускалась): прибрати
    /// <c>START_SERVICES</c> з умови будь-якого компонента зі стартом → тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Service.wxs")]
    [InlineData("Worker.wxs")]
    public void MSI_стартує_службу_лише_за_явним_START_SERVICES(string fileName)
    {
        var starts = Load(fileName).Descendants(Wix + "ServiceControl")
            .Where(c => (string?)c.Attribute("Start") is "install" or "both")
            .ToList();
        Assert.NotEmpty(starts);
        foreach (var start in starts)
        {
            var component = start.Ancestors(Wix + "Component").First();
            var condition = (string?)component.Attribute("Condition") ?? string.Empty;
            Assert.Contains("START_SERVICES = \"1\"", condition, StringComparison.Ordinal);
            Assert.Contains("SERVICE_ACCOUNT", condition, StringComparison.Ordinal);
        }

        var property = Load("Package.wxs").Descendants(Wix + "Property")
            .Single(p => (string?)p.Attribute("Id") == "START_SERVICES");
        Assert.Equal("yes", (string?)property.Attribute("Secure"));
        Assert.Null(property.Attribute("Value"));   // типово — не стартувати

        var deploy = File.ReadAllLines(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));
        Assert.DoesNotContain(deploy, line => !line.TrimStart().StartsWith('#') && line.Contains("START_SERVICES", StringComparison.Ordinal));
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
