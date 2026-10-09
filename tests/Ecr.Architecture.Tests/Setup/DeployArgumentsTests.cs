using System.Data.Common;
using System.Runtime.InteropServices;
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
        Assert.DoesNotContain("CreateDatabaseIfMissing", names);   // R5-U1/U1-04: оновлення базу не створює
        Assert.Equal(
            ["SkipSchema", "ServiceAccount", "ServicePassword", "SqlLogin", "SqlPassword"],
            names.SkipWhile(n => n != "SkipSchema").ToList());
    }

    /// <remarks>
    /// R5-U1/U1-04: оновлення зі зміною схеми й позначкою «I accept the risk» на хибному/типовому імені
    /// бази створювало порожню базу й переводило на неї службу. Мутація (CI, локально не запускалась):
    /// повернути безумовний <c>CreateDatabaseIfMissing</c> → тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Оновлення_не_дозволяє_скрипту_створювати_базу()
    {
        var state = new WizardState
        {
            Mode = WizardMode.Update,
            BackupRiskAccepted = true,
            ServiceAccountMode = ServiceAccountMode.Gmsa,
            ServiceAccountName = @"DOMAIN\ecr-svc$",
        };

        var names = DeployArguments.Build(state).Select(a => a.Key).ToList();

        Assert.DoesNotContain("CreateDatabaseIfMissing", names);
        Assert.Contains("SkipBackupCheck", names);
        Assert.Contains("CreateDatabaseIfMissing", DeployArguments.Build(new WizardState { Mode = WizardMode.FirstDeployment }).Select(a => a.Key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пароль_з_крапкою_з_комою_не_підміняє_параметри_рядка_підключення()
    {
        // L10-04: інтерполяція `Password={…};` давала паролю дописати власні параметри.
        const string password = "p;Database=master;Integrated Security=True;\"'=";
        using var sqlPassword = Secure(password);
        var state = new WizardState
        {
            SqlInstance = @"srv\SQL",
            Database = "ECR",
            SqlAuthIsWindows = false,
            SqlLogin = "ecr;app",
            SqlLoginPassword = sqlPassword,
        };

        var parsed = Parse(DeployArguments.BuildConnectionString(state));

        Assert.Equal(password, parsed["Password"]);
        Assert.Equal("ecr;app", parsed["User ID"]);
        Assert.Equal("ECR", parsed["Database"]);
        Assert.False(parsed.ContainsKey("Integrated Security"));
        Assert.Equal("Mandatory", parsed["Encrypt"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Типово_Windows_автентифікація_без_пароля_в_рядку()
    {
        // D-282: варіант A (Windows/gMSA) — типовий; секрету в рядку немає.
        var parsed = Parse(DeployArguments.BuildConnectionString(new WizardState()));

        Assert.Equal("True", parsed["Integrated Security"]);
        Assert.False(parsed.ContainsKey("Password"));
        Assert.False(parsed.ContainsKey("User ID"));
        Assert.Equal("Mandatory", parsed["Encrypt"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Типово_сертифікат_SQL_перевіряється_і_скрипт_без_перемикача_довіри()
    {
        // L10-04, D-333 (HU-12 R3 = A): прапорець «довіряти сертифікату SQL» типово вимкнений.
        var state = new WizardState();

        Assert.False(state.TrustSqlServerCertificate);
        var parsed = Parse(DeployArguments.BuildConnectionString(state));
        Assert.Equal("Mandatory", parsed["Encrypt"]);
        Assert.Equal("False", parsed["TrustServerCertificate"]);
        Assert.DoesNotContain("TrustServerCertificate", DeployArguments.Build(state).Select(a => a.Key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Увімкнений_прапорець_довіри_дає_True_у_рядку_і_перемикач_скрипту()
    {
        // D-333: свідомий вибір адміністратора — і рядок служби, і sqlcmd скрипта (-C).
        var state = new WizardState { TrustSqlServerCertificate = true };

        var parsed = Parse(DeployArguments.BuildConnectionString(state));
        Assert.Equal("Mandatory", parsed["Encrypt"]);
        Assert.Equal("True", parsed["TrustServerCertificate"]);
        var trust = DeployArguments.Build(state).Single(a => a.Key == "TrustServerCertificate");
        Assert.Null(trust.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_розгортання_кличе_sqlcmd_з_C_лише_за_перемикачем_довіри()
    {
        // D-333: раніше `-C` стояв безумовно в кожному виклику sqlcmd deploy-ecr.ps1.
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        Assert.Contains("[switch] $TrustServerCertificate", script, StringComparison.Ordinal);
        Assert.Contains("$sqlTrust = if ($TrustServerCertificate) { @('-C') } else { @() }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@('-C',", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("$sqlAuth + $sqlTrust +").Length - 1);
    }

    private static DbConnectionStringBuilder Parse(SecureString secure)
    {
        var bstr = Marshal.SecureStringToBSTR(secure);
        try
        {
            return new DbConnectionStringBuilder { ConnectionString = Marshal.PtrToStringBSTR(bstr) };
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
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
