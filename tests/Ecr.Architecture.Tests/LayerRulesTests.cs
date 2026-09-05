using System.Reflection;
using Ecr.TestKit;
using NetArchTest.Rules;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Вісім архітектурних правил (tz/03 §3.3). Порушення кожного з них
/// виявляється не при написанні коду, а через місяці — коли межу вже
/// перетнули десятки разів.
/// </summary>
public sealed class LayerRulesTests
{
    private static readonly Assembly Domain = typeof(Ecr.Domain.Abstractions.IClock).Assembly;
    private static readonly Assembly Application = typeof(Ecr.Application.Ports.ICellStore).Assembly;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_1_домен_не_залежить_ні_від_чого()
    {
        // Домен — це знання про предметну область. Щойно в ньому з'являється
        // EF або HTTP, його неможливо ні прочитати, ні перевірити окремо.
        var referenced = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                           && !name.Equals("System", StringComparison.Ordinal)
                           && !name.Equals("netstandard", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(referenced);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_2_застосунок_не_знає_про_інфраструктуру_і_EF_Core()
    {
        // Саме ця межа робить use-cases перевірюваними без бази. Один
        // DbContext у обробнику — і половина тестів починає вимагати SQL Server.
        var referenced = Application.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referenced, n => n.Contains("EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => n.Contains("Ecr.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => n.Contains("Ecr.Adapters", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_3_ядро_не_знає_про_AF_Excel_і_екологію()
    {
        // ⚠ «Екологія» тут — конкретні методики й формули НКОК. Ядро має
        // лишатися системою структурованої звітності: інакше наступна
        // методика стає зміною ядра, а не даними.
        foreach (var assembly in new[] { Domain, Application })
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(referenced, n => n.Contains("OSIsoft", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(referenced, n => n.Contains("OpenXml", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(referenced, n => n.Contains("Adapters", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_4_DbContext_не_зустрічається_у_контролерах()
    {
        // Контролер із DbContext — це другий прикладний шар, у якому те саме
        // правило живе вдруге і розходиться з першим.
        var offenders = SourceTree.Production("Ecr.Api")
            .Where(f => f.Path.Contains("/Controllers/", StringComparison.Ordinal))
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => x.Text.Contains("EcrDbContext", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_5_немає_блокувальних_викликів_Result_і_Wait()
    {
        // ⚠ `.Result` під навантаженням дає вичерпання пулу потоків і
        // взаємоблокування, яке неможливо відтворити на тесті: воно виникає
        // саме тоді, коли запитів багато — тобто в останній день періоду.
        // ⚠ Межа слова обов'язкова: без неї правило ловить `ResultDiffJson`
        // і будь-яку властивість, чиє ім'я починається з Result. Перше ж
        // хибне спрацювання перетворює архітектурний тест на шум, який
        // вимикають.
        var blocking = new System.Text.RegularExpressions.Regex(
            @"\.Result\b|\.Wait\(\)|\.GetAwaiter\(\)\.GetResult\(\)");

        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => blocking.IsMatch(x.Text))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Правило_6_у_застосунку_немає_ToList_без_Take()
    {
        // ⚠ Правило про матеріалізацію З БАЗИ, а не про будь-який ToList.
        // `request.Rows.Where(...).ToList()` над списком у пам'яті нічого не
        // коштує; небезпечний саме `ToListAsync` над DbSet без межі — це
        // «віддати весь реєстр»: на тестових даних непомітно, на бойових
        // кладе і сервер, і клієнта.
        //
        // Перевіряються і Application, і Infrastructure: у першому запитів до
        // бази не має бути взагалі (правило 2), у другому вони й живуть.
        var offenders = SourceTree.Production("Ecr.Application", "Ecr.Infrastructure")
            .SelectMany(f => Statements(f).Select(st => (f.Path, st.Line, st.Text)))
            .Where(x => x.Text.Contains("ToListAsync(", StringComparison.Ordinal)
                        && x.Text.Contains("db.", StringComparison.Ordinal)
                        && !x.Text.Contains("Where(", StringComparison.Ordinal)
                        && !x.Text.Contains("Take(", StringComparison.Ordinal)
                        && !x.Text.Contains("SqlQueryRaw", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Правило_7_перевірка_ролей_робиться_лише_через_IAccessDecisionService()
    {
        // ⚠ `User.IsInRole` і `[Authorize(Roles=…)]` бачать лише ім'я ролі, а
        // доступ у ECR залежить від РЕСУРСУ: проєкту, аркуша, періоду
        // (ФВ-6.14). Перевірка за роллю дає дозвіл там, де його не має бути.
        var offenders = SourceTree.Production()
            .SelectMany(f => f.CodeLines().Select(l => (f.Path, l.Line, l.Text)))
            .Where(x => x.Text.Contains("IsInRole", StringComparison.Ordinal)
                        || x.Text.Contains("Authorize(Roles", StringComparison.Ordinal)
                        || x.Text.Contains("RequireRole", StringComparison.Ordinal))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Правило_8_у_результатних_типах_немає_float_і_double()
    {
        // D-30: порядок додавання float змінює результат, і звірка з еталоном
        // стає неможливою. У звітності, яку подають у держорган, це
        // неприпустимо.
        var offenders = Types.InAssemblies([Domain, Application])
            .That().ArePublic()
            .GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType == typeof(double)
                        || x.Property.PropertyType == typeof(float)
                        || x.Property.PropertyType == typeof(double?)
                        || x.Property.PropertyType == typeof(float?))
            .Select(x => $"{x.Type.FullName}.{x.Property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Оператори, склеєні з рядків: ланцюг LINQ переноситься на кілька рядків,
    /// і порядкова перевірка бачила б `.ToListAsync()` окремо від `.Where(...)`.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> Statements(SourceFile file)
    {
        var buffer = new System.Text.StringBuilder();
        var start = 0;

        foreach (var (line, text) in file.CodeLines())
        {
            if (buffer.Length == 0)
            {
                start = line;
            }

            buffer.Append(' ').Append(text);

            if (text.EndsWith(';') || text.EndsWith('{') || text.EndsWith('}'))
            {
                yield return (start, buffer.ToString());
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            yield return (start, buffer.ToString());
        }
    }
}
