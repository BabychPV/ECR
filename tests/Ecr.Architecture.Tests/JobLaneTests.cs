// tests/Ecr.Architecture.Tests/JobLaneTests.cs
using System.Reflection;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Лейни черги (MI-02, <c>D-208</c>) — фіксовані константи <see cref="JobLanes"/>.
/// </summary>
/// <remarks>
/// ⛔ Лейн, написаний літералом в іншому місці, розходиться з пулом виконавців
/// мовчки: задача з лейном «Recalc» чи «recalc » стоїть <c>Queued</c> вічно, бо
/// жоден воркер її лейн не опитує. Тому лейн пишуть лише як <c>JobLanes.X</c>.
///
/// ⚠ Сторож читає ТЕКСТ джерел, а текст крихкий: коментар чи XML-документація
/// з «recalc» не є порушенням. Тому перед пошуком коментарі вирізаються
/// лексером, а шукаються лише рядкові літерали — C#-літерал, рівний лейну, і
/// SQL-літерал <c>'лейн'</c> усередині рядка (сирий SQL черги, F1b).
/// </remarks>
public sealed class JobLaneTests
{
    /// <summary>
    /// Рядки, що ЗБІГАЮТЬСЯ з лейном, але лейном не є. Кожен — з причиною.
    /// </summary>
    private static readonly HashSet<(string File, string Literal)> NotALane = new()
    {
        // Джерело замовчування параметра звіту (`Value(…, "default")`), не лейн.
        ("src/Ecr.Application/Reporting/ReportParameters.cs", "default"),
    };

    private const string LanesFile = "src/Ecr.Application/Ports/IJobQueue.cs";

