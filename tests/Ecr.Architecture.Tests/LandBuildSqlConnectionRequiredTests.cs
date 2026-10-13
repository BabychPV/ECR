using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Приймальна RC «П»: <c>Build-LandTemplate.ps1 -SqlConnection</c> мав дефолт на живий стенд (EcrLandUi), тож
/// запуск без явного параметра цілив у нього (охорона -ConfirmStand лише відмовляла). Тепер параметр не має
/// значення за замовчуванням: або явний -SqlConnection, або база виводиться з -ConfirmStand
/// (<c>localhost:5101/EcrLandAccP</c> -> EcrLandAccP, лише EcrLand*); інакше відмова.
/// Статичний сторож за прецедентом <c>LandScriptsHttpContractTests</c> + живі перевірки відмов (без мережі).
/// </summary>
public sealed class LandBuildSqlConnectionRequiredTests
{
    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private static (int Exit, string Output) Run(string scriptArgs)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // -File з тимчасовим скриптом: рядок підключення з `;` не розпадається на команди (див. LandStandSafetyTests).
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-sqlconn-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            file,
            $"try {{ & '{BuildScript}' {scriptArgs}; exit 0 }} catch {{ Write-Output $_.Exception.Message; exit 7 }}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            {
                start.ArgumentList.Add(a);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 120 с.");
            }

            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void SqlConnection_не_має_значення_за_замовчуванням_і_літералу_живого_стенда()
    {
        var text = File.ReadAllText(BuildScript);

        // Параметр оголошено без `= '...'`: дефолт на конкретну базу — небезпечний (мовчки цілить у живий стенд).
        Assert.DoesNotMatch(new Regex(@"\[string\]\s*\$SqlConnection\s*=", RegexOptions.CultureInvariant), text);
        // Літерал живого стенда не має бути значенням рядка підключення ніде у скрипті (приклад у коментарі — не значення).
        Assert.DoesNotMatch(new Regex(@"Database\s*=\s*EcrLandUi", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), text);
        Assert.True(Regex.IsMatch(text, @"\[string\]\s*\$SqlConnection\s*,"), "Параметр -SqlConnection має лишитися, без значення за замовчуванням.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Без_SqlConnection_і_без_бази_в_ConfirmStand_скрипт_відмовляє_а_не_цілить_у_стенд()
    {
        // Ні -SqlConnection, ні -ConfirmStand: навіть -DryRun відмовляє (раніше йшов у EcrLandUi за дефолтом).
        var (e1, o1) = Run("-BaseUrl http://localhost:5094 -DryRun");
        Assert.Equal(7, e1);
        Assert.Contains("-SqlConnection", o1, StringComparison.Ordinal);

        // -ConfirmStand без імені бази: вивести нема з чого.
        var (e2, o2) = Run("-BaseUrl http://localhost:5094 -ConfirmStand localhost:5094 -Stage Units");
        Assert.Equal(7, e2);
        Assert.Contains("-SqlConnection", o2, StringComparison.Ordinal);

        // База не EcrLand* (прод-інстанс старої системи): виведення відмовляє.
        var (e3, o3) = Run("-BaseUrl http://localhost:5094 -ConfirmStand localhost:5094/ECR -Stage Units");
        Assert.Equal(7, e3);
        Assert.Contains("EcrLand", o3, StringComparison.Ordinal);

        // Спроба протягнути чужий ключ рядка підключення через «ім'я бази».
        var (e4, o4) = Run("-BaseUrl http://localhost:5094 -ConfirmStand 'localhost:5094/EcrLandX;Server=prod' -Stage Units");
        Assert.Equal(7, e4);
        Assert.Contains("-SqlConnection", o4, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void База_виводиться_з_ConfirmStand_лише_для_EcrLand()
    {
        // Виведена база EcrLandAccP проходить Assert-LocalSqlConnection; -DryRun лише читає модель.
        var (exit, output) = Run("-BaseUrl http://localhost:5101 -ConfirmStand localhost:5101/EcrLandAccP -DryRun");
        Assert.True(exit == 0, output);
    }
}
