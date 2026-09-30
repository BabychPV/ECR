using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Діагностики виразів, що доходять до людини, — ключем каталогу
/// (<c>expr.*</c>) з англійським запасним текстом, а не готовим українським
/// реченням (V-20, третій раунд UX, 2026-09-24).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Редактор виразів показував «ECR-TMPL-0422 Функція 'IF' недоступна
/// в діалекті Methodology…» за будь-якої мови інтерфейсу: порада діалекту
/// методологій (<c>Parser.ReportUnknownFunction</c>) і всі діагностики
/// зв'язувача (<c>TypeChecker</c>, <c>UnitChecker</c>, <c>ReferenceResolver</c>,
/// <c>PredicateValidator</c>, <c>RangeExpander</c>, <c>ReportExpressionChecker</c>,
/// <c>CycleDescription</c>) несли українське речення без ключа.
/// <para>
/// ⚠ Два сторожі, бо дефект має дві половини: (1) у коді <c>Ecr.Expressions</c>
/// немає рядкового літерала з кирилицею поза конструктором винятку (внутрішні
/// винятки — 500, їхній текст людині не показують); (2) кожен ключ
/// <c>expr.*</c>, названий у коді, заведений у <c>09-seed.sql</c> англійською —
/// інакше клієнт показав би сам ключ.
/// </para>
/// </remarks>
public sealed partial class ExpressionDiagnosticLocalizationTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*[\\u0400-\\u04FF](?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex CyrillicLiteral();

    [GeneratedRegex("\"(expr\\.[A-Za-z0-9_.]+)\"")]
    private static partial Regex ExprKey();

    [GeneratedRegex(@"new\s+[A-Za-z]*Exception\s*\(")]
    private static partial Regex ExceptionCtor();

    /// <remarks>
    /// Мутація: повернути в <c>Parser.ReportUnknownFunction</c> колишній
    /// рядок «Функція '{name}' недоступна…» — тест червоний і називає рядок.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_коді_виразів_немає_українських_речень_поза_винятками()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production("Ecr.Expressions"))
        {
            var lines = MaskComments(file.Text).Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (!CyrillicLiteral().IsMatch(lines[i]))
                {
                    continue;
                }

                // ⚠ Конструктор винятку може стояти рядком-двома вище:
                // `throw new ArgumentException(\n    $"…",`.
                var window = string.Join('\n', lines[Math.Max(0, i - 2)..(i + 1)]);
                if (!ExceptionCtor().IsMatch(window))
                {
                    offenders.Add($"{file.Path}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Діагностика виразу з українським реченням без ключа каталогу (V-20). Заведи ключ expr.* "
            + "з англійським текстом у 09-seed.sql, речення в коді — англійською:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <remarks>
    /// Мутація: прибрати з <c>09-seed.sql</c> рядок
    /// <c>expr.unknownFunctionCase</c> — тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_ключ_expr_з_коду_заведено_в_каталозі_англійською()
    {
        var seed = File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile));

        var missing = SourceTree.Production("Ecr.Expressions", "Ecr.Application")
            .SelectMany(file => ExprKey().Matches(MaskComments(file.Text)).Select(m => (file.Path, Key: m.Groups[1].Value)))
            .Where(hit => !seed.Contains($"(N'{hit.Key}',", StringComparison.Ordinal)
                          || !Regex.IsMatch(seed, @"\(N'" + Regex.Escape(hit.Key) + @"',\s*N'en'"))
            .Select(hit => $"{hit.Path}: {hit.Key}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Ключі expr.* без рядка en у {SeedFile}:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>Замінює коментарі пробілами, зберігаючи нумерацію рядків.</summary>
    private static string MaskComments(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;

        while (i < chars.Length)
        {
            if (chars[i] == '"')
            {
                // Рядковий літерал — пропустити цілком (з екрануванням).
                i++;
                while (i < chars.Length && chars[i] != '"' && chars[i] != '\n')
                {
                    i += chars[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (i + 1 < chars.Length && chars[i] == '/' && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                {
                    chars[i++] = ' ';
                }

                continue;
            }

            if (i + 1 < chars.Length && chars[i] == '/' && chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }

                    i++;
                }

                if (i + 1 < chars.Length)
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                }

                i += 2;
                continue;
            }

            i++;
        }

        return new string(chars);
    }
}
