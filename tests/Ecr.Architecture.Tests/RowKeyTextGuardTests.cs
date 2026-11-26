using System.Text.RegularExpressions;
using Ecr.Application.Localization;
using Ecr.Application.Validation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// integ-row-label-messages (live doc 100: «Row adc95dc2bdb84fa0bce2d80e0defc581 (B12 diesel generator DG-1)»): ключ рядка,
/// доданого людиною, — GUID без дефісів; у текст для людини (повідомлення помилок, валідації, журналу) він не потрапляє.
/// </summary>
/// <remarks>
/// Сторож у чотирьох частинах: (1) ВИРОБНИКИ — підстановка/подробиця, у значенні якої є ключ рядка, може називатися лише
/// так, як її знає <see cref="RowKeyText.Humanize"/> (<c>rowKey</c>, <c>rowKeys</c>, <c>sourceRow</c>, <c>row</c>), або бути в
/// переліку відомих винятків (ключі шаблону, службові поля); (2) ШЛЯХИ — три місця, де підстановки стають текстом,
/// викликають <c>RowKeyText.Humanize</c>; (3) ТЕКСТИ — кожен шаблон каталогу, що бере такі підстановки, у трьох мовах
/// з GUID у значенні віддає текст без 32-hex; (4) сама заміна.
/// ⛔ Мутація-доказ: повернути сирий ключ (прибрати <c>Humanize</c> з <c>ValidationMessageTemplates.Render</c>, або
/// покласти <c>RowKey</c> у підстановку з іншим ім'ям) — тест червоніє.
/// </remarks>
public sealed partial class RowKeyTextGuardTests
{
    private const string Guid32 = "adc95dc2bdb84fa0bce2d80e0defc581";

    /// <summary>Підстановки, чиє значення може бути ключем рядка, але ключ там авторський (шаблон) або службовий.</summary>
    private static readonly HashSet<string> KnownNonHumanFacing = new(StringComparer.Ordinal)
    {
        "messageKey",      // ключ каталогу, не ключ рядка
        "parentRowKey",    // шаблон: код батьківського рядка, який написав автор шаблону
        "targetRowKey",    // мапінг поля: код рядка шаблону
        "clientRowId",     // реєстри: ідентифікатор рядка пакета, який дав клієнт
        "deniedCount", "rowCount", // кількості
        "RowDef", "RowKey", // властивості DTO/записів, не підстановки
        "from", "to",      // межі діапазону формули: ключі рядків шаблону, написані автором формули
        "rowId", "TableRowId", // числові Id рядка в базі (діагностика узгодженості, мапа колонок)
    };