    [Fact]
    public void All_містить_рівно_оголошені_константи_без_повторів_у_межах_колонки()
    {
        var constants = typeof(JobLanes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(constants, JobLanes.All.OrderBy(v => v, StringComparer.Ordinal));
        Assert.Equal(JobLanes.All.Count, JobLanes.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(JobProgress.MaxLaneLength, JobLanes.MaxLength);

        // varchar(32), IsUnicode(false): лише ASCII нижнього регістру — інакше
        // порівняння в SQL (колація) і в коді (Ordinal) розійдуться.
        Assert.All(JobLanes.All, lane =>
        {
            Assert.InRange(lane.Length, 1, JobLanes.MaxLength);
            Assert.Matches("^[a-z][a-z0-9-]*$", lane);
            Assert.True(JobLanes.IsKnown(lane));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Recalc")]
    [InlineData("recalc ")]
    [InlineData("unknown")]
    public void IsKnown_відкидає_невідомий_лейн_і_інший_регістр(string? lane)
        => Assert.False(JobLanes.IsKnown(lane));

    [Fact]
    public void Лейн_літералом_поза_JobLanes_у_src_не_пишуть()
    {
        var root = SolutionRoot();
        var problems = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == LanesFile || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var literal in StringLiterals(File.ReadAllText(file)))
            {
                foreach (var lane in JobLanes.All)
                {
                    // Ordinal: «Default» — код політики паролів (UserStore), не лейн.
                    // Лейн в іншому регістрі відкидає вже JobLanes.IsKnown на постановці.
                    var csharp = string.Equals(literal, lane, StringComparison.Ordinal);
                    var sql = literal.Contains($"'{lane}'", StringComparison.Ordinal);

                    if ((csharp || sql) && !NotALane.Contains((relative, literal)))
                    {
                        problems.Add($"{relative}: \"{literal}\" — пиши JobLanes.*");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Api не бере лейн перерахунку, коли його виконує окремий пул (<c>D-206</c>),
    /// а виконавець черги й адаптер з'являються лише в режимі <c>Database</c> (F1c).
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється РЕЄСТРАЦІЯ контейнера (<c>AddEcrInfrastructure</c>), а не
    /// сам <see cref="JobLaneMap"/>: лейни, правильно пораховані й не передані
    /// воркеру, так само лишили б перерахунок в Api.
    /// Мутація: <c>Lanes = JobLanes.All</c> у DependencyInjection — перший рядок
    /// Theory червоний.
    /// </remarks>
    [Theory]
    [InlineData("Database", "Worker", true, false)]
    [InlineData("Database", "InProcess", true, true)]
    [InlineData("Database", null, true, true)]
    [InlineData("Quartz", "Worker", false, false)]
    [InlineData(null, null, false, false)]
    public void Воркер_Api_опитує_лейни_за_режимом_і_виконавцем_перерахунку(
        string? mode, string? executor, bool workerRegistered, bool claimsRecalc)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ecr"] = "Server=.;Database=EcrJobLaneGuard;Integrated Security=true",
            [DbBackgroundJobScheduler.ModeKey] = mode,
            [JobLaneMap.ExecutorKey] = executor,
        };

        var services = new ServiceCollection();
        services.AddEcrInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        var worker = services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobWorker));
        var dbScheduler = services.Any(d =>
            d.ServiceType == typeof(IBackgroundJobScheduler) && d.ImplementationType == typeof(DbBackgroundJobScheduler));

        Assert.Equal(workerRegistered, worker);
        Assert.Equal(workerRegistered, dbScheduler);
        Assert.Single(services, d => d.ServiceType == typeof(IBackgroundJobScheduler));
        Assert.Contains(services, d => d.ServiceType == typeof(IJobQueue));
        Assert.Contains(services, d => d.ServiceType == typeof(IJobLeaseContext));

        if (workerRegistered)
        {
            var lanes = services.Single(d => d.ServiceType == typeof(JobWorkerOptions))
                .ImplementationInstance is JobWorkerOptions options ? options.Lanes : [];

            Assert.Contains(JobLanes.Default, lanes);
            Assert.Equal(claimsRecalc, lanes.Contains(JobLanes.Recalc));
        }
    }

    [Fact]
    public void Лексер_бачить_літерали_і_не_бачить_коментарів()
    {
        // Самоперевірка сторожа: без неї зелений прогін нічого б не доводив —
        // лексер, що не знаходить жодного літерала, теж «зелений».
        const string source = """"
            // коментар "recalc"
            #region Прив'язки "recalc"
            /* блок "recalc" */
            /// <summary>"recalc"</summary>
            var url = "http://x/y"; var a = "recalc";
            var b = @"шлях ""default"" тут"; var c = 'x'; var d = '"';
            var e = $"{a}/x"; var sql = """
                WHERE Lane = 'recalc' -- не коментар C#
                """;
            """";

        Assert.Equal(
            ["http://x/y", "recalc", "шлях \"default\" тут", "{a}/x", "WHERE Lane = 'recalc' -- не коментар C#"],
            StringLiterals(source));
    }

    /// <summary>
    /// Вміст рядкових літералів C#: звичайних, verbatim, інтерпольованих і
    /// сирих (<c>"""</c>); коментарі й символьні літерали пропускаються.
    /// </summary>
    /// <remarks>
    /// ⚠ Навмисно спрощено: вкладений літерал у дірці інтерпольованого рядка
    /// звичайного виду (<c>$"{"x"}"</c>) розбивається на шматки. Помилка
    /// може лише ПРОПУСТИТИ такий лейн, а не вигадати порушення.
    /// </remarks>
    private static List<string> StringLiterals(string text)
    {
        var result = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if ((c == '/' && next == '/') || (c == '#' && AtLineStart(text, i)))
            {
                // Коментар або директива (`#region Прив'язки` — апостроф не літерал).
                i = text.IndexOf('\n', i) is var eol and >= 0 ? eol : text.Length;
            }
            else if (c == '/' && next == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length;
            }
            else if (c == '\'')
            {
                // Символьний літерал: '\'' і '"' не мусять відкривати рядок.
                var close = next == '\\' ? text.IndexOf('\'', i + 3) : i + 2;
                i = close < 0 ? text.Length : close + 1;
            }
            else if (c == '"' || ((c is '@' or '$') && (next == '"' || next is '@' or '$')))
            {
                var start = i;
                while (text[i] is '@' or '$')
                {
                    i++;
                }

                var verbatim = text.AsSpan(start, i - start).Contains('@');
                var quotes = 0;
                while (i + quotes < text.Length && text[i + quotes] == '"')
                {
                    quotes++;
                }

                if (quotes >= 3)
                {
                    var close = new string('"', quotes);
                    var bodyStart = i + quotes;
                    var end = text.IndexOf(close, bodyStart, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end;
                    result.Add(RawBody(text[bodyStart..end]));
                    i = Math.Min(text.Length, end + quotes);
                }
                else if (quotes == 2)
                {
                    result.Add(string.Empty);
                    i += 2;
                }
                else
                {
                    i++;
                    var body = new StringBuilder();
                    while (i < text.Length)
                    {
                        if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            body.Append('"');
                            i += 2;
                        }
                        else if (!verbatim && text[i] == '\\' && i + 1 < text.Length)
                        {
                            body.Append(text[i + 1] == '"' ? "\"" : text.Substring(i, 2));
                            i += 2;
                        }
                        else if (text[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            body.Append(text[i++]);
                        }
                    }

                    result.Add(body.ToString());
                }
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static bool AtLineStart(string text, int index)
    {
        for (var j = index - 1; j >= 0 && text[j] != '\n'; j--)
        {
            if (!char.IsWhiteSpace(text[j]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Тіло сирого рядка без відступу закривних лапок і крайових переносів.</summary>
    private static string RawBody(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 1)
        {
            return lines[0];
        }

        var inner = lines[1..^1];
        var indent = lines[^1].Length;
        return string.Join("\n", inner.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
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
