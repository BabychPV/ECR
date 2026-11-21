using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>ONLINE = ON</c> у SQL-скриптах розгортання — лише всередині рядка динамічного SQL, який складається за
/// <c>SERVERPROPERTY('EngineEdition')</c>; статично — ніколи, навіть у гілці <c>IF</c>, що на Express не виконується.
/// </summary>
/// <remarks>
/// ⛔ Express і Standard відхиляють пакет зі статичним <c>CREATE INDEX … WITH (ONLINE = ON)</c> ще на компіляції
/// (Msg 1712), тож гілка <c>IF EngineEdition IN (3, 5, 8)</c> не рятує: інтеграція b2 (21.11) — схема не будувалась
/// на LocalDB (CI worker windows), а в замовника на Standard/Express упало б розгортання. Відтворено на LocalDB:
/// статична форма — 1712, динамічна — індекс створено. Мутація: повернути статичну гілку в <c>07-partition-tables.sql</c>
/// → тест червоний.
/// </remarks>
public sealed class SqlOnlineIndexOptionTests
{
    private static readonly Regex Online = new(@"\bONLINE\s*=\s*ON\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void ONLINE_лише_в_динамічному_SQL()
    {
        var dir = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                var match = Online.Match(code);
                if (match.Success && !InsideStringLiteral(code, match.Index))
                {
                    offenders.Add($"{Path.GetRelativePath(dir, path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Статичний ONLINE = ON (Express/Standard: Msg 1712):\n" + string.Join('\n', offenders));
    }

    /// <summary>Рядок до <c>--</c>, що стоїть поза рядковим літералом.</summary>
    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'')
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                return line[..i];
            }
        }

        return line;
    }

    /// <summary>Чи позиція всередині рядкового літерала (непарна кількість апострофів перед нею).</summary>
    private static bool InsideStringLiteral(string line, int index) => line[..index].Count(c => c == '\'') % 2 == 1;
}
