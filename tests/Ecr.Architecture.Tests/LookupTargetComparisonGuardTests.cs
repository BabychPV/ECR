using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// PRMG: порівняння «реєстр ЗАПИСУ ↔ ціль Lookup колонки/поля» робиться ЛИШЕ через
/// <c>LookupTargetMatcher.Belongs</c> (і далі <c>ILookupTargetResolver</c>). Ціль може бути ГРУПОЮ типів дозволів
/// (прихований реєстр <c>PRMG_*</c> без записів): пряме <c>standing.RegistryDefId != column.LookupRegistryDefId</c>
/// тихо відхилило б записи групи (або, у новому місці, пропустило б перевірку взагалі).
/// </summary>
/// <remarks>
/// <para>
/// Що ловить сторож: рядки джерел <c>src/**/*.cs</c> (крім коментарів), де (A) <c>standing.RegistryDefId</c> порівнюється
/// оператором <c>==</c>/<c>!=</c>; (B) <c>X.LookupRegistryDefId</c> порівнюється з чимось, що НЕ є ні <c>null</c>, ні іншим
/// <c>*.LookupRegistryDefId</c> (порівняння двох цілей - не перевірка запису); (C) <c>*.RegistryDefId</c> порівнюється з
/// <c>*.LookupRegistryDefId</c>.
/// </para>
/// <para>
/// Виняткові файли (лише LookupTargetMatcher) і виняткові РЯДКИ - ЯВНІ списки нижче з причиною; сторож порожнього результату не вимагає (жодних «NotEmpty»-пасток):
/// самоперевірка шаблонів іде окремим тестом на прикладах рядків.
/// </para>
/// <para>
/// Мутація: повернути в <c>PatchCellsHandler.CheckLookupStandings</c> рядок
/// <c>standing.RegistryDefId != column.LookupRegistryDefId</c> - сторож червоніє з файлом і рядком.
/// </para>
/// </remarks>
public sealed class LookupTargetComparisonGuardTests
{
    /// <summary>Виняткові файли: шлях відносно кореня репозиторію (з <c>/</c>) -> причина.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Application/Permits/Groups/LookupTargetMatcher.cs"] =
            "єдина точка порівняння: збіг реєстру - швидкий шлях до звернення до резолвера",
    };

    /// <summary>
    /// Виняткові РЯДКИ (не файли): шлях -> (уривок рядка -> причина). Інше пряме порівняння в тому ж файлі сторож побачить.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> AllowedLines = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Infrastructure/Persistence/RowActivityFilterSource.cs"] = new(StringComparer.Ordinal)
        {
            ["c.LookupRegistryDefId == registryId"] =
                "SQL-вибір колонок, прив'язаних до КОНКРЕТНОГО довідника статусів Land (registryId), а не перевірка належності запису",
        },
        ["src/Ecr.Infrastructure/Persistence/PermitGroupStore.cs"] = new(StringComparer.Ordinal)
        {
            ["c.LookupRegistryDefId == registryDefId"] =
                "SQL «чи є посилання на реєстр групи» (колонки, включно з м'яко видаленими, - FK Restrict); вибірка за цілю, а не перевірка належності запису",
            ["f.LookupRegistryDefId == registryDefId"] =
                "те саме для полів шапки",
        },
        ["src/Ecr.Infrastructure/Persistence/RegistryStore.cs"] = new(StringComparer.Ordinal)
        {
            ["where column.LookupRegistryDefId == registryDefId"] =
                "SQL «де використовується довідник» (колонки шаблонів з ціллю = registryDefId); вибірка за цілю, а не перевірка належності запису",
        },
    };

    private static readonly Regex[] Patterns =
    [
        // (A) standing.RegistryDefId ==/!= ...   або   ... ==/!= standing.RegistryDefId
        new(@"\bstanding\.RegistryDefId\s*(==|!=)|(==|!=)\s*standing\.RegistryDefId\b", RegexOptions.Compiled),

        // (B) X.LookupRegistryDefId ==/!= <не null і не інша *.LookupRegistryDefId>
        new(@"\.LookupRegistryDefId\s*(==|!=)\s*(?!null\b)(?![\w.!?]*LookupRegistryDefId)[A-Za-z_(]", RegexOptions.Compiled),

        // (C) *.RegistryDefId ==/!= *.LookupRegistryDefId (і навпаки)
        new(@"(?<![A-Za-z])RegistryDefId\s*(==|!=)\s*[\w.!?]*LookupRegistryDefId", RegexOptions.Compiled),
        new(@"\bLookupRegistryDefId\s*(==|!=)\s*[\w.!?]*(?<![Ll]ookup)RegistryDefId\b", RegexOptions.Compiled),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порівняння_реєстру_запису_з_ціллю_Lookup_лише_через_LookupTargetMatcher()
    {
        var root = SolutionRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || Allowed.ContainsKey(relative))
            {
                continue;
            }

            var line = 0;
            foreach (var text in File.ReadLines(file))
            {
                line++;
                if (IsComment(text))
                {
                    continue;
                }

                if (Patterns.Any(p => p.IsMatch(text))
                    && !(AllowedLines.TryGetValue(relative, out var lines)
                         && lines.Keys.Any(fragment => text.Contains(fragment, StringComparison.Ordinal))))
                {
                    offenders.Add($"{relative}:{line}: {text.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Пряме порівняння реєстру запису з ціллю Lookup (ціль може бути групою PRMG_): "
            + "використай LookupTargetMatcher.Belongs.\n" + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    /// <summary>Самоперевірка шаблонів: названі форми ловляться, дозволені - ні (сторож не вироджується в «завжди зелений»).</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("if (standing.RegistryDefId != column.LookupRegistryDefId)", true)]
    [InlineData("|| fieldsOfEntries[e.FieldId].LookupRegistryDefId is { } registryId && standing.RegistryDefId != registryId", true)]
    [InlineData("|| column.LookupRegistryDefId != registryDefId)", true)]
    [InlineData("if (!standings.TryGetValue(pair.From, out var old) || old.RegistryDefId != src.LookupRegistryDefId)", true)]
    [InlineData("&& src.LookupRegistryDefId != dst.LookupRegistryDefId)", false)]
    [InlineData("where column.LookupRegistryDefId != null", false)]
    [InlineData("|| existing.LookupRegistryDefId != command.LookupRegistryDefId))", false)]
    [InlineData("if (entry is null || entry.RegistryDefId != definition.Id)", false)]
    [InlineData("!LookupTargetMatcher.Belongs(groups, profile, column.LookupRegistryDefId, standing.RegistryDefId)", false)]
    public void Шаблони_сторожа_ловлять_пряме_порівняння_і_пропускають_дозволене(string text, bool flagged)
        => Assert.Equal(flagged, Patterns.Any(p => p.IsMatch(text)));

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
