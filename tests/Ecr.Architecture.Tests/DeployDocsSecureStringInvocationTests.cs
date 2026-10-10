using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R9-F5/F5-03 (аудит 2026-10-10): секрети <c>deploy-ecr.ps1</c> (<c>-ConnectionString</c>,
/// <c>-BootstrapPassword</c>, <c>-ServicePassword</c>, <c>-SqlPassword</c>) — <c>SecureString</c>, і в новий
/// процес (<c>powershell -File</c>) вони не переходять: «Cannot convert … to SecureString» (install-guide §8,
/// коментар <c>DeployRunner</c>). Runbook §8 п.3 і §10.3 давали саме цю форму — оновлення за ним не проходило
/// навіть <c>-WhatIf</c>. Жоден блок коду документів адміністратора не викликає скрипт так.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class DeployDocsSecureStringInvocationTests
{
    private static readonly string[] SecureParameters = ["-ConnectionString", "-BootstrapPassword", "-ServicePassword", "-SqlPassword"];

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(SourceTree.Root, "docs", "admin"), "*.md").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(SourceTree.Root, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        data.Add("docs/build/11-install-guide.md");
        data.Add("docs/build/TESTER-GUIDE.md");
        data.Add("docs/build/TESTER-HANDOVER.md");
        return data;
    }

    /// <remarks>Мутація: повернути <c>powershell -ExecutionPolicy Bypass -File tools\deploy-ecr.ps1 … -ConnectionString $cs</c> у runbook → червоний.</remarks>
    [Theory]
    [MemberData(nameof(Documents))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Секрети_не_передаються_скрипту_через_новий_процес(string document)
    {
        var path = Path.Combine([SourceTree.Root, .. document.Split('/')]);
        if (!File.Exists(path))
        {
            return;
        }

        var text = File.ReadAllText(path);
        var blocks = Regex.Matches(text, @"```[a-zA-Z]*\r?\n(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value);
        foreach (var block in blocks)
        {
            // Команда може тягнутися через рядки з ` — склеюємо продовження.
            var joined = Regex.Replace(block, @"`\r?\n\s*", " ");
            foreach (var line in joined.Split('\n'))
            {
                if (!line.Contains("deploy-ecr.ps1", StringComparison.Ordinal)
                    || !Regex.IsMatch(line, @"(?i)\b(powershell|pwsh)(\.exe)?\b.*\s-File\b"))
                {
                    continue;
                }

                foreach (var parameter in SecureParameters)
                {
                    Assert.False(
                        line.Contains(parameter, StringComparison.Ordinal),
                        $"{document}: «{line.Trim()}» передає {parameter} (SecureString) у новий процес — викликайте .\\tools\\deploy-ecr.ps1 у тій самій сесії");
                }
            }
        }
    }
}
