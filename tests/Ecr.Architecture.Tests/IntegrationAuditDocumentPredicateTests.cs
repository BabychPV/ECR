using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R5-Q1-01: кожне читання <c>aud.CellChange</c> в інтеграції (патчер і синк подій) з'єднує журнал
/// з <c>c.DocumentId = @document</c> — провідною колонкою <c>IX_CellChange_Cell</c>.
/// </summary>
/// <remarks>
/// ⛔ Без цього предиката жоден індекс журналу не дає seek (PK — <c>(ChangedAt, Id)</c>, партиції
/// за <c>ChangedAt</c>), і «чи правила комірку людина» сканує весь журнал аудиту системи — у синку
/// видалень ще й під <c>HOLDLOCK</c> на <c>doc.Period</c>. Запити <c>ManualCellsSql</c> і
/// <c>ManualRowKeysSql</c> міряє <c>IntegrationManualCellsAuditSeekTests</c>; цей сторож тримає
/// і третій, вбудований у пакет <c>RecheckRemovalAsync</c>, і будь-яку нову копію.
/// </remarks>
public sealed class IntegrationAuditDocumentPredicateTests
{
    private static readonly string[] Files =
    [
        "src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs",
        "src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Finding", "R5-Q1-01")]
    public void Читання_журналу_в_інтеграції_звужене_документом()
    {
        var sources = SourceTree.Production("Ecr.Infrastructure")
            .Where(f => Files.Contains(f.Path, StringComparer.Ordinal))
            .ToList();

        Assert.Equal(Files.Length, sources.Count);

        var reads = 0;
        var violations = new List<string>();

        foreach (var file in sources)
        {
            var lines = file.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("--", StringComparison.Ordinal)
                    || !(line.StartsWith("FROM aud.CellChange", StringComparison.Ordinal)
                         || line.StartsWith("JOIN aud.CellChange", StringComparison.Ordinal)))
                {
                    continue;
                }

                reads++;
                var window = string.Join(' ', lines.Skip(i).Take(3));
                if (!window.Contains("c.DocumentId = @document", StringComparison.Ordinal))
                {
                    violations.Add($"{file.Path}:{i + 1}: {line}");
                }
            }
        }

        // Патчер — 1, синк подій — 2 (ManualRowKeysSql і пакет RecheckRemovalAsync).
        Assert.True(reads >= 3, $"Знайдено лише {reads} читань aud.CellChange — сторож втратив предмет.");
        Assert.True(
            violations.Count == 0,
            "Читання aud.CellChange без `c.DocumentId = @document` — скан усього журналу аудиту (R5-Q1-01):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
