using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ключ каталогу з плейсхолдерами, що їде клієнтові НЕ винятком, а полем звіту
/// (помилка рядка імпорту, діагностика, порушення правила), приходить разом із
/// підстановками.
/// </summary>
/// <remarks>
/// ⛔ Клас дефекту <c>D1</c>: імпорт записів довідника з CSV віддавав
/// <c>RegistryEntryImportError(…, "err.ECR-REG-0422.valueNotNumber")</c> без
/// жодної підстановки, і клієнтський <c>t()</c> показував людині «The value
/// "{value}" is not a number» фігурними дужками. Кидки винятків цей клас уже не
/// пропускають — їх стереже <see cref="MessageKeyRatchetTests"/>
/// (<c>Кожен_плейсхолдер_шаблону_має_підстановку_в_кидку</c>), але він дивиться
/// лише на <c>["messageKey"] = "…"</c> у подробицях винятку. Звіт рядків — інший
/// канал: ключ іде позиційним аргументом запису, часто константою
/// (<c>RegistryEntryWriter.EntryCodeTakenKey</c>), і цей канал не стеріг ніхто.
///
/// ⚠ Межа сторожа — ТЕКСТ місця створення: ключ має бути літералом
/// <c>"err.…"</c> або константою <c>…Key</c>, і кожен плейсхолдер шаблону — бути
/// названим у тих самих аргументах як <c>"ім'я"</c>. Ключ, що приходить змінною з
/// винятку (<c>RegistryEntryWriter.MessageKeyOf(ex)</c>), перевірити звідси не
/// можна; там підстановки беруться з подробиць того самого винятку
/// (<c>ParamsOf</c>), а самі подробиці стереже сторож кидків.
/// </remarks>
public sealed partial class RowReportPlaceholderTests
{
    /// <summary>
    /// Записи, чий ключ НЕ показується людині через каталог, а лише пишеться
    /// технічним рядком журналу інтеграції (<c>messageKey=…</c> поруч із сирими
    /// значеннями) — підстановки там нема куди класти й нікому читати.
    /// </summary>
    private static readonly Dictionary<string, string> JournalOnly = new(StringComparer.Ordinal)
    {
        ["CurrentValueFailure"] = "RegistrySyncJob пише рядок «path=…; messageKey=…» у журнал інтеграції",
        ["CreateRefusal"] = "RegistrySyncJob.Refused пише «code=…; id=…; messageKey=…» у журнал інтеграції",
        ["SyncEvent"] = "рядок журналу інтеграції з усіма значеннями поруч із messageKey",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_звіту_з_плейсхолдерами_створюється_разом_із_підстановками()
    {
        var failures = Check(SourceTree.Production(), Templates(), out var checkedSites);

        // ⛔ Регулярка, що перестала збігатися, дала б нуль перевірок і ЗЕЛЕНЕ.
        Assert.True(checkedSites > 10, $"Сторож перевірив лише {checkedSites} місць — регулярка зламалась?");
        Assert.True(
            failures.Count == 0,
            "Ключ каталогу з плейсхолдерами створено без підстановок — клієнт покаже фігурні дужки (клас D1). "
            + "Передай підстановки поруч із ключем (Params/MessageParams):"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_бачить_і_літерал_і_константу_і_не_чіпає_журнал()
    {
        // ⛔ Доказ, що сторож уміє червоніти: рівно форма D1 (константа без
        // підстановок) і літерал без підстановок — червоні; з підстановками і
        // рядок журналу — зелені.
        const string sample = """
            public static class Keys { public const string NotNumberKey = "err.X-0422.notNumber"; }
            class A
            {
                void M()
                {
                    errors.Add(new RowError(1, "E1", NotNumberKey));
                    errors.Add(new RowError(2, "E2", "err.X-0422.notNumber"));
                    errors.Add(new RowError(3, "E3", NotNumberKey, new() { ["value"] = raw }));
                    errors.Add(new RowError(4, "E4", "err.X-0422.plain"));
                    log.Add(new SyncEvent(1, NotNumberKey));
                }
            }
            """;

        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["err.X-0422.notNumber"] = "The value \"{value}\" is not a number.",
            ["err.X-0422.plain"] = "No placeholders.",
        };

        var failures = Check([new SourceFile("src/Sample.cs", sample)], templates, out var checkedSites);

        Assert.Equal(4, checkedSites);
        Assert.Equal(2, failures.Count);
        Assert.Contains("src/Sample.cs:6:", failures[0], StringComparison.Ordinal);
        Assert.Contains("src/Sample.cs:7:", failures[1], StringComparison.Ordinal);
    }

    private static List<string> Check(
        IReadOnlyList<SourceFile> files, Dictionary<string, string> templates, out int checkedSites)
    {
        // Константи ключів — з усього дерева: `RegistryEntryWriter.EntryCodeTakenKey`
        // створюється в одному файлі, а береться в іншому.
        var constants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match m in KeyConstant().Matches(file.Text))
            {
                if (!constants.TryGetValue(m.Groups[1].Value, out var set))
                {
                    constants[m.Groups[1].Value] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(m.Groups[2].Value);
            }
        }

        var failures = new List<string>();
        checkedSites = 0;

        foreach (var file in files)
        {
            foreach (Match site in Construction().Matches(file.Text))
            {
                var type = site.Groups[1].Value;
                var arguments = Balanced(file.Text, site.Index + site.Length - 1);

                // Подробиці винятку — канал сторожа кидків, не цього.
                if (JournalOnly.ContainsKey(type) || arguments.Contains("[\"messageKey\"]", StringComparison.Ordinal))
                {
                    continue;
                }

                var keys = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Match literal in KeyLiteral().Matches(arguments))
                {
                    keys.Add(literal.Groups[1].Value);
                }

                foreach (Match identifier in KeyIdentifier().Matches(arguments))
                {
                    if (constants.TryGetValue(identifier.Groups[1].Value, out var named))
                    {
                        keys.UnionWith(named);
                    }
                }

                foreach (var key in keys)
                {
                    if (!templates.TryGetValue(key, out var template))
                    {
                        continue;
                    }

                    checkedSites++;

                    var missing = Placeholder().Matches(template)
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .Where(name => !arguments.Contains($"\"{name}\"", StringComparison.Ordinal))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        failures.Add(
                            $"{file.Path}:{Line(file.Text, site.Index)}: new {type}(…) з ключем {key} — "
                            + $"шаблон чекає {string.Join(", ", missing.Select(n => "{" + n + "}"))}, "
                            + "а підстановок із такими іменами в аргументах немає.");
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>Шаблони <c>err.*</c> сіду (en): ключ → текст.</summary>
    private static Dictionary<string, string> Templates()
    {
        var seed = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in SeedTemplate().Matches(seed))
        {
            templates[m.Groups[1].Value] = m.Groups[2].Value;
        }

        Assert.NotEmpty(templates);
        return templates;
    }

    /// <summary>Аргументи від відкритої дужки до парної закритої (рядки — без підрахунку дужок).</summary>
    private static string Balanced(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }

                        i++;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return text[openParen..(i + 1)];
                    }

                    break;
            }
        }

        return text[openParen..];
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"\bnew\s+([A-Z]\w*)\s*\(")]
    private static partial Regex Construction();

    [GeneratedRegex(@"const\s+string\s+(\w+Key)\s*=\s*""(err\.[^""]+)""")]
    private static partial Regex KeyConstant();

    [GeneratedRegex(@"""(err\.[^""]+)""")]
    private static partial Regex KeyLiteral();

    /// <summary>
    /// Ідентифікатор константи ключа. ⚠ Голе <c>MessageKey</c> — це властивість
    /// запису (<c>error.MessageKey</c>), а не константа: однойменні константи
    /// різних класів дали б хибні ключі кожному переносу ключа з запису в запис.
    /// </summary>
    [GeneratedRegex(@"\b((?!MessageKey\b)[A-Z]\w*Key)\b")]
    private static partial Regex KeyIdentifier();

    [GeneratedRegex(@"\(\s*N'(err\.[^']+)'\s*,\s*N'en'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedTemplate();

    [GeneratedRegex(@"\{([A-Za-z_]\w*)\}")]
    private static partial Regex Placeholder();
}
