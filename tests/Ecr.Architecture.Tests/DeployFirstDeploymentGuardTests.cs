using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R5-U1/U1-03 (аудит 2026-10-09): <c>-FirstDeployment</c> обходить перевірку копії бази (S2-04/AN-117) і
/// перевидаляє завдання Agent — тож на базі з хоч однією застосованою міграцією EF крок 2 відмовляє до
/// будь-якої зміни, а майстер на сервері з уже встановленою службою типово пропонує «Update».
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>) і справжній стан майстра
/// (файл, підключений посиланням). Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployFirstDeploymentGuardTests
{
    private const string Body = """
        function Out-First([string] $key, $count) {
            $p = Get-FirstDeploymentProblem -AppliedCount $count -Database 'EcrDb'
            "first.$key.ok=$($null -eq $p)"
            "first.$key.hint=$([bool] ($p -and $p.Contains('-SkipSchema') -and $p.Contains('EcrDb')))"
        }
        Out-First 'zero'    '0'
        Out-First 'zeroPad' ' 0 '
        Out-First 'one'     '1'
        Out-First 'many'    '57'
        Out-First 'empty'   ''
        Out-First 'null'    $null
        Out-First 'garbage' 'NULL'
        Out-First 'neg'     '-1'
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-FirstDeploymentProblem"], Body));

    /// <remarks>
    /// Мутації (CI): пропускати будь-яку кількість → червоні <c>one</c>/<c>many</c>; вважати порожню чи
    /// нечислову відповідь «порожньою базою» → червоні <c>empty</c>/<c>null</c>/<c>garbage</c>/<c>neg</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("zero", "True")]
    [InlineData("zeroPad", "True")]
    [InlineData("one", "False")]
    [InlineData("many", "False")]
    [InlineData("empty", "False")]
    [InlineData("null", "False")]
    [InlineData("garbage", "False")]
    [InlineData("neg", "False")]
    public void FirstDeployment_лише_на_базі_без_міграцій(string key, string ok)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"first.{key}.ok"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відмова_на_живій_базі_називає_базу_й_шлях_повтору()
    {
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "first.many.hint"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: запит до <c>__EFMigrationsHistory</c> і вердикт — у гілці
    /// <c>-FirstDeployment</c> кроку 2, до зупинки служб і першого <c>sqlcmd -i</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_порожньої_бази_стоїть_до_зміни_схеми()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var step2 = script.IndexOf("Write-Step \"Крок 2/7: схема ($Database", StringComparison.Ordinal);
        var branch = script.IndexOf("if ($FirstDeployment) {", step2, StringComparison.Ordinal);
        var query = script.IndexOf("SELECT COUNT(*) FROM dbo.__EFMigrationsHistory", step2, StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-FirstDeploymentProblem -AppliedCount", step2, StringComparison.Ordinal);
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);

        Assert.True(step2 > 0 && branch > step2, "гілки -FirstDeployment у кроці 2 не знайдено");
        Assert.True(query > branch && verdict > query, "перевірки __EFMigrationsHistory у гілці -FirstDeployment немає");
        Assert.True(stop > verdict, "служби зупиняються раніше, ніж перевірено, що база порожня");
    }

    /// <remarks>
    /// R5-U1/U1-04: <c>CREATE DATABASE</c> — лише в гілці <c>-CreateDatabaseIfMissing -and $FirstDeployment</c>;
    /// інакше відсутня база — відмова з <c>database missing</c> (той самий маркер шукає <c>SqlPreflight</c>).
    /// Мутація (CI): повернути умову <c>if ($CreateDatabaseIfMissing)</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_створює_базу_лише_при_першому_розгортанні()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var gate = script.IndexOf("if ($CreateDatabaseIfMissing -and $FirstDeployment) {", StringComparison.Ordinal);
        var create = script.IndexOf("N'CREATE DATABASE '", StringComparison.Ordinal);
        var missing = script.IndexOf("RAISERROR('database missing", StringComparison.Ordinal);

        Assert.True(gate > 0, "CREATE DATABASE не обмежено -FirstDeployment");
        Assert.True(create > gate && missing > create, "CREATE DATABASE поза гілкою першого розгортання");
        Assert.Equal(1, CountOf(script, "N'CREATE DATABASE '"));
        Assert.DoesNotContain("if ($CreateDatabaseIfMissing) {", script, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <remarks>Мутація (CI): повернути завжди <c>FirstDeployment</c> → червоний.</remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(true, "Update")]
    [InlineData(false, "FirstDeployment")]
    public void Майстер_на_сервері_з_ECR_типово_пропонує_оновлення(bool installed, string expected)
    {
        Assert.Equal(expected, WizardState.DefaultMode(installed).ToString());
    }
}
