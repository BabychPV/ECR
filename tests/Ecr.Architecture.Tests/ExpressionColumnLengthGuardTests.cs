using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// AN-91 (аудит 2026-10-09, L10-01): колонка, що зберігає текст виразу, вміщує
/// <see cref="MethodologyFormula.MaxExpressionLength"/> (4000) — або її вужчу межу заявлено явно.
/// </summary>
/// <remarks>
/// ⛔ Чому: вираз формули методології може мати 4000 символів, а <c>calc.CalculationStep.Expression</c> була 2000 —
/// довший вираз валив <c>SaveChanges</c> усього прогону (L10-01); виправлено обрізанням у домені. Сторожа,
/// що лишає таку розбіжність видимою, не було: нова вужча колонка виразу мовчала б до першого довгого виразу.
/// <para>
/// Предмет — ПОБУДОВАНА модель (<c>EcrDbContext</c>, без бази) і текст <c>12-archive-tables.sql</c>. Правило:
/// string-колонка <c>Expression</c> із <c>MaxLength &lt; 4000</c> мусить стояти в <see cref="NarrowByDesign"/> —
/// з причиною й сталою довжини в сутності, що збігається з колонкою (стеля одна для мапінгу й коду).
/// </para>
/// Мутації (CI): <c>HasMaxLength(1000)</c> для <c>MethodologyFormula.Expression</c> → перший тест називає її;
/// змінити <c>MaxStepExpressionLength</c> без мапінгу → «стала ≠ колонка»; звузити <c>arc.CalculationStep.Expression</c>
/// до 1000 → другий тест червоний. Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class ExpressionColumnLengthGuardTests
{
    /// <summary>Вужчі за 4000 колонки виразу: сутність, властивість → (стала межі в сутності, чому це припустимо).</summary>
    private static readonly IReadOnlyDictionary<(string Entity, string Property), (string? Constant, string Reason)> NarrowByDesign =
        new Dictionary<(string, string), (string?, string)>
        {
            [("CalculationStep", "Expression")] = ("MaxStepExpressionLength",
                "трейс — довідка, а не джерело формули: CalculationStep.Describe обрізає вираз до межі з «…» (L10-01)"),
            [("FormulaDef", "Expression")] = ("MaxExpressionLength",
                "довжину відхиляє ExpressionRejection/ExpressionLengthGuard.MaxFor на збереженні (діалекти Template/Report — 2000)"),
            [("ValidationRule", "Expression")] = ("MaxExpressionLength",
                "довжину відхиляє ExpressionRejection на збереженні (ValidationRuleHandlers, межа ValidationRule.MaxExpressionLength)"),
            [("ConsistencyRule", "Expression")] = (null,
                "сутність без записувачів у src (є лише DbSet): вираз не потрапляє з методологій; обмеження довжини в домені немає"),
        };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонка_виразу_вміщує_4000_або_її_вужчу_межу_заявлено()
    {
        using var db = Context();

        var offenders = new List<string>();
        var wide = 0;
        var narrowSeen = new HashSet<(string, string)>();

        foreach (var entity in db.Model.GetEntityTypes().OrderBy(e => e.ClrType.Name, StringComparer.Ordinal))
        {
            foreach (var property in entity.GetDeclaredProperties()
                         .Where(p => p.ClrType == typeof(string) && IsExpressionColumn(p.Name))
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var name = $"{entity.ClrType.Name}.{property.Name}";
                var max = property.GetMaxLength();
                if (max is null || max >= MethodologyFormula.MaxExpressionLength)
                {
                    wide++;
                    continue;
                }

                var key = (entity.ClrType.Name, property.Name);
                narrowSeen.Add(key);
                if (!NarrowByDesign.TryGetValue(key, out var declared))
                {
                    offenders.Add($"{name}: nvarchar({max}) вужча за {MethodologyFormula.MaxExpressionLength} і не заявлена в NarrowByDesign — "
                        + "довший вираз валив би SaveChanges; розшир колонку (міграція) або заяви межу з причиною");
                    continue;
                }

                if (declared.Constant is null)
                {
                    continue;
                }

                var field = entity.ClrType.GetField(declared.Constant, BindingFlags.Public | BindingFlags.Static);
                if (field is null || !field.IsLiteral || field.GetRawConstantValue() is not int limit || limit != max)
                {
                    offenders.Add($"{name}: nvarchar({max}), а стала {entity.ClrType.Name}.{declared.Constant} відсутня або має інше значення — "
                        + "мапінг і код розійшлися");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

        // Порожня модель (чи зламаний IsExpressionColumn) дала б «жодного порушення» так само.
        Assert.True(wide >= 3, $"широких колонок виразу в моделі лише {wide} — модель не зібралася?");

        // Мертвий рядок переліку: колонку розширили чи прибрали, а заяву лишили.
        var stale = NarrowByDesign.Keys.Where(k => !narrowSeen.Contains(k)).Select(k => $"{k.Entity}.{k.Property}").ToList();
        Assert.True(stale.Count == 0, "NarrowByDesign містить колонки, яких уже немає серед вузьких: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Дзеркало архіву приймає все, що приймає колонка джерела: інакше архівація кроку трейсу довшого за дзеркало
    /// впала б на <c>INSERT … SELECT</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Дзеркало_архіву_кроку_трейсу_не_вужче_за_колонку_джерела()
    {
        var sql = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "12-archive-tables.sql"));

        var table = Regex.Match(sql, @"CREATE TABLE arc\.CalculationStep\s*\((?<body>.*?)\)\s*ON\s", RegexOptions.Singleline);
        Assert.True(table.Success, "у 12-archive-tables.sql немає CREATE TABLE arc.CalculationStep");

        var column = Regex.Match(table.Groups["body"].Value, @"\bExpression\s+nvarchar\((?<n>\d+|max)\)", RegexOptions.IgnoreCase);
        Assert.True(column.Success, "у arc.CalculationStep немає колонки Expression nvarchar(...)");

        var mirror = column.Groups["n"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)
            ? int.MaxValue
            : int.Parse(column.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            mirror >= CalculationStep.MaxStepExpressionLength,
            $"arc.CalculationStep.Expression — nvarchar({mirror}), вужча за calc.CalculationStep.Expression "
            + $"({CalculationStep.MaxStepExpressionLength})");
    }

    // «Expression» чи будь-яке ...Expression, окрім розкладу cron: той не формула.
    private static bool IsExpressionColumn(string name)
        => name.EndsWith("Expression", StringComparison.Ordinal)
           && !name.Equals("CronExpression", StringComparison.Ordinal);

    /// <summary>Контекст без бази: модель будується, з'єднання не відкривається.</summary>
    private static EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);
}
