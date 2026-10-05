using System.Text.RegularExpressions;
using Ecr.Application.Validation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Шаблони повідомлень валідації в коді (<see cref="ValidationMessageTemplates"/>) і в каталозі
/// <c>sys_ecr.UiString</c> (секція <c>COLL:an42vm</c> у <c>09-seed.sql</c>) не розходяться (T2-07).
/// </summary>
/// <remarks>
/// ⛔ Текст при створенні повідомлення береться з коду, на читанні — з каталогу: розбіжність дала б
/// користувачу один текст у відповіді на «Перевірити» і інший у збереженому результаті.
/// </remarks>
public sealed partial class ValidationTemplateSeedTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "T2-07")]
    public void Кожен_шаблон_коду_є_в_сіді_всіма_трьома_мовами_з_тим_самим_текстом()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var seed = new Dictionary<(string Key, string Lang), string>();
        foreach (Match row in SeedRow().Matches(text))
        {
            seed[(row.Groups[1].Value, row.Groups[2].Value)] = row.Groups[3].Value.Replace("''", "'", StringComparison.Ordinal);
        }

        // ⛔ Регулярка, що перестала збігатися, дала б порожній перелік і зелене.
        Assert.NotEmpty(seed);
        Assert.NotEmpty(ValidationMessageTemplates.Keys);

        var problems = new List<string>();
        foreach (var key in ValidationMessageTemplates.Keys)
        {
            foreach (var lang in new[] { "en", "ru", "kz" })
            {
                var code = ValidationMessageTemplates.Template(key, lang);
                if (!seed.TryGetValue((key, lang), out var fromSeed))
                {
                    problems.Add($"  {key} ({lang}): рядка немає в секції COLL:an42vm.");
                }
                else if (!string.Equals(code, fromSeed, StringComparison.Ordinal))
                {
                    problems.Add($"  {key} ({lang}): у коді «{code}», у сіді «{fromSeed}».");
                }
            }
        }

        // І навпаки: ключ `validation.*` у сіді без шаблону в коді — забутий запасний текст.
        foreach (var (key, _) in seed.Keys.Where(k => !ValidationMessageTemplates.Keys.Contains(k.Key)))
        {
            problems.Add($"  {key}: є в сіді, але немає в ValidationMessageTemplates.");
        }

        Assert.True(problems.Count == 0, "Шаблони розійшлися:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct()));
    }

    [GeneratedRegex(@"\(\s*N'(validation\.[A-Za-z.]+)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*(?:,\s*[01]\s*)?\)")]
    private static partial Regex SeedRow();
}
