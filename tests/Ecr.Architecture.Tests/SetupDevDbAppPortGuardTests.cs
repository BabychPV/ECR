using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>setup-dev-db.ps1</c> піднімає застосунок заради сіду на ВІЛЬНОМУ порту й перевіряє, що
/// на ньому відповідає саме наш дочірній процес.
/// </summary>
/// <remarks>
/// ⛔ Інцидент 13.10: порт був зашитий (5099), там уже слухав чужий <c>Ecr.Api</c> іншої сесії;
/// скрипт прийняв ЙОГО <c>/health/live</c> за свій і хибно звітував «seed виконано»
/// (sys_ecr.UiString = 0), після чого DataGen впав на порожніх PeriodPolicies. Сторож читає
/// текст скрипта (як <c>SmokeStandDropGuardTests</c>). Мутація (прогнано): повернути
/// <c>$env:ASPNETCORE_URLS = 'http://localhost:5099'</c> або прибрати перевірку PID → тест червоний.
/// </remarks>
public sealed class SetupDevDbAppPortGuardTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "setup-dev-db.ps1"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порт_застосунку_не_зашитий_і_береться_з_AppPort()
    {
        var text = Script();

        Assert.DoesNotMatch(new Regex(@"\b5099\b"), text);
        Assert.DoesNotMatch(new Regex(@"ASPNETCORE_URLS\s*=\s*['""]http://[^$]*:\d+", RegexOptions.IgnoreCase), text);
        Assert.DoesNotMatch(new Regex(@"localhost:\d{4,5}/health", RegexOptions.IgnoreCase), text);

        Assert.Contains("[int] $AppPort = 0", text, StringComparison.Ordinal);
        Assert.Contains("$env:ASPNETCORE_URLS = $appBase", text, StringComparison.Ordinal);
        Assert.Contains("\"$appBase/health/live\"", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перед_стартом_є_перевірка_зайнятого_порту_а_після_старту_власник_слухача_звіряється_з_нашим_PID()
    {
        var text = Script();

        var preCheck = text.IndexOf("if (-not (Test-PortFree -Port $AppPort))", StringComparison.Ordinal);
        var start = text.IndexOf("$api = Start-Process @startArgs", StringComparison.Ordinal);
        Assert.True(preCheck > 0, "немає передперевірки зайнятого порту -AppPort");
        Assert.True(start > preCheck, "застосунок стартує до перевірки, що порт вільний");
        Assert.Contains("Get-NetTCPConnection -State Listen", text, StringComparison.Ordinal);

        var health = text.IndexOf("\"$appBase/health/live\"", StringComparison.Ordinal);
        var ownership = text.IndexOf("Get-DescendantPids -RootPid $api.Id", StringComparison.Ordinal);
        var refusal = text.IndexOf("слухає чужий процес", StringComparison.Ordinal);
        Assert.True(health > start, "проба /health/live стоїть до старту");
        Assert.True(ownership > health, "після проби здоров'я немає звірки власника порту з нашим PID");
        Assert.True(refusal > ownership, "чужий слухач не веде до відмови");

        var seedDone = text.IndexOf("seed виконано, bootstrap створено", StringComparison.Ordinal);
        Assert.True(seedDone > refusal, "«seed виконано» звітується до звірки власника порту");
    }
}
