using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен виклик <c>sqlcmd -i</c> у <c>tools/**/*.ps1</c> задає кодову сторінку входу 65001.
/// </summary>
/// <remarks>
/// ⛔ Інцидент 05.11 (живий Land, лише читання): тексти THROW у тригерах <c>TR_*_Immutable</c> починались
/// із U+00D0 — кирилиця, записана через кодову сторінку ANSI. <c>Sql/*.sql</c> — UTF-8 без BOM, а
/// <c>sqlcmd</c> без <c>-f</c> читає такий файл у ANSI: «Привіт» стає 12 символами «Ð…» (перевірено на
/// tempdb: без ключа UNICODE першого символу 208 і LEN 12, з <c>-f i:65001</c> — 1055 і 6).
/// Сторож читає текст скриптів (як <c>SetupDevDbAppPortGuardTests</c>). Мутація (прогнано): прибрати
/// <c>'-f', 'i:65001',</c> у <c>deploy-ecr.ps1</c> → тест червоний з іменем файлу й рядком.
/// </remarks>
public sealed class SqlcmdInputCodePageTests
{
    // Аргумент-масив: '-i' окремим елементом (Run-PostCheck тримає '-f' на 2 рядки вище, у тому ж масиві).
    private static readonly Regex ArrayInput = new(@"'-i'\s*,", RegexOptions.Compiled);

    // Прямий виклик: & sqlcmd ... -i $файл. Регістр важливий: -I — QUOTED_IDENTIFIER, не файл.
    private static readonly Regex BareInput = new(@"&\s*sqlcmd\b.*\s-i\s", RegexOptions.Compiled);

    private static readonly Regex CodePage = new(@"-f'?\s*,?\s*'?(i:)?65001", RegexOptions.Compiled);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_sqlcmd_з_файлом_читає_його_як_UTF8()
    {
        var tools = Path.Combine(SourceTree.Root, "tools");
        var sites = 0;
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                string window;
                if (ArrayInput.IsMatch(line))
                {
                    // Коментарі з вікна прибрано: інакше пояснення «-f i:65001» над рядком зараховувалось би за ключ.
                    window = string.Join('\n', lines[Math.Max(0, i - 3)..(i + 1)].Where(l => !l.TrimStart().StartsWith('#')));
                }
                else if (BareInput.IsMatch(line))
                {
                    window = line;
                }
                else
                {
                    continue;
                }

                sites++;
                if (!CodePage.IsMatch(window))
                {
                    missing.Add($"{Path.GetRelativePath(SourceTree.Root, file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        // Не порожній сторож: deploy-ecr, setup-dev-db, verify-sql-scripts, br07, e2e-stand ×2, Run-PostCheck ×2.
        Assert.True(sites >= 8, $"знайдено лише {sites} викликів sqlcmd -i — регулярка більше не бачить скриптів");
        Assert.True(missing.Count == 0,
            "sqlcmd -i без -f i:65001 (Sql/*.sql — UTF-8 без BOM, кирилиця стане «Ð…»):\n" + string.Join('\n', missing));
    }
}
