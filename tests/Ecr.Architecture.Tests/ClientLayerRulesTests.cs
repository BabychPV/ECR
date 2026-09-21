using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Шари клієнта (<c>src/Ecr.Web/src</c>): <c>shared</c> не імпортує
/// <c>features</c>/<c>pages</c>/<c>app</c>; <c>features</c> не імпортує
/// <c>pages</c>/<c>app</c>. Напрям залежностей — лише вниз:
/// app → pages → features → shared.
/// </summary>
/// <remarks>
/// Імпорт — це рядковий літерал ПОЗА коментарем (<see cref="TypeScriptSource"/>),
/// перед яким у коді стоїть <c>from</c>, <c>import</c> або <c>import(</c>.
/// Тобто враховано <c>import … from</c>, <c>export … from</c>, side-effect
/// <c>import '…'</c>, динамічний <c>import('…')</c> і <c>typeof import('…')</c>.
/// Специфікатор розв'язується і для <c>@/</c>, і для відносних шляхів.
///
/// ⚠ Тести (<c>__tests__/</c>, <c>*.test.ts(x)</c>) виведено з-під правила
/// за шаблоном шляху: інтеграційний тест фічі законно рендерить сторінку чи
/// маршрутизатор, у якому фіча живе (<c>SourcesPage</c> для
/// <c>integration</c>, <c>routes</c> для <c>search</c>). До продукту тест не
/// потрапляє, тож циклу залежностей у збірці він не створює.
/// </remarks>
public sealed class ClientLayerRulesTests
{
    /// <summary>Шар → шари, які йому імпортувати заборонено.</summary>
    private static readonly Dictionary<string, string[]> Forbidden = new(StringComparer.Ordinal)
    {
        ["shared"] = ["features", "pages", "app"],
        ["features"] = ["pages", "app"],
    };

    /// <summary>
    /// Наявні порушення в продуктовому коді станом на 2026-09-21. Ratchet у
    /// два боки: нове порушення → червоне; виправлене, але не прибране зі
    /// списку → теж червоне. Формат: «файл → шлях імпорту без розширення»,
    /// обидва відносно <c>src/Ecr.Web/src</c>.
    /// </summary>
    private static readonly string[] KnownViolations =
    [
        // Утиліта безпечного return-шляху живе в pages, а потрібна гріду.
        // Місце їй — shared/routing; перенесення — окремий PR.
        "features/grid/lostEdits.ts -> pages/safeReturnPath",
        // Сторож незбережених змін у shared знає про автозбереження гріду.
        // Правильно — інверсія (реєстрація провайдера pending з features).
        "shared/ui/UnsavedGuard.tsx -> features/grid/autosave",
        "shared/ui/UnsavedGuard.tsx -> features/grid/pendingStore",
    ];

    private static readonly Regex ImportBefore = new(
        @"(?:\bfrom|\bimport|\bimport\s*\()\s*$",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Шари_клієнта_імпортують_лише_вниз()
    {
        var actual = Violations(WebRoot()).ToHashSet(StringComparer.Ordinal);
        var known = KnownViolations.ToHashSet(StringComparer.Ordinal);

        var fresh = actual.Except(known).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var gone = known.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(
            fresh.Count == 0,
            "Нове порушення шарів клієнта (shared ↛ features/pages/app, features ↛ pages/app):\n  "
            + string.Join("\n  ", fresh));
        Assert.True(
            gone.Count == 0,
            "Порушення виправлено — приберіть його з KnownViolations у ClientLayerRulesTests:\n  "
            + string.Join("\n  ", gone));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("import { X } from '@/pages/A';", true)]
    [InlineData("export { X } from '../../pages/A';", true)]
    [InlineData("import '@/app/side';", true)]
    [InlineData("const m = await import('@/app/routes');", true)]
    [InlineData("// import { X } from '@/pages/A';", false)]
    [InlineData("/* import { X } from '@/app/routes'; */", false)]
    [InlineData("const s = 'from @/pages/A';", false)]
    [InlineData("import { X } from '@/shared/ui/Button';", false)]
    public void Розбір_імпортів_бачить_код_і_не_бачить_коментарів(string line, bool violates)
    {
        var found = ImportsOf(line, "features/x/File.ts")
            .Any(target => Layer(target) is "pages" or "app");

        Assert.Equal(violates, found);
    }

    private static IEnumerable<string> Violations(string web)
    {
        foreach (var path in Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".ts", StringComparison.Ordinal) && !path.EndsWith(".tsx", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (IsTest(relative) || !Forbidden.TryGetValue(Layer(relative) ?? "", out var banned))
            {
                continue;
            }

            foreach (var target in ImportsOf(File.ReadAllText(path), relative))
            {
                if (Layer(target) is { } layer && banned.Contains(layer))
                {
                    yield return $"{relative} -> {target}";
                }
            }
        }
    }

    /// <summary>Цілі імпортів модуля відносно <c>src/Ecr.Web/src</c>, без розширення.</summary>
    private static IEnumerable<string> ImportsOf(string text, string relative)
    {
        var source = TypeScriptSource.Parse(text);
        foreach (var token in source.Strings)
        {
            var before = text[Math.Max(0, token.Start - 40)..token.Start];
            if (!ImportBefore.IsMatch(before))
            {
                continue;
            }

            var spec = token.Text;
            string? target = null;
            if (spec.StartsWith("@/", StringComparison.Ordinal))
            {
                target = spec[2..];
            }
            else if (spec.StartsWith('.'))
            {
                var dir = Path.GetDirectoryName(relative)!.Replace('\\', '/');
                target = Normalize($"{dir}/{spec}");
            }

            if (target is not null)
            {
                yield return Regex.Replace(target, @"\.(tsx?|jsx?)$", "");
            }
        }
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string? Layer(string relative)
    {
        var slash = relative.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? null : relative[..slash];
    }

    private static bool IsTest(string relative) =>
        relative.Contains("/__tests__/", StringComparison.Ordinal)
        || Regex.IsMatch(relative, @"\.(test|spec)\.tsx?$");

    private static string WebRoot()
    {
        var web = Path.Combine(SourceTree.Root, "src", "Ecr.Web", "src");
        Assert.True(Directory.Exists(web), $"Немає каталогу клієнта: {web}");
        return web;
    }
}
