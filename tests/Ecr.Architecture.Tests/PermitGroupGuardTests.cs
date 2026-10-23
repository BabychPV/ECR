using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторожі груп типів дозволів (Permits, PRMG): прихований реєстр <c>PRMG_&lt;КОД&gt;</c> керується лише групою, а порт
/// <c>IPermitGroupResolver</c>/<c>ILookupTargetResolver</c> кличеться тільки після <c>EnsureFreshAsync</c> і має одну реалізацію.
/// </summary>
/// <remarks>
/// ⚠ Сторожі по тексту джерел крихкі (падають на переносі рядка) - тому кожен спирається на ім'я типу чи методу, а не на форму
/// виразу. Мутації: (1) прибрати <c>RequireNotManaged</c> з писача записів - червоніє «Шляхи_запису_довідників…»; (2) додати літерал
/// <c>"PRMG_"</c> у будь-який інший файл - «Префікс_PRMG…»; (3) прибрати <c>EnsureFreshAsync</c> з піклера групи - «Споживач_порту…»;
/// (4) додати другий клас, що реалізує порт, - «Порт_резолвера…».
/// </remarks>
public sealed partial class PermitGroupGuardTests
{
    /// <summary>Файли, яким дозволено знати префікс <c>PRMG_</c>: усе інше йде через <c>PermitGroupRegistryGuard</c>.</summary>
    private static readonly string[] PrefixOwners =
    [
        "src/Ecr.Application/Permits/Groups/PermitGroupErrors.cs",
        "src/Ecr.Application/Permits/Groups/PermitGroupRegistryGuard.cs",
        "src/Ecr.Infrastructure/Persistence/UnitOfWork.cs",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Префікс_PRMG_живе_лише_у_файлах_власниках()
    {
        var offenders = SourceTree.Production()
            .Where(f => !PrefixOwners.Contains(f.Path))
            .Where(f => PrefixLiteral().IsMatch(WithoutLineComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Шляхи_запису_довідників_викликають_сторож_прихованого_реєстру_групи()
    {
        var expectations = new Dictionary<string, (string Needle, int Min)>
        {
            ["src/Ecr.Application/Registries/RegistryEntryWriter.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/RegistryDefinitionHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/RegistryDefinitionDraftHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 3),
            ["src/Ecr.Application/Registries/RegistryAdminHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/CreateRegistryHandler.cs"] = ("PermitGroupRegistryGuard.RequireNotReserved", 1),
            ["src/Ecr.Application/Registries/RegistryEntryCsvHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Registries/ExternalRegistryGuard.cs"] = ("PermitGroupRegistryGuard.RequireNotManaged", 1),
            ["src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs"] = ("PermitGroupRegistryGuard.RequireNotRowActivityTarget", 1),
            ["src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs"] = ("PublishChecks.CheckGroupLookupFormulas", 1),
        };

        var files = SourceTree.Production("Ecr.Application").ToDictionary(f => f.Path);
        var failures = expectations
            .Where(e => !files.TryGetValue(e.Key, out var file)
                        || Regex.Count(WithoutLineComments(file.Text), Regex.Escape(e.Value.Needle)) < e.Value.Min)
            .Select(e => $"{e.Key}: потрібно ≥ {e.Value.Min} викликів {e.Value.Needle}")
            .ToList();

        Assert.Empty(failures);
    }

    /// <summary>
    /// PG-P3b №3: <c>CheckGroupLookupFormulas</c> мовчки пропускає нерозібраний вираз - це безпечно лише тому, що <c>PublishChecks.Run</c>
    /// (синтаксис формул) і <c>RuleExpressionChecks.Check</c> (V-19, правила) йдуть у <c>PublishTemplateVersionHandler</c> РАНІШЕ.
    /// Сторож порядку: перестановка воріт робить нерозібраний вираз над колонкою-групою непоміченим.
    /// Мутація: перенести виклик <c>CheckGroupLookupFormulas</c> вище за <c>Run</c> - червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ворота_публікації_розбирають_вирази_раніше_за_перевірку_груп()
    {
        var handler = SourceTree.Production("Ecr.Application")
            .Single(f => f.Path == "src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs");
        var code = WithoutLineComments(handler.Text);

        var run = code.IndexOf("PublishChecks.Run(", StringComparison.Ordinal);
        var rules = code.IndexOf("RuleExpressionChecks.Check(", StringComparison.Ordinal);
        var groups = code.IndexOf("PublishChecks.CheckGroupLookupFormulas(", StringComparison.Ordinal);

        Assert.True(run >= 0 && rules >= 0 && groups >= 0, "обробник має викликати Run, RuleExpressionChecks.Check і CheckGroupLookupFormulas");
        Assert.True(run < groups && rules < groups, "CheckGroupLookupFormulas має йти ПІСЛЯ Run і RuleExpressionChecks.Check");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Споживач_порту_груп_кличе_EnsureFreshAsync_до_читання_знімка()
    {
        var consumers = SourceTree.Production()
            .Where(f => !f.Path.StartsWith("src/Ecr.Application/Permits/Groups/", StringComparison.Ordinal))
            .Where(f => !f.Path.EndsWith("/DependencyInjection.cs", StringComparison.Ordinal))
            .Where(f => ResolverMention().IsMatch(WithoutLineComments(f.Text)))
            .ToList();

        // Порт має споживачів: сторож не повинен зеленіти на порожньому наборі.
        Assert.NotEmpty(consumers);

        // Свіжість знімка гарантує сам споживач (EnsureFreshAsync) або спільні помічники порту, що кличуть її всередині:
        // `LookupTargetMatcher.EnsureFreshOnMismatchAsync` (перевірки належності) і `LookupTargetEntries.ListAsync` (читання складу).
        // `SourceEventMapSupport.BuildSpecsAsync(` - лише пересилає резолвер у `SourceEventMapHandlers`, де є `EnsureFreshOnMismatchAsync`.
        // ⛔ Виклик, а не підрядок: коментарі (`//`, `///`, `/* */`) і рядкові літерали вирізаються, імена - з межею слова (`\b`).
        var offenders = consumers
            .Where(f => !CallsFreshness(f.Text))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);

        // Помічники лежать у каталозі, який сторож пропускає, тож їхню свіжість перевіряємо окремо: без неї спільні споживачі читали б застарілий знімок.
        var helpers = SourceTree.Production()
            .Where(f => f.Path.EndsWith("/Permits/Groups/LookupTargetMatcher.cs", StringComparison.Ordinal)
                        || f.Path.EndsWith("/Permits/Groups/LookupTargetEntries.cs", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, helpers.Count);
        Assert.All(helpers, f => Assert.True(FreshCall().IsMatch(CodeOnly(f.Text)), $"{f.Path}: немає виклику EnsureFreshAsync("));
    }

    /// <summary>
    /// Самоперевірка сторожа свіжості: коментар, документ-коментар, блок-коментар, рядковий літерал і суфіксне ім'я НЕ є викликом;
    /// справжній виклик (також з пробілом перед дужкою і через помічник) - так. Без цього сторож зеленів би на підрядку в коментарі.
    /// </summary>
    [Theory]
    [InlineData("// await resolver.EnsureFreshAsync(ct);", false)]
    [InlineData("/// <see cref=\"X.EnsureFreshAsync(System.Threading.CancellationToken)\"/>", false)]
    [InlineData("/* await r.EnsureFreshAsync(ct); */ var x = 1;", false)]
    [InlineData("var s = \"EnsureFreshAsync(\"; var v = @\"EnsureFreshOnMismatchAsync(\";", false)]
    [InlineData("await r.NotEnsureFreshAsync(ct);", false)]
    [InlineData("await r.EnsureFreshAsync(ct);", true)]
    [InlineData("await r.EnsureFreshAsync (ct);", true)]
    [InlineData("await LookupTargetMatcher.EnsureFreshOnMismatchAsync(r, ct);", true)]
    [InlineData("var e = await LookupTargetEntries\n    .ListAsync(r, ct);", true)]
    [InlineData("// x\nawait r.EnsureFreshAsync(ct); // y", true)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_свіжості_бачить_лише_виклик_у_коді(string source, bool expected)
        => Assert.Equal(expected, CallsFreshness(source));

    private static bool CallsFreshness(string text)
    {
        var code = CodeOnly(text);
        return FreshCall().IsMatch(code)
               || FreshOnMismatchCall().IsMatch(code)
               || HelperCall().IsMatch(code);
    }

    /// <summary>Код без коментарів (<c>//</c>, <c>///</c>, <c>/* */</c>) і рядкових/символьних літералів; переноси рядків лишаються.</summary>
    private static string CodeOnly(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        sb.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(text.Length, i + 2);
            }
            else if (c == '"' || ((c == '@' || c == '$') && (next == '"' || (i + 2 < text.Length && text[i + 2] == '"' && (next == '@' || next == '$')))))
            {
                // Рядок: `"..."`, `@"..."` (подвоєні лапки), `$"..."`, `$@"..."`; сирі `"""` читаються як порожній + решта (достатньо для сторожа).
                var verbatim = false;
                while (text[i] != '"')
                {
                    verbatim |= text[i] == '@';
                    i++;
                }

                i++;
                while (i < text.Length)
                {
                    if (!verbatim && text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    if (text[i] == '\n')
                    {
                        sb.Append('\n');
                    }

                    i++;
                }

                i++;
            }
            else if (c == '\'' && next != '\0')
            {
                // Символьний літерал `'x'` / `'\n'`; апостроф у решті коду трапляється лише так.
                i += next == '\\' ? 4 : 3;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\bEnsureFreshAsync\s*\(")]
    private static partial Regex FreshCall();

    [GeneratedRegex(@"\bEnsureFreshOnMismatchAsync\s*\(")]
    private static partial Regex FreshOnMismatchCall();

    [GeneratedRegex(@"\b(?:LookupTargetEntries\s*\.\s*ListAsync|SourceEventMapSupport\s*\.\s*BuildSpecsAsync)\s*\(")]
    private static partial Regex HelperCall();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порт_резолвера_груп_має_одну_реалізацію()
    {
        var implementations = SourceTree.Production()
            .Where(f => ImplementsPort().IsMatch(WithoutLineComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["src/Ecr.Application/Permits/Groups/PermitGroupResolver.cs"], implementations);
    }

    private static string WithoutLineComments(string text)
        => LineComment().Replace(text, string.Empty);

    [GeneratedRegex(@"""PRMG_")]
    private static partial Regex PrefixLiteral();

    [GeneratedRegex(@"\b(IPermitGroupResolver|ILookupTargetResolver)\b")]
    private static partial Regex ResolverMention();

    [GeneratedRegex(@"class\s+\w+[^{;=]*:\s*[^{;]*\b(IPermitGroupResolver|ILookupTargetResolver)\b")]
    private static partial Regex ImplementsPort();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
