using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Тест, що чекає блокування через <c>sys.dm_tran_locks</c>, дивиться лише на
/// свою базу або свої сесії (<c>Z8-02</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>sys.dm_tran_locks</c> показує блокування всього інстансу. Запит
/// «є хтось у черзі на <c>sheet-edit</c>» без
/// <c>resource_database_id = DB_ID()</c> бачить і чужу базу на тому самому
/// сервері: другий worktree з <c>ECR_TEST_SQL</c>, шард
/// <c>ECR_TEST_SHARD</c>. Наслідок залежить від тесту: хибне «у черзі»
/// червонить <c>SheetEditGateNoWaitTests</c> без вади й робить
/// <c>RowStoreEnsureStructureLockTests</c> зеленим навіть на коді з вадою.
/// Зразок із фільтром — <c>RecalculationDocumentLockFairnessTests</c>.
/// <para>
/// Правило: у тексті команди від <c>FROM sys.dm_tran_locks</c> до кінця
/// літерала має бути <c>resource_database_id</c> або
/// <c>request_session_id</c> (фільтр за своїми сесіями теж відсікає чужі бази).
/// </para>
/// </remarks>
public sealed partial class LockWaitDatabaseFilterTests
{
    /// <summary>Скільки символів команди дивитися, якщо кінця літерала не знайдено.</summary>
    private const int MaxCommandLength = 2000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Запит_до_dm_tran_locks_у_тестах_фільтрує_свою_базу_чи_свої_сесії()
    {
        var checkedQueries = new List<string>();
        var offenders = new List<string>();

        foreach (var path in SourceTree.Walk(Path.Combine(SourceTree.Root, "tests"))
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.EndsWith(nameof(LockWaitDatabaseFilterTests) + ".cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(path);
            var relative = Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');

            foreach (Match query in FromLocksRegex().Matches(text))
            {
                var line = text.AsSpan(0, query.Index).Count('\n') + 1;
                checkedQueries.Add($"{relative}:{line}");

                var command = CommandText(text, query.Index);
                if (!command.Contains("resource_database_id", StringComparison.OrdinalIgnoreCase)
                    && !command.Contains("request_session_id", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(
                        $"{relative}:{line}: sys.dm_tran_locks без resource_database_id = DB_ID() "
                        + "чи request_session_id — бачить блокування чужих баз інстансу");
                }
            }
        }

        // ⚠ Зразок із фільтром мусить знаходитися, інакше «порушень немає»
        // означало б лише, що сканувати нічого.
        Assert.Contains(checkedQueries, q => q.Contains("RecalculationDocumentLockFairnessTests.cs:", StringComparison.Ordinal));
        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    /// <summary>Текст команди від <paramref name="start"/> до кінця рядкового літерала.</summary>
    /// <param name="text">Вміст файлу.</param>
    /// <param name="start">Позиція <c>FROM sys.dm_tran_locks</c>.</param>
    /// <remarks>
    /// Кінець — перше <c>"""</c> (сирий літерал) або <c>";</c> (звичайний чи
    /// склеєний через <c>+</c>), що трапиться після початку.
    /// </remarks>
    private static string CommandText(string text, int start)
    {
        var window = text.Substring(start, Math.Min(MaxCommandLength, text.Length - start));
        var ends = new[] { window.IndexOf("\"\"\"", StringComparison.Ordinal), window.IndexOf("\";", StringComparison.Ordinal) }
            .Where(i => i >= 0)
            .ToList();
        return ends.Count == 0 ? window : window[..ends.Min()];
    }

    [GeneratedRegex(@"FROM\s+sys\.dm_tran_locks\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromLocksRegex();
}
