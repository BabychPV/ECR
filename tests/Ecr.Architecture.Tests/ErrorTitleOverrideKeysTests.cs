using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// T3-07: <c>ExceptionHandlingMiddleware.LocalizedTitleAsync</c> бере заголовок із ключа
/// <c>&lt;messageKey&gt;.title</c> РАНІШЕ за <c>err.&lt;код&gt;</c>. Тож будь-який сідовий ключ
/// <c>err.*.title</c> мовчки перекриває заголовок відповідного кидка. Перелік таких ключів — явний:
/// новий ключ із цим закінченням називається тут поіменно, а не з'являється випадково.
/// Мутація: додати в сід <c>err.X.y.title</c> без запису тут — тест червоний.
/// </summary>
public sealed partial class ErrorTitleOverrideKeysTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    private static readonly string[] Allowed = ["err.ECR-TMPL-0409.relationCodeTaken.title"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключі_err_title_у_сіді_лише_з_переліку()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var found = TitleOverrideKey().Matches(text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // ⛔ Порожня множина = регулярка не збіглася, а не «усе гаразд».
        Assert.Contains("err.ECR-TMPL-0409.relationCodeTaken.title", found);

        var unexpected = found.Except(Allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "Ключі err.*.title у сіді перекривають заголовок кидка з відповідним messageKey: "
            + string.Join(", ", unexpected)
            + ". Додай їх до Allowed, якщо перекриття задумане.");
    }

    [GeneratedRegex(@"N'(err\.[^']+\.title)'")]
    private static partial Regex TitleOverrideKey();
}
