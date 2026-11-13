using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B69 (P1, слово людини: «заархівовані дані не мають змінюватися; від SQL-адміна ніщо не захищає»): у
/// <c>arc.*</c> пишуть ЛИШЕ процедури архівації — застосунок не має жодного іншого шляху запису в архів.
/// </summary>
/// <remarks>
/// ⛔ Захист від адміністратора SQL — не задача застосунку; задача застосунку — не мати ВЛАСНОГО шляху запису в
/// архів, крім <see cref="AllowedProcedures"/>. Сторож сканує <c>src/**/*.cs</c> і <c>src/**/*.sql</c> (без міграцій
/// EF): <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c>/<c>TRUNCATE TABLE</c>/<c>SELECT … INTO</c>/
/// <c>ALTER TABLE … SWITCH … TO</c> із ціллю <c>arc.</c> чи <c>[arc].</c>.
/// <para>
/// ⚠ Коментарі вирізаються ДО пошуку (SQL: <c>--</c> і вкладені <c>/* */</c>; C#: <c>//</c> і <c>/* */</c>), а
/// рядкові літерали лишаються: C# несе свій SQL саме в рядках. Дозвіл — на ТІЛО процедури: SQL ділиться на пакети
/// за <c>GO</c>, ім'я береться з <c>CREATE [OR ALTER] PROC[EDURE] arc.…</c> цього пакета. У <c>.cs</c> дозволу
/// немає: застосунок лише кличе <c>EXEC arc.usp_…</c>.
/// </para>
/// <para>
/// ⚠ Межа сторожа (свідома): <c>UPDATE alias SET … FROM arc.X alias</c> і ціль у змінній динамічного SQL
/// (<c>SWITCH … TO ' + @dst</c>) регулярним виразом не впізнаються. Друга форма є лише в
/// <c>arc.usp_ArchiveAudit</c> (дозволена); першої в коді немає.
/// </para>
/// Доказ червоного: рядок <c>"UPDATE arc.CellValue SET Value = 0"</c> у будь-якому <c>.cs</c> під <c>src</c> →
/// перший тест падає з «файл:рядок» і переліком дозволених процедур (те саме доводить
/// <see cref="Сторож_бачить_запис_в_архів_і_не_бачить_коментар_чи_читання"/> без правки дерева).
/// </remarks>
public sealed partial class ArchiveWriteGuardTests
{
    /// <summary>Процедури, яким дозволено писати в <c>arc.*</c> (архівація, відновлення, аудит, межі партицій).</summary>
    public static readonly IReadOnlySet<string> AllowedProcedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "usp_ArchiveYear",
        "usp_RestoreYear",
        "usp_ArchiveAudit",
        "usp_EnsurePartitions",
        "usp_EnsureAuditPartitions",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_arc_пишуть_лише_дозволені_процедури_архівації()
    {
        var root = SourceTree.Root;
        var src = Path.Combine(root, "src");

        var scannedSql = 0;
        var allowedHits = 0;
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal)
                || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var isSql = path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
            if (!isSql && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isSql)
            {
                scannedSql++;
            }

