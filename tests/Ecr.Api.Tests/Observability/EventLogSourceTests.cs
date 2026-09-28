// tests/Ecr.Api.Tests/Observability/EventLogSourceTests.cs

using System.Text.RegularExpressions;
using Ecr.Api.Observability;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests.Observability;

/// <summary>
/// Застосунок пише в журнал подій під тим самим джерелом, яке реєструє MSI і
/// на яке посилаються скрипт розгортання й runbook (<c>U15</c>, <c>R-03</c>).
/// </summary>
[Collection("SqlServer")]
public sealed partial class EventLogSourceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U15")]
    public void MSI_скрипт_і_runbook_називають_те_саме_джерело_що_й_застосунок()
    {
        var root = RepositoryRoot();

        // ⚠ Звірка з САМИМ елементом реєстрації, а не з будь-якою згадкою «ECR» у
        // файлі: це слово там на кожному кроці (служба, тека, продукт).
        var wix = File.ReadAllText(Path.Combine(root, "installer", "Ecr.Installer", "Service.wxs"));
        var registered = EventSourceName().Matches(wix).Select(m => m.Groups["name"].Value).ToList();
        Assert.Equal([EventLogSource.Name], registered);

        var deploy = File.ReadAllText(Path.Combine(root, "tools", "deploy-ecr.ps1"));
        Assert.Contains($"Event Log (джерело {EventLogSource.Name})", deploy, StringComparison.Ordinal);

        var runbook = File.ReadAllText(Path.Combine(root, "docs", "admin", "operations-runbook.md"));
        Assert.Contains($"джерело **`{EventLogSource.Name}`**", runbook, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "U15")]
    public void Постачальник_журналу_подій_застосунку_пише_під_джерелом_MSI()
    {
        // ⚠ Лише Windows: журналу подій (і EventLogSettings) поза нею немає, і
        // `AddEcrEventLogSource` там свідомо нічого не реєструє. На Linux-CI
        // тест проходить порожньо — звірку імен тримає попередній тест.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var app = new EcrApiFactory(sql);
        _ = app.Services; // піднімає справжній Program.cs

        // ⛔ Без виклику в Program.cs тут був би null (поза службою
        // `UseWindowsService()` ім'я не ставить), а в службі — `Ecr.Api`.
        Assert.Equal(
            EventLogSource.Name,
            app.Services.GetRequiredService<IOptions<EventLogSettings>>().Value.SourceName);
    }

    [GeneratedRegex("""<util:EventSource\b[^>]*\bName="(?<name>[^"]+)"[^>]*>""")]
    private static partial Regex EventSourceName();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Ecr.sln не знайдено від {AppContext.BaseDirectory}.");
    }
}
