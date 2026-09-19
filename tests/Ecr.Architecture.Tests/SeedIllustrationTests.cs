using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ілюстративні копії сіду в документації не розходяться з самим сідом.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Що сталося без цього сторожа.</b> У <c>docs/build/02a-db-schema.md</c>
/// стояв приклад «мінімального публічного набору» рядків інтерфейсу з
/// префіксом <c>auth.</c> — і сімох із тих ключів у сіді не існувало
/// <b>ніколи</b>: сторінка входу просить <c>login.*</c>. Той, хто пішов би за
/// прикладом, засіяв би ключі, яких ніхто не читає, і на екрані лишилося б
/// <c>⟦login.title⟧</c> — рівно та поломка, від якої застерігає коментар над
/// самим прикладом. Розходження прожило непоміченим, бо жоден сторож не
/// звіряв копію з джерелом.
/// </para>
/// <para>
/// ⚠ Сторож навмисно вузький: він перевіряє <b>лише існування ключа</b> в
/// сіді, а не текст, не мову, не порядок і не повноту переліку. Приклад на те
/// й приклад, що показує форму на кількох рядках; вимагати від нього збігу
/// один-в-один означало б ламати його на кожному новому рядку сіду — і тоді
/// наступний автор прибрав би сторожа, а не полагодив копію.
/// </para>
/// <para>
/// ⚠ Це перевірка по ТЕКСТУ файлів, з усією її крихкістю. Вона обмежена
/// рівно тим, що не має іншого способу перевірки: документ не компілюється й
/// не виконується, тож розходження з кодом видно тільки так.
/// </para>
/// </remarks>
public sealed class SeedIllustrationTests
{
    /// <summary>Джерело істини для рядків інтерфейсу.</summary>
    private const string SeedPath = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    /// <summary>Документ з ілюстративною копією.</summary>
    private const string DocPath = "docs/build/02a-db-schema.md";

    /// <summary>Ключ у вигляді <c>N'щось.щось'</c> — так їх пишуть в обох файлах.</summary>
    private static readonly Regex KeyLiteral = new(
        @"N'(?<key>(?:login|auth|common|err|doc|grid|jobs|periods|registries|unsaved)\.[^']*)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_ключ_з_ілюстрації_у_схемі_існує_в_сіді()
    {
        var root = SolutionRoot();
        var seed = File.ReadAllText(Path.Combine(root, SeedPath.Replace('/', Path.DirectorySeparatorChar)));
        var doc = File.ReadAllText(Path.Combine(root, DocPath.Replace('/', Path.DirectorySeparatorChar)));

        var seedKeys = KeyLiteral.Matches(seed)
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            seedKeys.Count > 50,
            $"У сіді знайдено лише {seedKeys.Count} ключів — регулярка більше не збігається з тим, " +
            "як їх записують, і сторож нічого не перевіряє.");

        /*
         * ⚠ Контрольне твердження вище не формальність: якби формат запису
         * ключів змінився, `seedKeys` став би порожнім, і тест був би зеленим
         * на будь-якій ілюстрації — тобто перетворився б на власну протилежність.
         */
        var missing = KeyLiteral.Matches(doc)
            .Select(m => m.Groups["key"].Value)
            .Where(key => !seedKeys.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"У {DocPath} названо ключі, яких немає в {SeedPath}: {string.Join(", ", missing)}. " +
            "Приклад, що називає неіснуючий ключ, шкідливіший за відсутній: той, хто піде за ним, " +
            "засіє рядки, яких ніхто не читає.");
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
