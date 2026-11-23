// tests/Ecr.Architecture.Tests/SourceBindingConsumerTableReadGuardTests.cs
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B179 L6 → L7 (D-574): ручну таблицю споживачів зв'язки (<c>cfg.DocumentSourceBindingConsumer</c>) знято — споживачів
/// дає лише граф залежностей (<c>IDependencyGraphProvider</c>). Жоден продуктовий файл (C# і SQL, крім історії
/// міграцій EF) не називає таблицю, і модель EF її не відображає.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ L6 мав дозволені «визначення» (сутність, конфігурація, <c>DbSet</c>), і саме крізь навігацію сутності
/// (<c>b.Consumers</c>) лишились два читачі, яких сито імені таблиці не бачило: нічний розклад
/// (<c>RecalculationJob.NightlySourcesOfAsync</c>) і дотик споживачів перед видаленням джерела
/// (<c>DocumentDeletionStore.TouchSourceConsumersAsync</c>). L7 переводить їх на граф і прибирає визначення — тепер
/// повернення будь-якого читання не компілюється, а сторож тримає ім'я таблиці поза кодом і сідом.
/// </para>
/// <para>
/// Мутації (локально, B179 L7): повернути рядок <c>INSERT INTO cfg.DocumentSourceBindingConsumer</c> у
/// <c>seed/99-epilogue.sql</c> → червоний <see cref="Сід_і_SQL_скрипти_не_називають_знятої_таблиці"/>; повернути
/// <c>DocumentSourceBindingConsumerConfiguration</c> → червоні <see cref="Модель_EF_не_відображає_знятої_таблиці"/> і
/// <see cref="Продуктовий_C_sharp_не_називає_знятої_таблиці"/>.
/// </para>
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Architecture)]
[Trait(TestCategories.Check, TestCategories.Static)]
[Trait("Directive", "B179")]
public sealed partial class SourceBindingConsumerTableReadGuardTests
{
    /// <summary>Міграція, що знімає таблицю (єдина поза історією, кому ім'я потрібне).</summary>
    private const string DropMigrationName = "B179L7DropSourceBindingConsumer";

    [Fact]
    public void Продуктовий_C_sharp_не_називає_знятої_таблиці()
    {
        var files = SourceTree.Production().ToList();
        Assert.Contains(files, f => f.Path == "src/Ecr.Domain/Entities/Configuration/DocumentSourceBinding.cs");

        var readers = files
            .Where(f => !f.Path.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(f => ConsumerTable().IsMatch(WithoutComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            readers.Count == 0,
            "Код називає зняту cfg.DocumentSourceBindingConsumer — споживачів дає IDependencyGraphProvider (B179 L7, D-574):"
            + Environment.NewLine + string.Join(Environment.NewLine, readers));
    }

    [Fact]
    public void Сід_і_SQL_скрипти_не_називають_знятої_таблиці()
    {
        var sqlRoot = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");
        var scripts = Directory.EnumerateFiles(sqlRoot, "*.sql", SearchOption.AllDirectories).ToList();
        Assert.Contains(scripts, p => p.EndsWith("99-epilogue.sql", StringComparison.Ordinal));

        var writers = scripts
            .Where(p => ConsumerTable().IsMatch(SqlLineComment().Replace(BlockComment().Replace(File.ReadAllText(p), " "), " ")))
            .Select(p => Path.GetRelativePath(SourceTree.Root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            writers.Count == 0,
            "SQL-скрипт пише/читає зняту cfg.DocumentSourceBindingConsumer (B179 L7, D-574):"
            + Environment.NewLine + string.Join(Environment.NewLine, writers));
    }

    [Fact]
    public void Модель_EF_не_відображає_знятої_таблиці()
    {
        using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

        var tables = db.Model.GetEntityTypes()
            .Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .ToList();

        // Не порожня перевірка: сама зв'язка в моделі лишається.
        Assert.Contains("cfg.DocumentSourceBinding", tables);
        Assert.DoesNotContain("cfg.DocumentSourceBindingConsumer", tables);
    }

    [Fact]
    public void Знімає_таблицю_рівно_одна_міграція_L7()
    {
        var migrations = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Migrations");
        var drops = Directory.EnumerateFiles(migrations, "*.cs")
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("DROP TABLE cfg.DocumentSourceBindingConsumer", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        var drop = Assert.Single(drops);
        Assert.EndsWith("_" + DropMigrationName, drop, StringComparison.Ordinal);
    }

    /// <summary>Сито бачить DbSet, сутність і сирий SQL, але не коментарі.</summary>
    [Theory]
    [InlineData("from c in db.DocumentSourceBindingConsumers.AsNoTracking()", true)]
    [InlineData("var x = new DocumentSourceBindingConsumer(1);", true)]
    [InlineData("\"SELECT TemplateId FROM cfg.DocumentSourceBindingConsumer\"", true)]
    [InlineData("// db.DocumentSourceBindingConsumers — лише визначення", false)]
    [InlineData("/* DocumentSourceBindingConsumer */ var y = 1;", false)]
    [InlineData("var binding = db.DocumentSourceBindings.AsNoTracking();", false)]
    public void Сито_впізнає_читання_таблиці_споживачів(string code, bool expected)
        => Assert.Equal(expected, ConsumerTable().IsMatch(WithoutComments(code)));

    /// <summary>Сито SQL знімає коментарі <c>--</c> (ремарка про зняття в сіді лишається дозволеною).</summary>
    [Theory]
    [InlineData("INSERT INTO cfg.DocumentSourceBindingConsumer (BindingId) VALUES (1);", true)]
    [InlineData("-- ✎ B179 L7: cfg.DocumentSourceBindingConsumer знято", false)]
    [InlineData("SELECT 1 FROM cfg.DocumentSourceBinding;", false)]
    public void Сито_SQL_впізнає_звернення_до_таблиці(string sql, bool expected)
        => Assert.Equal(expected, ConsumerTable().IsMatch(SqlLineComment().Replace(BlockComment().Replace(sql, " "), " ")));

    private static string WithoutComments(string text)
        => LineComment().Replace(BlockComment().Replace(text, " "), " ");

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"--[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlLineComment();

    [GeneratedRegex(@"\bDocumentSourceBindingConsumers?\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ConsumerTable();
}