            var scan = Scan(File.ReadAllText(path), isSql);
            allowedHits += scan.AllowedHits;
            offenders.AddRange(scan.Offenders.Select(o => $"{relative}:{o}"));
        }

        // ⚠ Порожній знаменник доводить не відсутність порушень, а відсутність перевірки.
        Assert.True(scannedSql >= 10, $"Знайдено лише {scannedSql} .sql під src — шлях до скриптів змінився?");

        // Сторож мусить бачити ВІДОМІ записи в архів (usp_ArchiveYear/usp_RestoreYear): інакше регулярний вираз
        // зламано й зелений результат нічого не доводить.
        Assert.True(allowedHits >= 10, $"У дозволених процедурах знайдено лише {allowedHits} записів у arc.* — сторож сліпий?");

        Assert.True(
            offenders.Count == 0,
            "Запис у arc.* поза процедурами архівації (B69: архівні дані не змінюються застосунком). Дозволено лише "
            + string.Join(", ", AllowedProcedures.Order(StringComparer.Ordinal).Select(p => "arc." + p))
            + ":" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Модель_EF_не_мапить_жодної_сутності_на_схему_arc()
    {
        using var db = Context();

        var entities = db.Model.GetEntityTypes().ToList();
        Assert.True(entities.Count >= 20, $"у моделі лише {entities.Count} сутностей — модель не зібралася?");

        var mapped = entities
            .Where(e => string.Equals(e.GetSchema(), "arc", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.GetViewSchema(), "arc", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.ClrType.Name} → {e.GetSchema() ?? e.GetViewSchema()}.{e.GetTableName() ?? e.GetViewName()}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            mapped.Count == 0,
            "Сутність EF на схемі arc дала б застосунку SaveChanges в архів (B69); архів читається лише сирим SQL: "
            + string.Join(", ", mapped));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("var sql = \"UPDATE arc.CellValue SET Value = 0\";", false, 1)]
    [InlineData("var sql = $\"DELETE FROM [arc].[TableRow] WHERE Id = {id}\";", false, 1)]
    [InlineData("var sql = @\"INSERT arc.CellValue (Id) VALUES (1)\";", false, 1)]
    [InlineData("var sql = \"merge into arc.CellStyle as t using x\";", false, 1)]
    [InlineData("var sql = \"SELECT * FROM arc.CellValue WHERE Id = 1\";", false, 0)]
    [InlineData("// UPDATE arc.CellValue SET Value = 0", false, 0)]
    [InlineData("/* DELETE FROM arc.TableRow */ var x = 1;", false, 0)]
    [InlineData("var c = '\"'; var sql = \"TRUNCATE TABLE arc.CellValue\";", false, 1)]
    [InlineData("SELECT Id INTO arc.Copy FROM doc.CellValue;", true, 1)]
    [InlineData("ALTER TABLE aud.CellChange SWITCH PARTITION 3 TO arc.AuditCellChange PARTITION 3;", true, 1)]
    [InlineData("-- DELETE FROM arc.CellValue", true, 0)]
    [InlineData("/* outer /* inner */ UPDATE arc.CellValue SET x = 1 */ SELECT 1;", true, 0)]
    [InlineData("SELECT N'it''s -- not a comment', 1; UPDATE arc.TableRow SET x = 1;", true, 1)]
    [InlineData("CREATE OR ALTER PROCEDURE arc.usp_Evil AS BEGIN DELETE FROM arc.CellValue; END;\nGO", true, 1)]
    [InlineData("CREATE OR ALTER PROCEDURE arc.usp_ArchiveYear AS BEGIN DELETE FROM arc.CellValue; END;\nGO\nUPDATE arc.CellValue SET x = 1;", true, 1)]
    [InlineData("CREATE PROC [arc].[usp_RestoreYear] AS DELETE FROM arc.CellValue;\nGO", true, 0)]
    public void Сторож_бачить_запис_в_архів_і_не_бачить_коментар_чи_читання(string text, bool isSql, int expected)
    {
        // Доказ, що сторож не зелений «сам собою»: той самий розбір, що й у скануванні дерева.
        Assert.Equal(expected, Scan(text, isSql).Offenders.Count);
    }

    /// <summary>Результат розбору одного файла.</summary>
    /// <param name="Offenders">«рядок: фрагмент» записів поза дозволеними процедурами.</param>
    /// <param name="AllowedHits">Записи всередині дозволених процедур.</param>
    private sealed record ScanResult(IReadOnlyList<string> Offenders, int AllowedHits);

    private static ScanResult Scan(string text, bool isSql)
    {
        var code = isSql ? StripSqlComments(text) : StripCsComments(text);
        var lineStarts = LineStarts(code);

        // Межі пакетів SQL (рядки `GO`); у C# пакетів немає — увесь файл один «пакет» без процедури.
        var batchStarts = new List<int> { 0 };
        if (isSql)
        {
            batchStarts.AddRange(GoRegex().Matches(code).Select(m => m.Index + m.Length));
        }

        var offenders = new List<string>();
        var allowed = 0;

        foreach (Match match in WriteRegex().Matches(code))
        {
            if (isSql && IsInsideAllowedProcedure(code, batchStarts, match.Index))
            {
                allowed++;
                continue;
            }

            var line = LineOf(lineStarts, match.Index);
            var snippet = string.Join(' ', match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            offenders.Add(string.Create(CultureInfo.InvariantCulture, $"{line}: {snippet}"));
        }

        return new ScanResult(offenders, allowed);
    }

    private static bool IsInsideAllowedProcedure(string code, List<int> batchStarts, int index)
    {
        var start = batchStarts.Where(s => s <= index).Max();
        var next = batchStarts.Where(s => s > index).DefaultIfEmpty(code.Length).Min();
        var batch = code[start..next];

        var proc = ProcedureRegex().Match(batch);
        return proc.Success && AllowedProcedures.Contains(proc.Groups["name"].Value);
    }

    private static List<int> LineStarts(string code)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> lineStarts, int index)
    {
        var line = lineStarts.BinarySearch(index);
        return line >= 0 ? line + 1 : ~line;
    }

    /// <summary>Вирізає коментарі T-SQL (<c>--</c>, вкладені <c>/* */</c>), лишаючи рядкові літерали й переводи рядків.</summary>
    private static string StripSqlComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var depth = 0;
        var inString = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (depth > 0)
            {
                if (c == '/' && next == '*')
                {
                    depth++;
                    sb.Append("  ");
                    i += 2;
                }
                else if (c == '*' && next == '/')
                {
                    depth--;
                    sb.Append("  ");
                    i += 2;
                }
                else
                {
                    sb.Append(c == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (inString)
            {
                sb.Append(c);
                if (c == '\'' && next == '\'')
                {
                    sb.Append(next);
                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (c == '\'')
            {
                inString = true;
                sb.Append(c);
                i++;
            }
            else if (c == '-' && next == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                depth = 1;
                sb.Append("  ");
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Вирізає коментарі C# (<c>//</c>, <c>/* */</c>), лишаючи рядки (звичайні, <c>@"…"</c>, сирі <c>"""…"""</c>) і
    /// символьні літерали.
    /// </summary>
    private static string StripCsComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                for (; i < stop; i++)
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                }

                continue;
            }

            if (c == '\'')
            {
                // Символьний літерал: '"' інакше відкрив би рядок.
                sb.Append(c);
                i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i]);
                        i++;
                    }

                    sb.Append(text[i]);
                    i++;
                }

                if (i < text.Length)
                {
                    sb.Append(text[i]);
                    i++;
                }

                continue;
            }

            if (c != '"')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var quotes = 0;
            while (i + quotes < text.Length && text[i + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3)
            {
                // Сирий рядок: до такої самої кількості лапок.
                var fence = new string('"', quotes);
                var end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + quotes;
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }

            var back = i - 1;
            while (back >= 0 && text[back] == '$')
            {
                back--;
            }

            var verbatim = (back >= 0 && text[back] == '@') || (i >= 2 && text[i - 1] == '$' && text[i - 2] == '@');
            sb.Append(c);
            i++;

            while (i < text.Length)
            {
                var s = text[i];
                if (verbatim)
                {
                    if (s == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append("\"\"");
                        i += 2;
                        continue;
                    }
                }
                else if (s == '\\' && i + 1 < text.Length)
                {
                    sb.Append(s).Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                else if (s == '\n')
                {
                    break;
                }

                sb.Append(s);
                i++;
                if (s == '"')
                {
                    break;
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Контекст без бази: модель будується, з'єднання не відкривається.</summary>
    private static EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

    /// <summary>Ціль <c>arc.</c> / <c>[arc].</c>.</summary>
    private const string Arc = @"\[?arc\]?\s*\.";

    /// <summary>Записи в архів: усі форми з B69.</summary>
    private const string Write =
        @"\bINTO\s+" + Arc                                 // INSERT INTO, MERGE INTO, SELECT … INTO
        + @"|\bINSERT\s+" + Arc
        + @"|\bUPDATE\s+" + Arc
        + @"|\bDELETE\s+(?:FROM\s+)?" + Arc
        + @"|\bDELETE\s+\w+\s+FROM\s+" + Arc
        + @"|\bMERGE\s+" + Arc
        + @"|\bTRUNCATE\s+TABLE\s+" + Arc
        + @"|\bSWITCH\s+(?:PARTITION\s+[^;]{0,200}?)?\bTO\s+" + Arc;

    [GeneratedRegex(Write, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteRegex();

    [GeneratedRegex(@"^[ \t]*GO\b[^\n]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoRegex();

    [GeneratedRegex(
        @"\b(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+PROC(?:EDURE)?\s+\[?arc\]?\s*\.\s*\[?(?<name>\w+)\]?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcedureRegex();
}
