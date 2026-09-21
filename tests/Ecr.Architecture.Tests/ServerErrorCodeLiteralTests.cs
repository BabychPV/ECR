using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сервер не кидає кодів помилок повз каталог: кожен рядковий літерал
/// <c>"ECR-XXX-NNNN"</c> у <c>src/**.cs</c> мусить бути константою
/// <c>ErrorCodes.cs</c>.
/// </summary>
/// <remarks>
/// ⛔ Чому окремий сторож. <c>ECR-JOB-0409</c> кидався в семи місцях сирим
/// літералом і в каталозі не був ніколи. <c>ContractIntegrityTests</c> цього не
/// бачив (він звіряє кинуте з §7, а там код є), <c>ErrorTitleCatalogTests</c>
/// теж (шукає константи), а <c>ClientErrorCodeTests</c> червонив клієнтові
/// будь-яку згадку цього коду — тож клієнт не міг за ним розгалузитися і
/// обходив його заглушкою.
///
/// ⚠ Порівнюється ЦІЛИЙ літерал, а не підрядок: <c>"err.ECR-JOB-0409.x"</c> —
/// ключ повідомлення, не код. Коментарі (<c>//</c>, <c>///</c>, <c>/* */</c>)
/// знімаються лексером, який знає рядки, — інакше <c>"http://…"</c> обрізав би
/// рядок, а пояснення в XML-доці валило б сторожа.
/// </remarks>
public sealed partial class ServerErrorCodeLiteralTests
{
    private const string CatalogFile = "src/Ecr.Domain/Errors/ErrorCodes.cs";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сервер_не_кидає_кодів_яких_немає_в_каталозі()
    {
        var files = SourceTree.Production();
        var catalog = CatalogConstant()
            .Matches(files.Single(f => f.Path == CatalogFile).Text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(catalog);

        var offenders = files
            .Where(f => f.Path != CatalogFile)
            .SelectMany(f => StringLiterals(f.Text)
                .Where(s => Code().IsMatch(s) && !catalog.Contains(s))
                .Select(s => $"  {s} ({f.Path})"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Коди помилок літералом повз {CatalogFile}:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders) + Environment.NewLine
            + "Заведи константу в каталозі (плюс §7 02-contracts.md, заголовок err.<код> у 09-seed.sql, "
            + "арм у ExceptionHandlingMiddleware, якщо статус не 422) і посилайся на неї.");
    }

    /// <summary>
    /// ⛔ Самоперевірка лексера: без неї лексер, що перестав бачити рядки,
    /// дав би порожній перелік і зелене саме тоді, коли зламався.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Лексер_бачить_літерали_і_не_бачить_коментарів()
    {
        const string sample = """"
            /// <summary>Кидає <c>"ECR-DOCA-0001"</c>.</summary>
            // "ECR-DOCB-0002"
            /* "ECR-DOCC-0003" */
            var url = "http://host/x"; var a = "ECR-LIVE-0001";
            var b = @"say ""hi"""; var c = $"{x}ECR"; var d = '"';
            var e = "esc \" still"; var f = "ECR-LIVE-0002";
            var sql = """
                SELECT 1 -- "ECR-RAWA-0004"
                """; var g = "ECR-LIVE-0005";
            """";

        var codes = StringLiterals(sample)
            .Where(s => Code().IsMatch(s))
            .ToList();

        Assert.Equal(["ECR-LIVE-0001", "ECR-LIVE-0002", "ECR-LIVE-0005"], codes);
    }

    /// <summary>Вміст рядкових літералів C# поза коментарями.</summary>
    /// <param name="source">Текст файлу.</param>
    /// <remarks>
    /// Звичайні, <c>@</c>-, інтерпольовані й сирі (<c>"""</c>) рядки. Вкладені
    /// рядки в дірках інтерполяції не розбираються — код помилки там не живе.
    /// </remarks>
    private static IEnumerable<string> StringLiterals(string source)
    {
        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (ch == '/' && next == '/')
            {
                i = source.IndexOf('\n', i) is var nl and >= 0 ? nl : source.Length;
            }
            else if (ch == '/' && next == '*')
            {
                i = source.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : source.Length;
            }
            else if (ch == '\'')
            {
                // Символьний літерал: '"' не повинен відкривати рядок.
                i += next == '\\' ? 4 : 3;
            }
            else if (ch == '"')
            {
                var run = 0;
                while (i + run < source.Length && source[i + run] == '"')
                {
                    run++;
                }

                if (run >= 3)
                {
                    var close = source.IndexOf(new string('"', run), i + run, StringComparison.Ordinal);
                    var stop = close >= 0 ? close : source.Length;
                    yield return source[(i + run)..stop];
                    i = stop + run;
                }
                else
                {
                    var verbatim = i > 0 && (source[i - 1] == '@' || (i > 1 && source[i - 2] == '@'));
                    var text = new StringBuilder();
                    i++;
                    while (i < source.Length)
                    {
                        var c = source[i];
                        if (!verbatim && c == '\\' && i + 1 < source.Length)
                        {
                            text.Append(c).Append(source[i + 1]);
                            i += 2;
                        }
                        else if (c == '"' && verbatim && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            text.Append('"');
                            i += 2;
                        }
                        else if (c == '"' || (!verbatim && c == '\n'))
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            text.Append(c);
                            i++;
                        }
                    }

                    yield return text.ToString();
                }
            }
            else
            {
                i++;
            }
        }
    }

    [GeneratedRegex(@"public const string \w+\s*=\s*""(ECR-[A-Z]{3,4}-\d{4})""")]
    private static partial Regex CatalogConstant();

    [GeneratedRegex(@"^ECR-[A-Z]{3,4}-\d{4}$")]
    private static partial Regex Code();
}
