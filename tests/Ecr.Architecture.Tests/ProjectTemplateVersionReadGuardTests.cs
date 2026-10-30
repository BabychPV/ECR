// tests/Ecr.Architecture.Tests/ProjectTemplateVersionReadGuardTests.cs
using System.Text.RegularExpressions;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RPT-STABLE Part B: <c>Project.TemplateVersionId</c> — це «чинна/остання» версія проєкту (для НОВИХ періодів); відповідь на
/// «яка версія діє за період» дає ЛИШЕ <c>IProjectVersionTimeline</c> / <c>doc.fn_VersionOf</c> (<c>EcrDbContext.VersionOf</c>).
/// Сторож не пускає нових читачів поля поза переліком і не дає лічильникам зростати (храповик: тільки вниз).
/// </summary>
/// <remarks>
/// Перелік нижче - чесний облік: <c>Current</c> - читання, що за визначенням про чинну версію (нові періоди, автор переносу,
/// аудит, лічильники використання, прогрів кешу); <c>Debt</c> - читання, що відповідає про документ/період і ще не переведене
/// на шкалу (на проєкті з однією версією воно правильне, на проєкті з кількома - бере чинну). Мутаційний доказ: додати
/// <c>project.TemplateVersionId</c> у файл поза переліком - тест червоніє; прибрати запис, що має збіги, - теж.
/// </remarks>
public sealed partial class ProjectTemplateVersionReadGuardTests
{
    [GeneratedRegex(@"\b(project|proj|prj|p|pr)\??\.TemplateVersionId\b")]
    private static partial Regex Read();

    private enum Kind
    {
        /// <summary>Читання про ЧИННУ версію проєкту - так і задумано.</summary>
        Current,

        /// <summary>Читання про документ/період, ще не переведене на шкалу версій.</summary>
        Debt,
    }

    private static readonly Dictionary<string, (int Max, Kind Kind)> Allowed = new(StringComparer.Ordinal)
    {
        // Єдине місце, де поле читається для відповіді «яка версія» (разом із SQL-функцією doc.fn_VersionOf).
        ["src/Ecr.Infrastructure/Persistence/ProjectVersionTimelineService.cs"] = (1, Kind.Current),

        // Автор/виконавець переносу: джерело переносу = чинна версія; запис нової чинної версії.
        ["src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs"] = (3, Kind.Current),
        ["src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs"] = (2, Kind.Current),

        // Нові документи/періоди й аудит - за чинною версією.
        ["src/Ecr.Application/Documents/GetDocumentTemplateHandler.cs"] = (1, Kind.Current),
        ["src/Ecr.Application/Periods/ReopenPeriodHandler.cs"] = (1, Kind.Current),
        ["src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Jobs/PeriodStateJob.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Jobs/ConsistencyCheckJob.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Startup/MetadataWarmup.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Persistence/TemplateVersionStore.cs"] = (5, Kind.Current),     // лічильники «проєкти на версії»
        ["src/Ecr.Infrastructure/Persistence/DocumentStore.cs"] = (5, Kind.Current),            // «версія за замовчуванням» переліку/картки
        ["src/Ecr.Infrastructure/Persistence/SheetEditGate.cs"] = (1, Kind.Current),            // без періоду - чинна версія
        ["src/Ecr.Infrastructure/Persistence/CollectionStore.cs"] = (1, Kind.Current),          // збір пише в чинну версію
        ["src/Ecr.Infrastructure/Persistence/SourceEventMapStore.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Persistence/ApprovalQueueStore.cs"] = (2, Kind.Current),
        ["src/Ecr.Infrastructure/Security/ResourceNameResolver.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Security/EffectiveAccessStore.cs"] = (1, Kind.Current),
        ["src/Ecr.Infrastructure/Persistence/MonthColumnRouter.cs"] = (2, Kind.Current),        // сімейство року іде однією версією (правило січня)

        // Борг: відповідає про документ/період, а бере чинну версію проєкту.
        ["src/Ecr.Application/Workflow/ApprovalQueue/ListMyApprovalQueueHandler.cs"] = (4, Kind.Debt),
        ["src/Ecr.Application/Workflow/ReturnedSheets/ListMyReturnedSheetsHandler.cs"] = (3, Kind.Debt),
        ["src/Ecr.Infrastructure/Persistence/ArchiveAwareCellReader.cs"] = (1, Kind.Debt),
        ["src/Ecr.Infrastructure/Persistence/CalculationResultStore.cs"] = (1, Kind.Debt),
        ["src/Ecr.Infrastructure/Persistence/ConsistencyIssueReader.cs"] = (1, Kind.Debt),
        ["src/Ecr.Infrastructure/Security/AccessDecisionService.cs"] = (6, Kind.Debt),
    };

    [Fact]
    [Trait("Requirement", "RPT-STABLE")]
    public void Версію_проєкту_читають_лише_перелічені_файли_і_лічильники_не_ростуть()
    {
        var root = RepositoryRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var count = Read().Count(File.ReadAllText(file));
            if (count > 0)
            {
                found[relative] = count;
            }
        }

        var unlisted = found.Where(f => !Allowed.ContainsKey(f.Key)).Select(f => $"{f.Key} ({f.Value})").ToList();
        Assert.True(
            unlisted.Count == 0,
            "Нове читання Project.TemplateVersionId поза переліком. Версію ПЕРІОДУ дає IProjectVersionTimeline.VersionOfAsync "
            + "(у запиті - EcrDbContext.VersionOf(projectId, periodKey)); справді «чинну» версію - додай файл у Allowed як Current "
            + "з причиною:\n  " + string.Join("\n  ", unlisted));

        var grown = found.Where(f => Allowed.TryGetValue(f.Key, out var a) && f.Value > a.Max)
            .Select(f => $"{f.Key}: {f.Value} > {Allowed[f.Key].Max}").ToList();
        Assert.True(grown.Count == 0, "Лічильник читань зріс (храповик лише вниз):\n  " + string.Join("\n  ", grown));

        var stale = Allowed.Where(a => !found.TryGetValue(a.Key, out var c) || c < a.Value.Max)
            .Select(a => $"{a.Key}: дозволено {a.Value.Max}, є {found.GetValueOrDefault(a.Key)}").ToList();
        Assert.True(stale.Count == 0, "Читань побільшало-менше, ніж у переліку - зменш Max/прибери запис (храповик):\n  " + string.Join("\n  ", stale));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
