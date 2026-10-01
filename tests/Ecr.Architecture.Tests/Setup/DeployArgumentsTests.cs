using System.Security;
using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// Параметри виклику <c>deploy-ecr.ps1</c> із майстра встановлення
/// (<c>DeployArguments</c>, перенесено з <c>DeployRunner</c>).
/// </summary>
/// <remarks>
/// ⚠ Код майстра підключено посиланням на файли (див.
/// <c>Ecr.Architecture.Tests.csproj</c>): <c>Ecr.Setup</c> — net10.0-windows
/// із PowerShell SDK, а перелік параметрів — чиста функція стану.
/// </remarks>
public sealed class DeployArgumentsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Параметри_розгортання_в_тому_самому_порядку_й_з_тими_самими_перемикачами()
    {
        // Регресія перенесення з DeployRunner: перелік, порядок і перемикачі ті самі.
        using var bootstrap = Secure("Bootstrap-1!");
        var state = new WizardState
        {
            Mode = WizardMode.FirstDeployment,
            DataProtectionThumbprint = "AA11BB22CC33DD44EE55FF6600112233445566AA",
            HttpsThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567",
            MsiPath = @"C:\payload\Ecr.msi",
            BootstrapPassword = bootstrap,
            ServiceAccountMode = ServiceAccountMode.Gmsa,
            ServiceAccountName = @"DOMAIN\ecr-svc$",
        };

        var arguments = DeployArguments.Build(state);
        var names = arguments.Select(a => a.Key).ToList();

        Assert.Equal(
            ["SqlInstance", "Database", "MsiPath", "AppPort", "ConnectionString", "CreateDatabaseIfMissing",
             "DataProtectionThumbprint", "HttpsThumbprint", "FirstDeployment", "BootstrapPassword", "ServiceAccount"],
            names);
        Assert.Null(arguments.Single(a => a.Key == "CreateDatabaseIfMissing").Value);
        Assert.Null(arguments.Single(a => a.Key == "FirstDeployment").Value);
        Assert.IsType<SecureString>(arguments.Single(a => a.Key == "ConnectionString").Value);
        Assert.DoesNotContain("SkipSchema", names);
        Assert.DoesNotContain("SqlLogin", names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Оновлення_з_SQL_логіном_і_доменним_записом()
    {
        using var sqlPassword = Secure("Sql-1!");
        using var servicePassword = Secure("Svc-1!");
        var state = new WizardState
        {
            Mode = WizardMode.Update,
            SkipSchema = true,
            SqlAuthIsWindows = false,
            SqlLogin = "ecr_dba",
            SqlLoginPassword = sqlPassword,
            ServiceAccountMode = ServiceAccountMode.DomainUser,
            ServiceAccountName = @"DOMAIN\ecr",
            ServicePassword = servicePassword,
            BootstrapPassword = null,
        };

        var names = DeployArguments.Build(state).Select(a => a.Key).ToList();

        Assert.DoesNotContain("FirstDeployment", names);
        Assert.DoesNotContain("BootstrapPassword", names);
        Assert.Equal(
            ["SkipSchema", "ServiceAccount", "ServicePassword", "SqlLogin", "SqlPassword"],
            names.SkipWhile(n => n != "SkipSchema").ToList());
    }

    private static SecureString Secure(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }
}
