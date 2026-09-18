using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// До чужої бази ходить адаптер, а не наша схема (ФВ-11.8, <c>B18</c> §14.5).
/// </summary>
/// <remarks>
/// ⛔ Чинна система дотягується до FLERT синонімами
/// (<c>Utility_RebindFlertSynonyms</c>) і набором RTQP-об'єктів
/// (<c>B14</c> §7). Синонім і linked server — це залежність від чужого
/// сервера, вшита в НАШУ схему: вона переживає розгортання, її не видно в
/// міграціях, і ламається вона мовчки при переїзді середовища. Так уже було в
/// зворотний бік: адаптеру заборонено створювати в'юхи в базі джерела, бо
/// «вони не їдуть із застосунком» (ER-I-01) — тут та сама помилка, зроблена
/// навпаки.
/// <para>
/// ⚠ Сторож на ЗАБОРОНУ, а не на присутність, тому виглядає порожнім і зараз
/// зелений «сам собою». Його цінність з'явиться в день, коли хтось
/// розв'язуватиме доступ до наступної чужої бази — і синонім буде на порядок
/// швидшим шляхом, ніж джерело з транспортом <c>Sql</c>.
/// </para>
/// </remarks>
public sealed partial class ForeignDatabaseAccessTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-11.8")]
    public void Наша_схема_не_дотягується_до_чужої_бази_синонімом_чи_linked_server()
    {
        var root = SourceTree.Root;

        var scanned = new List<string>();
        var offenders = new List<string>();

        foreach (var path in Files(root))
        {
            scanned.Add(path);

            foreach (Match match in ForeignAccessRegex.Matches(File.ReadAllText(path)))
            {
                offenders.Add($"{Path.GetRelativePath(root, path).Replace('\\', '/')}: {match.Value}");
            }
        }

        // ⚠ Без цього рядка тест був би зелений і тоді, коли шлях до скриптів
        // зміниться і сканувати стане нічого — тобто саме тоді, коли він
        // потрібен. Порожній знаменник доводить не відсутність порушень, а
        // відсутність перевірки.
        Assert.NotEmpty(scanned);

        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    /// <summary>Файли схеми й розгортання: сирі скрипти, міграції, інсталятор.</summary>
    /// <param name="root">Корінь репозиторію.</param>
    private static IEnumerable<string> Files(string root)
    {
        foreach (var directory in new[] { "src", "tools" })
        {
            var start = Path.Combine(root, directory);

            if (!Directory.Exists(start))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(start, "*.*", SearchOption.AllDirectories))
            {
                var relative = path.Replace('\\', '/');

                if (relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// Способи дотягнутися до чужого сервера з нашої схеми.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>OPENQUERY</c>/<c>OPENROWSET</c>/<c>OPENDATASOURCE</c> тут разом із
    /// синонімом і linked server навмисно: вони дають рівно той самий наслідок
    /// (запит до чужого сервера з нашого T-SQL), лише без окремого об'єкта в
    /// схемі — тобто ще менш помітно.
    /// </remarks>
    [GeneratedRegex(
        @"(?i)\b(CREATE\s+SYNONYM|sp_addlinkedserver|sp_addlinkedsrvlogin|OPENQUERY|OPENROWSET|OPENDATASOURCE)\b")]
    private static partial Regex ForeignAccessRegex { get; }
}
