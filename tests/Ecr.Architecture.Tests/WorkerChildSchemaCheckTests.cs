using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R6-X4/X4-02: дочірній воркер черги (<c>Ecr.Worker --child</c>) звіряє міграції бази зі збіркою
/// ДО <c>host.RunAsync</c> (першої задачі) і з розбіжністю виходить кодом
/// <c>ExitSchemaIncompatible</c>, не беручи задач. Сама звірка — <c>SchemaValidator.MigrationMismatchAsync</c>
/// (інтеграційний тест у <c>SchemaValidatorTests</c>).
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class WorkerChildSchemaCheckTests
{
    /// <remarks>
    /// Мутації: прибрати виклик <c>CheckSchemaAsync</c> або перенести його після <c>host.RunAsync</c> → червоний;
    /// прибрати <c>return ExitSchemaIncompatible</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Дочірній_звіряє_схему_до_першої_задачі()
    {
        var source = File.ReadAllText(Path.Combine(SourceTree.Root, "src", "Ecr.Worker", "WorkerProgram.cs"));

        var child = source.IndexOf("private static async Task<int> RunChildAsync(", StringComparison.Ordinal);
        Assert.True(child > 0, "RunChildAsync не знайдено");
        var end = source.IndexOf("private static async Task<string?> CheckSchemaAsync(", child, StringComparison.Ordinal);
        Assert.True(end > child, "CheckSchemaAsync не знайдено після RunChildAsync");
        var body = source[child..end];

        var build = body.IndexOf("using var host = builder.Build();", StringComparison.Ordinal);
        var check = body.IndexOf("await CheckSchemaAsync(host.Services", StringComparison.Ordinal);
        var refuse = body.IndexOf("return ExitSchemaIncompatible;", StringComparison.Ordinal);
        var run = body.IndexOf("await host.RunAsync(", StringComparison.Ordinal);

        Assert.True(build > 0 && check > build, "звірки схеми немає після Build()");
        Assert.True(refuse > check && run > refuse, "звірка схеми не стоїть ДО host.RunAsync з відмовою");

        var helper = source[end..];
        Assert.Contains("SchemaValidator.MigrationMismatchAsync(", helper, StringComparison.Ordinal);
        Assert.Contains("catch (DbException", helper, StringComparison.Ordinal);
    }
}