    private static string SrcDirectory()
        => Path.GetFullPath(Path.Combine(SeedSql.SeedDirectory(), "..", "..", "..", ".."));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підстановка_з_ключем_рядка_називається_так_як_знає_Humanize()
    {
        var known = new HashSet<string>(RowKeyText.KeyParameters, StringComparer.Ordinal);
        var problems = new List<string>();
        var scanned = 0;
        var sep = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(SrcDirectory(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                              && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                              && !f.Contains($"{sep}Migrations{sep}", StringComparison.Ordinal)))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in DictionaryEntry().Matches(lines[i]))
                {
                    var name = m.Groups["name"].Value;
                    if (!RowKeyValue().IsMatch(m.Groups["value"].Value) || known.Contains(name) || KnownNonHumanFacing.Contains(name))
                    {
                        continue;
                    }

                    problems.Add($"  {Path.GetRelativePath(SrcDirectory(), file)}:{i + 1}: підстановка «{name}» несе ключ рядка");
                }
            }
        }

        Assert.True(scanned > 100, "Сканер не знайшов вихідників.");
        Assert.True(
            problems.Count == 0,
            "Ключ рядка (GUID) у підстановці, яку RowKeyText.Humanize не знає, дійшов би до людини:\n" + string.Join('\n', problems)
            + "\nНазвіть підстановку rowKey/rowKeys/sourceRow або дайте рядку назву/номер (row).");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Місця_де_підстановки_стають_текстом_викликають_Humanize()
    {
        var src = SrcDirectory();
        string[] paths =
        [
            Path.Combine("Ecr.Api", "Errors", "ExceptionHandlingMiddleware.cs"),
            Path.Combine("Ecr.Application", "Validation", "ValidationMessageTemplates.cs"),
            Path.Combine("Ecr.Application", "Integration", "JobProgressMessageResolver.cs"),
        ];

        foreach (var path in paths)
        {
            Assert.True(
                File.ReadAllText(Path.Combine(src, path)).Contains("RowKeyText.Humanize(", StringComparison.Ordinal),
                $"{path}: підстановки з ключем рядка мають проходити RowKeyText.Humanize.");
        }

        // Рендер повідомлення Check (вихідний текст у момент запуску).
        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            var text = ValidationMessageTemplates.Render(
                ValidationMessageTemplates.CheckMismatchRow,
                lang,
                new Dictionary<string, string>
                {
                    ["left"] = "A", ["leftValue"] = "1", ["sourceRow"] = Guid32, ["right"] = "B", ["rightValue"] = "2",
                    ["deviation"] = "1", ["allowed"] = "0", ["kind"] = "abs",
                });
            Assert.DoesNotMatch(Hex24(), text);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_шаблон_каталогу_з_ключем_рядка_у_трьох_мовах_віддає_текст_без_GUID()
    {
        var checkedTemplates = 0;
        foreach (Match row in SeedRow().Matches(SeedSql.Text))
        {
            var template = row.Groups[3].Value.Replace("''", "'", StringComparison.Ordinal);
            var used = RowKeyText.KeyParameters.Where(name => template.Contains("{" + name + "}", StringComparison.Ordinal)).ToList();
            if (used.Count == 0)
            {
                continue;
            }

            var parameters = used.ToDictionary(name => name, _ => $"{Guid32}, {Guid32}", StringComparer.Ordinal);
            var text = UiStringResolver.Format(template, RowKeyText.Humanize(parameters, row.Groups[2].Value));
            Assert.DoesNotMatch(Hex24(), text);
            checkedTemplates++;
        }

        // ⛔ Регулярка, що перестала збігатися, дала б нуль перевірених шаблонів і зелене.
        Assert.True(checkedTemplates >= 20, $"Перевірено лише {checkedTemplates} шаблонів з ключем рядка.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Humanize_замінює_ключі_у_переліку_і_лишає_короткі_коди()
    {
        var parameters = new Dictionary<string, string>
        {
            ["rowKeys"] = $"LTD-A01, {Guid32}, F_B12",
            ["columnCode"] = Guid32,
        };

        var result = RowKeyText.Humanize(parameters, "en");

        Assert.Equal("LTD-A01, (added by a user), F_B12", result["rowKeys"]);
        Assert.Equal(Guid32, result["columnCode"]);

        var codes = new Dictionary<string, string> { ["rowKey"] = "LTD-A01" };
        Assert.Same(codes, RowKeyText.Humanize(codes, "en"));
    }

    [GeneratedRegex(@"\[""(?<name>[A-Za-z]+)""\]\s*=\s*(?<value>[^;]*?)[,;]?\s*$|\(\s*""(?<name>[A-Za-z]+)""\s*,\s*(?<value>[^)]*)\)")]
    private static partial Regex DictionaryEntry();

    [GeneratedRegex(@"(?i)row(Key|Id)\b|SourceRowKey|\.RowKey")]
    private static partial Regex RowKeyValue();

    [GeneratedRegex("[0-9a-fA-F]{24,}")]
    private static partial Regex Hex24();

    [GeneratedRegex(@"\(\s*N'([A-Za-z0-9_.-]+)'\s*,\s*N'(en|ru|kz)'\s*,\s*N'((?:[^']|'')*)'\s*(?:,\s*[01]\s*)?\)")]
    private static partial Regex SeedRow();
}
