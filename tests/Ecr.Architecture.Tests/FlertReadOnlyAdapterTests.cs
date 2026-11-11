// tests/Ecr.Architecture.Tests/FlertReadOnlyAdapterTests.cs
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Шлях читання FLERT лише ЧИТАЄ (<c>D-44</c>, <c>D-476</c>): адаптер і зіставлення не несуть жодного тексту з
/// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c>, не виконують команд без результату, а запит знімка йде в
/// транзакції, яку адаптер лише відкочує.
/// </summary>
/// <remarks>
/// ⚠ Текст запиту — конфігурація замовника, і його сторож не бачить; тому другий рубіж — відкат транзакції
/// (<c>SqlDataSource.ReadTableAsync</c>), а третій — логін FLERT лише з <c>EXECUTE</c> на процедуру читання
/// (інструкція встановлення). Сторож тримає перші два в коді.
/// <para>
/// Мутаційні докази (локально, не в коміті): (1) дописати в <c>SqlDataSource.cs</c> літерал
/// <c>"DELETE FROM dbo.FlaringStream"</c> → <c>Шлях_читання_FLERT_не_містить_SQL_запису</c> червоний;
/// (2) замінити <c>RollbackAsync</c> на <c>CommitAsync</c> у <c>ReadTableAsync</c> →
/// <c>Знімок_FLERT_читається_в_транзакції_що_лише_відкочується</c> червоний.
/// </para>
/// </remarks>
public sealed partial class FlertReadOnlyAdapterTests
{
    /// <summary>Файли шляху читання FLERT.</summary>
    private static readonly string[] ReadPath =
    [
        "src/Ecr.Adapters.Sql/SqlDataSource.cs",
        "src/Ecr.Adapters.Sql/DependencyInjection.cs",
        "src/Ecr.Application/Ports/IExternalTableSource.cs",
        "src/Ecr.Application/Registries/Flert/FlertCompositionSchema.cs",
        "src/Ecr.Application/Registries/Flert/FlertCompositionMapping.cs",
    ];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D-476")]
    public void Шлях_читання_FLERT_не_містить_SQL_запису()
    {
        // ⛔ Самоперевірка першою: регулярка, що перестала бачити літерал, дала б зелене ні про що.
        Assert.Single(WriteLiterals("var q = \"MERGE INTO x USING y\";"));
        Assert.Empty(WriteLiterals("// \"DELETE FROM x\" у коментарі"));
        Assert.Empty(WriteLiterals("var updatedAt = \"UpdatedAt\";"));

        var problems = new List<string>();
        foreach (var path in ReadPath)
        {
            var lines = File.ReadAllLines(Path.Combine(SourceTree.Root, path));
            Assert.True(lines.Length > 0, $"{path} порожній або зник — онови ReadPath.");

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var literal in WriteLiterals(lines[i]))
                {
                    problems.Add($"{path}:{i + 1}: {literal}");
                }

                if (!IsComment(lines[i]) && lines[i].Contains("ExecuteNonQuery", StringComparison.Ordinal))
                {
                    problems.Add($"{path}:{i + 1}: ExecuteNonQuery — команда без результату на шляху читання FLERT");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "Шлях читання FLERT мусить лише читати (D-44, D-476):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D-476")]
    public void Знімок_FLERT_читається_в_транзакції_що_лише_відкочується()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, "src/Ecr.Adapters.Sql/SqlDataSource.cs"));
        var start = text.IndexOf("public async Task<ExternalTable> ReadTableAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ReadTableAsync зник із SqlDataSource — онови сторож.");

        // Тіло методу — до наступного члена класу з документаційним коментарем.
        var end = text.IndexOf("    /// <summary>", start, StringComparison.Ordinal);
        var body = text[start..(end > start ? end : text.Length)];

        Assert.Contains("BeginTransactionAsync", body, StringComparison.Ordinal);
        Assert.Contains("new SqlCommand(query, connection, transaction)", body, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Commit(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Точний GRANT логіну FLERT — в інструкції встановлення і в runbook однаковий і мінімальний (людина 02.11: кожне право
    /// БД — точними командами в обох документах). Право запису чи <c>db_datareader</c> на всю базу — червоне.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D-476")]
    public void GRANT_логіну_FLERT_точний_і_однаковий_в_інструкції_і_runbook()
    {
        const string grant = "GRANT EXECUTE ON OBJECT::[dbo].[usp_GetAllStreamDataFlattened] TO [ecr_flert_reader];";
        foreach (var doc in new[] { "docs/build/11-install-guide.md", "docs/admin/operations-runbook.md" })
        {
            var text = File.ReadAllText(Path.Combine(SourceTree.Root, doc));
            Assert.Contains(grant, text, StringComparison.Ordinal);

            var flert = text.Split('\n').Where(l => l.Contains("ecr_flert_reader", StringComparison.Ordinal) && l.TrimStart().StartsWith("GRANT", StringComparison.OrdinalIgnoreCase));
            Assert.All(flert, l => Assert.DoesNotMatch(@"\b(INSERT|UPDATE|DELETE|ALTER|CONTROL|db_datareader|db_owner)\b", l));
        }
    }

    /// <summary>Рядкові літерали рядка коду (не коментаря), що містять оператор запису SQL.</summary>
    private static IEnumerable<string> WriteLiterals(string line)
    {
        if (IsComment(line))
        {
            yield break;
        }

        foreach (Match literal in StringLiteral().Matches(line))
        {
            if (WriteStatement().IsMatch(literal.Value))
            {
                yield return literal.Value;
            }
        }
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteStatement();
}
