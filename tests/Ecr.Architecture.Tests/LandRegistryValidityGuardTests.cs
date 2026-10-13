using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-40, ризик 2 (<c>.sync-local\reports\VALIDFROM-RISK-2026-10-13.md</c>): записи довідників Land створюються
/// скриптами БЕЗ вікна чинності (<c>ValidFrom/ValidTo = NULL</c> — «чинний завжди»), а довідники — нетемпоральні.
/// Знімок для шаблонного перерахунку будується на <c>period.End</c> і відфільтровує записи за вікном, тож
/// <c>validFrom = «сьогодні»</c> у імпорті сховав би довідник від усіх минулих місяців — тихо, без помилки.
/// Сторож читає скрипти як текст (без БД, без запуску PowerShell).
/// </summary>
/// <remarks>
/// Перевіряються лише скрипти довідників Land, що наявні в дереві: відсутній файл не є провалом (гілка може його ще
/// не мати), а наявний із <c>validFrom</c>/<c>validTo</c> у коді — є. Рядки-коментарі (<c>#</c>) пропускаються.
/// </remarks>
public sealed class LandRegistryValidityGuardTests
{
    private static readonly string[] Masks =
    [
        "Import-LandHse400*.ps1",
        "Import-LandAirSubstances.ps1",
        "Import-LandNaeGroup.ps1",
        "LandHse400*.Lib.ps1",
        "LandAirSubstances.Lib.ps1",
        "LandNaeGroup*.Lib.ps1",
        "Build-LandTemplate.ps1",
    ];

    private static string LandDir => Path.Combine(SourceTree.Root, "tools", "land");

    private static IReadOnlyList<string> Scripts()
        => [.. Masks
            .SelectMany(mask => Directory.GetFiles(LandDir, mask, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static IEnumerable<(int Line, string Text)> CodeLines(string path)
        => File.ReadAllLines(path)
            .Select((text, index) => (Line: index + 1, Text: text))
            .Where(l => !l.Text.TrimStart().StartsWith('#'));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипти_довідників_Land_знаходяться_у_дереві()
    {
        // Сторож не мав би бути порожнім: Build-LandTemplate.ps1 і Import-LandHse400Composition.ps1 є в кожній гілці.
        var names = Scripts().Select(Path.GetFileName).ToList();
        Assert.Contains("Build-LandTemplate.ps1", names);
        Assert.Contains("Import-LandHse400Composition.ps1", names);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипти_довідників_Land_не_ставлять_вікно_чинності_записів()
    {
        var offenders = new List<string>();
        foreach (var path in Scripts())
        {
            foreach (var (line, text) in CodeLines(path))
            {
                if (Regex.IsMatch(text, "valid(from|to)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{line}: {text.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Запис довідника Land не має вікна чинності (ValidFrom/ValidTo = NULL, «чинний завжди»); "
            + "validFrom = «сьогодні» сховав би його від минулих періодів у знімку на period.End:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідники_Land_створюються_нетемпоральними()
    {
        var offenders = new List<string>();
        var createCalls = 0;
        foreach (var path in Scripts())
        {
            var code = CodeLines(path).ToList();
            foreach (var (line, text) in code)
            {
                // Будь-яке згадування isTemporal у скрипті довідника Land — лише зі значенням $false.
                if (Regex.IsMatch(text, "isTemporal", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    && !Regex.IsMatch(text, @"isTemporal\s*=\s*\$false\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{line}: {text.Trim()}");
                }
            }

            // Виклик створення довідника (POST /api/v1/registries, без /{code}/entries) мусить нести isTemporal = $false.
            // Хеш-таблиця тіла може бути на наступних рядках (Import-LandAirSubstances.ps1) — дивимось 4 рядки вперед.
            for (var i = 0; i < code.Count; i++)
            {
                var text = code[i].Text;
                if (!Regex.IsMatch(text, @"\bPOST\b", RegexOptions.IgnoreCase)
                    || !text.Contains("'/api/v1/registries'", StringComparison.Ordinal))
                {
                    continue;
                }

                createCalls++;
                var window = string.Join(' ', code.Skip(i).Take(4).Select(l => l.Text));
                if (!Regex.IsMatch(window, @"isTemporal\s*=\s*\$false\b", RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{code[i].Line}: створення довідника без isTemporal = $false: {text.Trim()}");
                }
            }
        }

        Assert.True(createCalls > 0, "Жодного створення довідника не знайдено: сторож втратив предмет перевірки.");
        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }
}
