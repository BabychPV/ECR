using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Крок <c>Тести Infrastructure (Persistence)</c> (<c>server-part (core-persistence)</c>) не може
/// мовчки стати порожнім зеленим: простір імен у фільтрі <c>verify-all.ps1</c> і в сирцях збігається,
/// а тестів у ньому не менше порогу.
/// </summary>
/// <remarks>
/// ⛔ Фільтр <c>FullyQualifiedName~X</c>, що не відібрав жодного тесту, <c>dotnet test</c> вважає успіхом
/// (нуль тестів, код виходу 0). Перейменували простір імен <c>Ecr.Infrastructure.Tests.Persistence</c> —
/// крок <c>core-persistence</c> зелений із нулем тестів, а крок «Тести .NET» тим часом виключає вже
/// неіснуючий простір і ганяє ВСЕ, що лишилось. Сторож читає сирці й текст скрипта, а не прогін:
/// так само, як <see cref="CiPipelineTests"/> і <see cref="ApiTestShardsTests"/>.
/// </remarks>
public sealed partial class CiPersistenceStepTests
{
    private const string PersistenceNamespace = "Ecr.Infrastructure.Tests.Persistence";

    /// <summary>Поріг кількості тестових методів (<c>[Fact]</c>/<c>[Theory]</c>) у просторі імен.</summary>
    /// <remarks>
    /// Станом на 2026-10-19: 926 атрибутів у 262 файлах (у журналі CI — 1078 тестів із розгорнутими
    /// теоріями). Поріг із запасом: падіння нижче означає, що тести переїхали з простору імен.
    /// Свідоме скорочення набору — знизити поріг тут окремим рядком із причиною.
    /// </remarks>
    private const int MinTestMethods = 700;

    private const int MinTestFiles = 150;

    [GeneratedRegex(@"\[(?:Fact|Theory)\b")]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"(?m)^\s*namespace\s+([A-Za-z0-9_.]+)")]
    private static partial Regex NamespaceDecl();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_просторі_імен_Persistence_достатньо_тестів_і_всі_у_ньому()
    {
        var dir = Path.Combine(SourceTree.Root, "tests", "Ecr.Infrastructure.Tests", "Persistence");
        Assert.True(Directory.Exists(dir), $"Немає каталогу {dir}: набір Persistence переїхав? Онови фільтри в verify-all.ps1 і цей сторож.");

        var sep = Path.DirectorySeparatorChar;
        var methods = 0;
        var files = 0;
        var outside = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var count = TestAttribute().Matches(text).Count;
            if (count == 0)
            {
                continue;
            }

            methods += count;
            files++;

            // ⛔ Тест у каталозі Persistence, але в іншому просторі імен, не потрапив би під фільтр
            // `~Ecr.Infrastructure.Tests.Persistence.`: його ганяв би крок «Тести .NET», а не core-persistence.
            var ns = NamespaceDecl().Match(text).Groups[1].Value;
            if (ns != PersistenceNamespace && !ns.StartsWith(PersistenceNamespace + ".", StringComparison.Ordinal))
            {
                outside.Add($"{Path.GetRelativePath(dir, file)}: namespace {ns}");
            }
        }

        Assert.True(
            outside.Count == 0,
            $"Тести каталогу Persistence поза простором імен {PersistenceNamespace}: {string.Join("; ", outside)}. " +
            "Крок core-persistence їх не бере — виправ namespace або фільтри verify-all.ps1.");
        Assert.True(
            methods >= MinTestMethods && files >= MinTestFiles,
            $"У {PersistenceNamespace} лише {methods} тестових методів у {files} файлах (поріг {MinTestMethods}/{MinTestFiles}). " +
            "Перейменування чи переїзд тестів дає крок core-persistence із нулем тестів і зеленим кодом виходу.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Фільтри_кроків_Persistence_і_Тести_NET_взаємні_заперечення_одного_простору_імен()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "verify-all.ps1"));

        var persistence = StepFilter(script, "Тести Infrastructure (Persistence)");
        var dotnet = StepFilter(script, "Тести .NET");

        // Виняток (борг D16): TestDatabaseSizeTests лишається в кроці «Тести .NET» (core), а не в Persistence.
        var sizeTests = $"{PersistenceNamespace}.TestDatabaseSizeTests";

        // Крок Persistence бере РІВНО простір імен із крапкою наприкінці (щоб не зачепити сусідній `PersistenceX`)
        // за мінусом винятку.
        Assert.Equal($"FullyQualifiedName~{PersistenceNamespace}.&FullyQualifiedName!~{sizeTests}", persistence);

        // Крок «Тести .NET» = (усе, крім Persistence) | виняток. `!~X` проти `~X` того самого простору, інакше
        // тест дублюється або випадає; виняток підключено через `|` поза дужками.
        var orIndex = dotnet.LastIndexOf('|');
        Assert.True(orIndex > 0, $"Фільтр кроку 'Тести .NET' має вигляд (…)|FullyQualifiedName~{sizeTests}: {dotnet}");
        Assert.Equal($"FullyQualifiedName~{sizeTests}", dotnet[(orIndex + 1)..]);

        var excluding = dotnet[..orIndex];
        Assert.True(excluding.StartsWith('(') && excluding.EndsWith(')'), $"Виключення Persistence мають бути в дужках: {excluding}");
        Assert.Contains($"FullyQualifiedName!~{PersistenceNamespace}.", excluding[1..^1].Split('&'));
    }

    private static string StepFilter(string script, string stepName)
    {
        var start = script.IndexOf($"Step '{stepName}'", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У verify-all.ps1 немає кроку '{stepName}'.");

        var next = script.IndexOf("\nStep ", start + 1, StringComparison.Ordinal);
        var body = next < 0 ? script[start..] : script[start..next];

        var filters = Regex.Matches(body, @"--filter\s+'([^']*)'");
        Assert.True(filters.Count == 1, $"У кроці '{stepName}' має бути рівно один літеральний --filter '…', знайдено {filters.Count}.");

        return filters[0].Groups[1].Value;
    }
}
