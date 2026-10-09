using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Конвеєр складання не втрачає жодного гейта (<c>II.12</c>, <c>H-22</c>).
/// </summary>
/// <remarks>
/// ⛔ Конвеєр розкладено на завдання, які йдуть паралельно: доступність іде
/// понад двадцять хвилин і в одному завданні з рештою тримала б усе решту
/// заручником. Але «розділити на завдання» і «тихо викинути гейт» на вигляд
/// однакові — обидва дають зелений прогін.
///
/// ⚠ Саме тому перевірка йде не по тому, що конвеєр РОБИТЬ, а по тому, що він
/// НЕ робить: перелік кроків здобувається зі скрипта, перелік виконуваних —
/// із конвеєра, і різниця мусить бути названа поіменно з причиною.
///
/// ⛔ Перелік кроків читається з ТЕКСТУ <c>verify-all.ps1</c>, а не з його
/// прогону. Прогін дав би той самий перелік і коштував би запуску PowerShell
/// із кожного тесту; головне ж — питання тут не «що виконається», а «що
/// оголошено», і на нього відповідає саме текст. Той самий спосіб, що в
/// <see cref="DeleteBehaviorTests"/> і <see cref="ClientErrorCodeTests"/>.
/// </remarks>
public sealed partial class CiPipelineTests
{
    /// <summary>Оголошення кроку в <c>verify-all.ps1</c>.</summary>
    [GeneratedRegex(@"^\s*Step\s+'([^']+)'", RegexOptions.Multiline)]
    private static partial Regex DeclaredStep();

    /// <summary>Перелік кроків, які бере завдання конвеєра.</summary>
    [GeneratedRegex(@"-Only\s+((?:'[^']*'\s*,\s*)*'[^']*')")]
    private static partial Regex OnlyList();

    /// <summary>Одне ім'я в такому переліку.</summary>
    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex Quoted();

    /// <summary>Пароль, присвоєний сталою, а не змінною.</summary>
    /// <remarks>
    /// ⚠ Значення, що починається з <c>$</c> або <c>${{</c>, — це посилання
    /// на змінну чи вираз, і воно дозволене. Усе інше — стала.
    /// </remarks>
    [GeneratedRegex(@"MSSQL_SA_PASSWORD\s*[:=]\s*[""']?(?![$\s])[^\s""']+")]
    private static partial Regex LiteralPassword();

    /// <summary>Названий виняток: <c># ci-exempt: крок — причина</c>.</summary>
    [GeneratedRegex(@"^#\s*ci-exempt:\s*(.+?)\s+—\s+(\S.*)$", RegexOptions.Multiline)]
    private static partial Regex Exempt();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_крок_перевірки_або_виконується_конвеєром_або_названий_винятком()
    {
        var declared = DeclaredStep().Matches(Script())
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);

        var workflow = Workflow();
        var covered = Covered(workflow);
        var exempt = Exempt().Matches(workflow)
            .Select(m => m.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

        // ⛔ Головне твердження: крок, якого не бере жодне завдання і який не
        // названий винятком, — це гейт, що зник із конвеєра непоміченим.
        var dropped = declared.Except(covered).Except(exempt)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(dropped);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Конвеєр_не_називає_кроків_яких_не_існує()
    {
        // ⚠ Зворотний напрям, і він не декоративний: `-Only 'Тести .NET '` із
        // зайвим пробілом не виконує НІЧОГО і виходить нулем. Помилка в імені
        // виглядає точнісінько як пройдений гейт.
        var declared = DeclaredStep().Matches(Script())
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var workflow = Workflow();

        var unknown = Covered(workflow)
            .Concat(Exempt().Matches(workflow).Select(m => m.Groups[1].Value.Trim()))
            .Where(name => !declared.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Виняток_несе_причину_а_не_саме_ім_я()
    {
        // ⛔ Виняток без причини — це спосіб вимкнути гейт, не сказавши цього.
        // Формат вимагає тире і тексту після нього саме тому, що інакше
        // перелік винятків через місяць складається з самих імен.
        var workflow = Workflow();

        var lines = workflow.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("# ci-exempt:", StringComparison.Ordinal)
                        || line.StartsWith("ci-exempt:", StringComparison.Ordinal))
            .ToList();

        var withoutReason = lines
            .Where(line => !Exempt().IsMatch(line.TrimStart('#', ' ').Insert(0, "# ")))
            .ToList();

        Assert.Empty(withoutReason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пароль_контейнера_не_сталий_і_прихований()
    {
        // ⛔ `D-11` / `ФВ-6.11`: у файлах живуть ІМЕНА секретів, не значення.
        // Пароль контейнера — спокуса саме тут: база одноразова, і «та це ж
        // тимчасово» звучить переконливо рівно до першого копіювання файла.
        //
        // ⚠ Перша збірка вимагала секрету репозиторію — і виявилося, що це
        // вимикає гейт розгортання в кожного, хто секрету не завів: завдання
        // не запускалося ЖОДНОГО разу. Вимога, яка нічого не захищає і при
        // цьому спиняє перевірку, гірша за її відсутність. Тепер пароль
        // генерується на прогін; правило лишається те саме — сталого немає.
        var workflow = Workflow();

        // Присвоєння сталою: значення, яке не починається з `$` або `${{`.
        var literal = LiteralPassword().Match(workflow);

        Assert.False(
            literal.Success,
            literal.Success ? $"Сталий пароль у конвеєрі: {literal.Value}" : string.Empty);

        // ⛔ Згенерований пароль без маски потрапляє в журнал прогону, який
        // видно всім, хто бачить репозиторій. Одноразовість цього не рятує:
        // журнал переживає контейнер.
        Assert.Contains("::add-mask::", workflow, StringComparison.Ordinal);
        Assert.Contains("openssl rand", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Гейти_не_пропускаються_для_PR_з_dev_integration(string file)
    {
        // ⛔ L10-09, D-335 (HU-12 R5 = A): 057a1118 пропускав гейти на PR з head `dev/integration`,
        // а пропущену обов'язкову перевірку GitHub зараховує як пройдену. Жодна умова в
        // конвеєрі не дивиться на `head_ref == 'dev/integration'`, крім джоба `pr-source`,
        // який такий PR валить.
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", file));

        var skips = workflow.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains("head_ref == 'dev/integration'", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        var allowed = file == "ci.yml"
            ? new[] { "if: github.event_name == 'pull_request' && github.head_ref == 'dev/integration'" }
            : Array.Empty<string>();
        Assert.Equal(allowed, skips);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void PR_з_dev_integration_валиться_окремим_джобом()
    {
        // D-335: тижневий PR — з `rc/*` (CLAUDE.md §8); PR з `dev/integration` червоний з поясненням.
        var workflow = Workflow().Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = workflow.IndexOf("\n  pr-source:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Немає джоба pr-source у ci.yml.");
        var next = workflow.IndexOf("\n  lane-shape:", start, StringComparison.Ordinal);
        var job = next > start ? workflow[start..next] : workflow[start..];

        Assert.Contains("github.head_ref == 'dev/integration'", job, StringComparison.Ordinal);
        Assert.Contains("exit 1", job, StringComparison.Ordinal);
        Assert.Contains("rc/", job, StringComparison.Ordinal);
    }

    private static List<string> Covered(string workflow)
        => workflow.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .SelectMany(line => OnlyList().Matches(line))
            .SelectMany(m => Quoted().Matches(m.Groups[1].Value).Select(q => q.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Script()
        => File.ReadAllText(Path.Combine(Root(), "tools", "verify-all.ps1"));

    private static string Workflow()
        => File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "ci.yml"));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
