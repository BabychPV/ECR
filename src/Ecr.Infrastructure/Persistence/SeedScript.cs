using System.Reflection;
using System.Text;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Склад сіду: <c>Persistence/Sql/seed/00-prelude.sql</c> → <c>seed/features/*.sql</c>
/// (за іменем файла, <see cref="StringComparer.Ordinal"/>) → <c>seed/99-epilogue.sql</c>.
/// </summary>
/// <remarks>
/// ⛔ Один порядок на всіх: його бере <see cref="SeedRunner"/> (вбудовані ресурси) і
/// тести/сторожі (файли дерева, <see cref="ReadDirectory"/>). Дві копії правила
/// порядку рано чи пізно розійшлися б, і тест перевіряв би не той текст, що
/// виконує застосунок.
///
/// ⚠ Файл фічі лише наповнює тимчасові таблиці (<c>#UiEn</c> — en-каталог, <c>#I18N</c>,
/// <c>#UiChanged</c>, <c>#UiRemoved</c>), які створює пролог і застосовує епілог; тому порядок
/// фіч між собою не впливає на результат (сторож <c>SeedSplitLayoutTests</c>).
/// Поділ колишнього <c>09-seed.sql</c> — <c>tools/split-seed.ps1</c>.
/// </remarks>
public static class SeedScript
{
    /// <summary>Пролог — виконується першим.</summary>
    public const string PreludeFile = "00-prelude.sql";

    /// <summary>Епілог — виконується останнім.</summary>
    public const string EpilogueFile = "99-epilogue.sql";

    /// <summary>Каталог файлів фіч відносно <c>seed/</c>.</summary>
    public const string FeaturesFolder = "features";

    /// <summary>Префікс <c>LogicalName</c> вбудованих частин (<c>Ecr.Infrastructure.csproj</c>).</summary>
    public const string ResourcePrefix = "EcrSeed/";

    /// <summary>
    /// Відносні шляхи частин (через <c>/</c>) у порядку виконання.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Немає прологу чи епілогу або є частина поза <c>features/*.sql</c> — краще
    /// не стартувати, ніж мовчки виконати сід без частини чи з чужим файлом.
    /// </exception>
    public static IReadOnlyList<string> Order(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var all = relativePaths.Select(p => p.Replace('\\', '/')).ToList();
        var unexpected = all
            .Where(p => p != PreludeFile && p != EpilogueFile && !IsFeature(p))
            .ToList();
        if (unexpected.Count > 0)
        {
            throw new InvalidOperationException(
                "Сід: невідомі частини " + string.Join(", ", unexpected)
                + $" — дозволено лише {PreludeFile}, {EpilogueFile} і {FeaturesFolder}/*.sql.");
        }

        foreach (var required in new[] { PreludeFile, EpilogueFile })
        {
            if (all.Count(p => p == required) != 1)
            {
                throw new InvalidOperationException($"Сід: немає частини {required}.");
            }
        }

        var features = all.Where(IsFeature).ToList();
        features.Sort(StringComparer.Ordinal);

        return [PreludeFile, .. features, EpilogueFile];
    }

    /// <summary>Зводить частини в один скрипт у порядку <see cref="Order"/>.</summary>
    public static string Compose(IReadOnlyDictionary<string, string> partsByRelativePath)
    {
        ArgumentNullException.ThrowIfNull(partsByRelativePath);

        var normalized = partsByRelativePath.ToDictionary(p => p.Key.Replace('\\', '/'), p => p.Value, StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var path in Order(normalized.Keys))
        {
            var text = normalized[path];
            builder.Append(text);

            // ⚠ Файл без перекладу рядка в кінці склеїв би свій `GO` з першим рядком
            // наступного — і батч розрізало б не там.
            if (text.Length > 0 && text[^1] != '\n')
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Сід із ресурсів збірки — те, що виконує застосунок.</summary>
    public static string ReadEmbedded() => Compose(ReadEmbeddedParts());

    /// <summary>Частини сіду з ресурсів збірки: відносний шлях → текст.</summary>
    public static IReadOnlyDictionary<string, string> ReadEmbeddedParts()
    {
        var assembly = typeof(SeedScript).Assembly;
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            parts[name[ResourcePrefix.Length..].Replace('\\', '/')] = ReadResource(assembly, name);
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Ресурси сіду ({ResourcePrefix}*) не вбудовані у збірку. " +
                "Перевірте <EmbeddedResource> у Ecr.Infrastructure.csproj.");
        }

        return parts;
    }

    /// <summary>Сід із каталогу <c>seed/</c> дерева (тести, інструменти).</summary>
    /// <param name="seedDirectory">Абсолютний шлях до <c>Persistence/Sql/seed</c>.</param>
    public static string ReadDirectory(string seedDirectory) => Compose(ReadDirectoryParts(seedDirectory));

    /// <summary>Частини сіду з каталогу: відносний шлях → текст.</summary>
    public static IReadOnlyDictionary<string, string> ReadDirectoryParts(string seedDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(seedDirectory);

        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(seedDirectory, "*.sql", SearchOption.AllDirectories))
        {
            parts[Path.GetRelativePath(seedDirectory, file).Replace('\\', '/')] = File.ReadAllText(file, Encoding.UTF8);
        }

        return parts;
    }

    private static bool IsFeature(string relativePath)
        => relativePath.StartsWith(FeaturesFolder + "/", StringComparison.Ordinal)
           && relativePath.EndsWith(".sql", StringComparison.Ordinal)
           && relativePath.IndexOf('/', FeaturesFolder.Length + 1) < 0;

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Ресурс '{name}' не відкривається.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
