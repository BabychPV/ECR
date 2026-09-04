using System.Text.RegularExpressions;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Доступ до вихідних текстів проєкту для правил, які неможливо перевірити
/// по збірці.
/// </summary>
/// <remarks>
/// Частина архітектурних правил живе на рівні **тексту**, а не типів:
/// `.Result`, `async void`, `DateTime.Now`, `ToList()` без `Take` — це все
/// зникає в IL або стає невідрізнюваним від дозволених випадків. Тому такі
/// правила перевіряються по джерелах.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1 (`Q-057`): без нього
/// `LayerRulesTests` і `ForbiddenApiTests` довелося б звести до перевірок по
/// збірці, тобто не перевіряти половину правил узагалі.
/// </remarks>
internal static partial class SourceTree
{
    /// <summary>Корінь репозиторію.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Файли <c>.cs</c> проєкту без згенерованих і тестових.</summary>
    /// <param name="projects">Імена проєктів у <c>src/</c>; порожньо — усі.</param>
    public static IReadOnlyList<SourceFile> Production(params string[] projects)
    {
        var root = Path.Combine(Root, "src");
        var files = new List<SourceFile>();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');

            // obj/bin — компіляторний непотріб; Migrations — згенерований код,
            // на нього правила стилю не поширюються (`Q-034`).
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/Migrations/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal))
            {
                continue;
            }

            if (projects.Length > 0
                && !projects.Any(p => relative.StartsWith($"src/{p}/", StringComparison.Ordinal)))
            {
                continue;
            }

            files.Add(new SourceFile(relative, File.ReadAllText(path)));
        }

        return files;
    }

    /// <summary>Рядки коду без коментарів і рядкових літералів.</summary>
    /// <remarks>
    /// ⚠ Без цього кожне правило ловило б саме себе: у коментарях цього
    /// проєкту слова `DateTime.Now` і `.Result` зустрічаються рівно тому, що
    /// пояснюють, чому їх не можна писати.
    /// </remarks>
    public static IEnumerable<(int Line, string Text)> CodeLines(this SourceFile file)
    {
        var inBlockComment = false;
        var lines = file.Text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (inBlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                line = line[(end + 2)..];
                inBlockComment = false;
            }

            var blockStart = line.IndexOf("/*", StringComparison.Ordinal);
            if (blockStart >= 0)
            {
                inBlockComment = !line[blockStart..].Contains("*/", StringComparison.Ordinal);
                line = line[..blockStart];
            }

            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = StringLiteral().Replace(line, "\"\"");

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return (i + 1, line);
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Не знайдено кореня репозиторію (каталогу з src/).");
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();
}

/// <summary>Файл вихідного тексту.</summary>
/// <param name="Path">Шлях відносно кореня репозиторію.</param>
/// <param name="Text">Вміст.</param>
internal sealed record SourceFile(string Path, string Text);
