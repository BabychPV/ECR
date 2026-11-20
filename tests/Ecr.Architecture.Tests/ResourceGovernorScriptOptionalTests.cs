using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// PSS-03: <c>Sql/16-resource-governor.sql</c> — необов'язковий скрипт DBA рівня інстансу. Він лежить у <c>Sql/</c>
/// (потрапляє в пакет), але НЕ входить до <c>$scripts</c> розгортання (обліковий запис застосунку без DDL, D-66).
/// </summary>
/// <remarks>
/// Мутація: додати <c>'16-resource-governor.sql'</c> до масиву <c>$scripts</c> у <c>deploy-ecr.ps1</c> → червоний;
/// прибрати з тіла скрипта перевірку чужого класифікатора (<c>50017</c>) → червоний.
/// </remarks>
public sealed class ResourceGovernorScriptOptionalTests
{
    private const string ScriptName = "16-resource-governor.sql";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_є_у_дереві_але_не_входить_до_автоматичного_розгортання()
    {
        var sqlDir = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");
        Assert.True(File.Exists(Path.Combine(sqlDir, ScriptName)));

        var deploy = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));
        Assert.DoesNotContain("16-resource-governor", deploy, StringComparison.Ordinal);

        // setup-dev-db / verify-sql-scripts / br07: ім'я лише як названий виняток сторожа «є в дереві, але не виконується».
        foreach (var tool in new[] { "setup-dev-db.ps1", "verify-sql-scripts.ps1", "br07-load-test.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", tool));
            var mentions = Regex.Count(text, Regex.Escape(ScriptName));
            Assert.Equal(1 + (tool == "br07-load-test.ps1" ? 0 : 1), mentions);
            Assert.Matches(@"-notin\s+'14-agent-jobs\.sql',\s*'16-resource-governor\.sql'", text);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_не_перезаписує_чужий_класифікатор_і_має_відкат()
    {
        var text = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", ScriptName));

        Assert.Contains("50017", text, StringComparison.Ordinal);
        Assert.Contains("@Rollback", text, StringComparison.Ordinal);
        Assert.Contains("APP_NAME()", text, StringComparison.Ordinal);
        Assert.Contains("RECONFIGURE", text, StringComparison.Ordinal);
    }
}
