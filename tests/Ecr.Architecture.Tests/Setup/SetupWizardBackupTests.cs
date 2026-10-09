using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// Крок «копію бази зроблено» майстра в режимі оновлення (AN-117, S2-04): та сама перевірка
/// <c>msdb.dbo.backupset</c>, що в <c>deploy-ecr.ps1</c>, і <c>-SkipBackupCheck</c> лише за явною позначкою.
/// </summary>
/// <remarks>
/// Мутації: <c>SkipBackupCheck</c> без <c>BackupRiskAccepted</c> — червоний
/// <see cref="Оновлення_без_позначки_не_передає_SkipBackupCheck"/>; межа майстра, що розійшлася з типовим
/// <c>-BackupMaxAgeHours</c> скрипта, — червоний <see cref="Запит_і_межа_ті_самі_що_в_deploy_ecr"/>.
/// </remarks>
public sealed class SetupWizardBackupTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Оновлення_з_позначкою_передає_SkipBackupCheck_перемикачем()
    {
        var state = new WizardState { Mode = WizardMode.Update, BackupRiskAccepted = true };

        var arguments = DeployArguments.Build(state);

        var skip = arguments.Single(a => a.Key == "SkipBackupCheck");
        Assert.Null(skip.Value);
        Assert.True(state.SkipBackupCheck);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Оновлення_без_позначки_не_передає_SkipBackupCheck()
    {
        // Свіжа копія в msdb перемикача не вмикає: скрипт перевірить її сам.
        var state = new WizardState
        {
            Mode = WizardMode.Update,
            Backup = SchemaBackupRules.Assess("30", "ECR"),
        };

        Assert.DoesNotContain("SkipBackupCheck", DeployArguments.Build(state).Select(a => a.Key));
        Assert.False(state.SkipBackupCheck);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Позначка_без_зміни_схеми_не_діє(bool firstDeployment, bool skipSchema)
    {
        // Перше розгортання (порожня база) і -SkipSchema копії не перевіряють — перемикач там зайвий.
        var state = new WizardState
        {
            Mode = firstDeployment ? WizardMode.FirstDeployment : WizardMode.Update,
            SkipSchema = skipSchema,
            BackupRiskAccepted = true,
        };

        Assert.False(state.SkipBackupCheck);
        Assert.False(SchemaBackupRules.IsRequired(state));
        Assert.DoesNotContain("SkipBackupCheck", DeployArguments.Build(state).Select(a => a.Key));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("none", "None", null)]
    [InlineData("", "None", null)]
    [InlineData(null, "None", null)]
    [InlineData("30", "Fresh", 30)]
    [InlineData("1440", "Fresh", 1440)]
    [InlineData("1441", "Stale", 1441)]
    [InlineData("\r\n  95\r\n", "Fresh", 95)]
    [InlineData("Msg 229, Level 14", "Unknown", null)]
    public void Відповідь_msdb_оцінюється_як_у_скрипті(string? output, string expected, int? minutes)
    {
        // Дзеркало Get-SchemaBackupProblem: 'none'/порожньо — копії немає; понад MaxAgeHours*60 — стара.
        var result = SchemaBackupRules.Assess(output, "ECR");

        Assert.Equal(Enum.Parse<BackupFreshness>(expected), result.Freshness);
        Assert.Equal(minutes, result.AgeMinutes);
        Assert.Contains("ECR", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Запит_і_межа_ті_самі_що_в_deploy_ecr()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        Assert.Contains("[switch] $SkipBackupCheck", script, StringComparison.Ordinal);
        Assert.Contains($"[int] $BackupMaxAgeHours = {SchemaBackupRules.MaxAgeHours}", script, StringComparison.Ordinal);

        var query = SchemaBackupRules.Query("E'CR");
        Assert.Contains("database_name = N'E''CR'", query, StringComparison.Ordinal);
        foreach (var fragment in new[]
                 {
                     "SET NOCOUNT ON; SELECT ISNULL(CAST(DATEDIFF(MINUTE, MAX(backup_finish_date), GETDATE()) AS nvarchar(20)), N'none') ",
                     "FROM msdb.dbo.backupset WHERE database_name = N'",
                     "' AND type IN ('D', 'I');",
                 })
        {
            Assert.Contains(fragment, query, StringComparison.Ordinal);
            Assert.Contains(fragment, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Рядок_Review_червоний_лише_коли_перевірку_пропущено()
    {
        var skipped = SchemaBackupRules.Describe(
            new WizardState { Mode = WizardMode.Update, BackupRiskAccepted = true }, out var skippedWarning);
        Assert.True(skippedWarning);
        Assert.Contains("SKIPPED", skipped, StringComparison.Ordinal);

        var fresh = SchemaBackupRules.Describe(
            new WizardState { Mode = WizardMode.Update, Backup = SchemaBackupRules.Assess("60", "ECR") }, out var freshWarning);
        Assert.False(freshWarning);
        Assert.StartsWith("checked:", fresh, StringComparison.Ordinal);

        SchemaBackupRules.Describe(new WizardState { Mode = WizardMode.FirstDeployment, BackupRiskAccepted = true }, out var first);
        Assert.False(first);

        SchemaBackupRules.Describe(
            new WizardState { Mode = WizardMode.Update, SkipSchema = true, BackupRiskAccepted = true }, out var noSchema);
        Assert.False(noSchema);
    }
}
