using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Базова версія ТЗ: кожен файл <c>docs/tz/**</c> має запис у
/// <c>docs/CHECKSUMS.txt</c>, і сума в записі дорівнює сумі файла.
/// </summary>
/// <remarks>
/// ⛔ До 2026-09-27 маніфест накривав 84 файли, і його не стеріг ніхто: з
/// поставки 2026-09-04 розійшлося 38 сум, а документи й коментарі в коді далі
/// посилалися на нього як на гарантію («ТЗ не правиться — воно накрите
/// CHECKSUMS»). Людина зафіксувала нову базову версію; цей сторож робить
/// фіксацію чинною. Звіряється лише <c>tz/**</c> — записи інших каталогів
/// прибрано, щоб файл не обіцяв того, чого ніхто не стереже.
///
/// ⚠ Сума — SHA-256 вмісту з кінцями рядків LF, тобто так, як файл зберігає
/// git (<c>git show HEAD:docs/tz/… | sha256sum</c>). Робоче дерево Windows з
/// <c>core.autocrlf=true</c> тримає CRLF, тож без нормування той самий вміст
/// давав би різні суми на різних машинах.
/// </remarks>
public sealed partial class SpecificationBaselineTests
{
    /// <summary>Рядок формату <c>sha256sum</c>: <c>хеш  шлях</c> або <c>хеш *шлях</c>.</summary>
    [GeneratedRegex(@"^([0-9a-f]{64}) [ *](.+)$")]
    private static partial Regex ChecksumLine();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    public void Кожен_файл_ТЗ_збігається_з_записом_у_CHECKSUMS()
    {
        var docs = Path.Combine(SourceTree.Root, "docs");
        var problems = Verify(
            File.ReadAllLines(Path.Combine(docs, "CHECKSUMS.txt")),
            Path.Combine(docs, "tz"));

        Assert.True(
            problems.Count == 0,
            "docs/CHECKSUMS.txt не відповідає docs/tz/**:\n  "
            + string.Join("\n  ", problems)
            + "\nТЗ зафіксоване: змінюєш — онови суму в тому ж коміті.");
    }

    /// <summary>Звіряє маніфест із файлами каталогу ТЗ; повертає перелік проблем.</summary>
    /// <param name="manifest">Рядки <c>CHECKSUMS.txt</c>; шляхи відносно <c>docs/</c>.</param>
    /// <param name="tzDirectory">Каталог <c>docs/tz</c>.</param>
    private static List<string> Verify(IEnumerable<string> manifest, string tzDirectory)
    {
        var problems = new List<string>();
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);

        var lineNumber = 0;
        foreach (var raw in manifest)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var match = ChecksumLine().Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                problems.Add($"рядок {lineNumber}: не має форми «<sha256>  tz/<файл>»: {raw}");
                continue;
            }

            var path = match.Groups[2].Value;
            if (!path.StartsWith("tz/", StringComparison.Ordinal))
            {
                problems.Add($"рядок {lineNumber}: запис поза tz/ ({path}) — його ніхто не стереже, прибери");
                continue;
            }

            if (!recorded.TryAdd(path, match.Groups[1].Value))
            {
                problems.Add($"рядок {lineNumber}: повторний запис для {path}");
            }
        }

        var docs = Path.GetDirectoryName(tzDirectory)!;
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(tzDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var path = Path.GetRelativePath(docs, file).Replace('\\', '/');
            present.Add(path);
            var actual = LfSha256(File.ReadAllBytes(file));

            if (!recorded.TryGetValue(path, out var expected))
            {
                problems.Add($"файл без запису: {path} — впиши рядок: {actual}  {path}");
            }
            else if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                problems.Add($"сума не збігається: {path} — заміни рядок на: {actual}  {path}");
            }
        }

        foreach (var path in recorded.Keys.Where(p => !present.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            problems.Add($"запис без файлу: {path} — прибери рядок або поверни файл");
        }

        return problems;
    }

    /// <summary>SHA-256 вмісту з CRLF, зведеними до LF (як зберігає git).</summary>
    private static string LfSha256(byte[] content)
    {
        var normalized = new byte[content.Length];
        var length = 0;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
            {
                continue;
            }

            normalized[length++] = content[i];
        }

        return Convert.ToHexStringLower(SHA256.HashData(normalized.AsSpan(0, length)));
    }
}
