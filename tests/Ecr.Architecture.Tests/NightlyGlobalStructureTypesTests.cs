// tests/Ecr.Architecture.Tests/NightlyGlobalStructureTypesTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ✎ PSS-15 (повторний огляд M-new-1, D-562): кожен тип запису ГЛОБАЛЬНОГО журналу структури (<c>aud.StructureChange</c> з
/// <c>TemplateVersionId = 0</c>), який пише код, мусить бути явно класифікований: або в
/// <c>NightlyInputChangeQuery.GlobalStructureEntityTypes</c> (будить нічний перерахунок), або в
/// <see cref="NotCalculationInputs"/> з причиною. Новий тип, що змінює розрахунок, не може тихо пройти повз пропуск
/// незмінних — збірка червоніє, доки хтось не вирішить, куди він належить.
/// </summary>
/// <remarks>
/// <para>
/// Писачі шукаються в ТЕКСТІ <c>src/**</c> (як <c>SheetCopyQuerySitesTests</c>): іменований аргумент
/// <c>TemplateVersionId: 0</c> з наступним <c>EntityType: …</c> (літерал, константа <c>const string</c> — у тому самому
/// файлі чи будь-де в <c>src</c>) і сирий <c>INSERT INTO aud.StructureChange … VALUES (@…, 0, N'…'</c>. Писач, що передає
/// тип ПАРАМЕТРОМ (<c>IntegrationConfigAudit.WriteAsync(…, string entityType, …)</c>), — у <see cref="IndirectWriters"/>:
/// його типи — усі <c>const string</c> того файлу. Невідомий параметр — теж червоне.
/// </para>
/// <para>
/// ⚠ Межа: писачі з версією-змінною (<c>TemplateVersionId: templateVersionId</c>) сюди не входять — вони пишуть під версією
/// документа чи проєкту, і їх нічний перерахунок бачить гілкою версій документа чи станів періодів.
/// </para>
/// <para>
/// МУТАЦІЇ: прибрати рядок із <see cref="NotCalculationInputs"/> — червоний (тип некласифікований); додати в <c>src</c>
/// писача з новим типом — червоний.
/// </para>
/// </remarks>
public sealed partial class NightlyGlobalStructureTypesTests
{
    /// <summary>Типи, що НЕ є входом розрахунку, — і чому.</summary>
    private static readonly Dictionary<string, string> NotCalculationInputs = new(StringComparer.Ordinal)
    {
        ["wf.ApprovalRoute"] = "маршрут погодження: хто погоджує, а не числа",
        ["ReportVersion"] = "версія звіту: читає результати, не вхід розрахунку",
        ["cfg.PermitTypeDef"] = "тип дозволу: значення доходять у комірки записом Permit — гілка правок комірок",
        ["cfg.PermitGroup"] = "група дозволів: те саме — значення доходять комірками Permit",
        ["cfg.RegistryDefinitionDraft"] = "чернетка опису довідника: до публікації (cfg.RegistryDef) не діє",
        ["PeriodPolicy"] = "політика періодів проєкту: переходи станів ловить гілка doc.Period.StateChangedAt",
        ["ext.SourceEntity"] = "сутність/політика збору: дані доходять комірками Integration — гілка правок",
        ["ext.EntityFieldMap"] = "мапінг поля збору: те саме — дані доходять комірками Integration",
        ["ext.CollectionSchedule"] = "розклад збору: коли збирати, а не що рахувати",
        ["ext.SourceEventMap"] = "мапінг подій джерела: рядки подій доходять комірками Integration",
        ["ext.RowWindowMap"] = "прив'язка PI за вікном рядка: значення доходять комірками Integration",
        ["ext.DataSource"] = "з'єднання з джерелом (адреса, транспорт): не дані",
        ["ext.SourceEventLink"] = "видалення рядка синком подій: синк торкається документа — гілка «документ»",
        ["calc.Methodology"] = "D-565: прапор «трасувати проміжні кроки» (SetTraceSteps) — чи пишуться кроки трейсу; чисел не змінює",
    };

    /// <summary>Писачі, що отримують тип параметром: типи — усі <c>const string</c> файлу.</summary>
    private static readonly HashSet<string> IndirectWriters = new(StringComparer.Ordinal)
    {
        "src/Ecr.Application/Sources/IntegrationConfigAudit.cs",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Finding", "PSS-15")]
    public void Кожен_глобальний_тип_журналу_структури_класифікований_для_нічного_перерахунку()
    {
        var allowed = AllowList();
        Assert.Empty(allowed.Intersect(NotCalculationInputs.Keys, StringComparer.Ordinal));

        var files = SourceTree.Production();
        var written = WrittenGlobalTypes(files, out var unresolved);

        Assert.True(unresolved.Count == 0, "Тип журналу з TemplateVersionId = 0 не розв'язано (константу не знайдено чи тип "
            + "іде параметром — додай файл у IndirectWriters):\n  " + string.Join("\n  ", unresolved));

        // Самоперевірка сканера: відомі писачі знайдено (інакше регулярні вирази зламались і тест мовчки зелений).
        Assert.Contains("cfg.CalculationBinding", written.Keys);
        Assert.Contains("wf.ApprovalRoute", written.Keys);
        Assert.Contains("ext.SourceEventLink", written.Keys);
        Assert.Contains("ext.EntityFieldMap", written.Keys);

        var unclassified = written
            .Where(w => !allowed.Contains(w.Key) && !NotCalculationInputs.ContainsKey(w.Key))
            .Select(w => $"{w.Key}  ({string.Join(", ", w.Value)})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "Новий тип глобального журналу структури (TemplateVersionId = 0) не класифіковано для нічного перерахунку "
            + "(D-562): додай його в NightlyInputChangeQuery.GlobalStructureEntityTypes, якщо він змінює розрахунок, "
            + "або в NightlyGlobalStructureTypesTests.NotCalculationInputs з причиною:\n  " + string.Join("\n  ", unclassified));
    }

    /// <summary>Перелік, що будить нічний перерахунок, — з продуктового коду (internal, тож рефлексією).</summary>
    private static HashSet<string> AllowList()
    {
        var type = typeof(Ecr.Infrastructure.Jobs.RecalculationJob).Assembly
                       .GetType("Ecr.Infrastructure.Jobs.NightlyInputChangeQuery", throwOnError: true)!;
        var field = type.GetField("GlobalStructureEntityTypes", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    ?? throw new InvalidOperationException("NightlyInputChangeQuery.GlobalStructureEntityTypes не знайдено.");
        return new HashSet<string>((string[])field.GetValue(null)!, StringComparer.Ordinal);
    }

    /// <summary>Тип → файли, що його пишуть з <c>TemplateVersionId = 0</c>.</summary>
    private static Dictionary<string, SortedSet<string>> WrittenGlobalTypes(
        IReadOnlyList<SourceFile> files, out List<string> unresolved)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        unresolved = [];

        void Add(string type, string path)
        {
            if (!result.TryGetValue(type, out var paths))
            {
                result[type] = paths = new SortedSet<string>(StringComparer.Ordinal);
            }

            paths.Add(path);
        }

        foreach (var file in files)
        {
            foreach (Match match in NamedWriter().Matches(file.Text))
            {
                var expression = match.Groups["expr"].Value.Trim();
                if (expression.StartsWith('"'))
                {
                    Add(expression.Trim('"'), file.Path);
                    continue;
                }

                if (IndirectWriters.Contains(file.Path))
                {
                    foreach (Match constant in ConstString().Matches(file.Text))
                    {
                        Add(constant.Groups["value"].Value, file.Path);
                    }

                    continue;
                }

                var name = expression[(expression.LastIndexOf('.') + 1)..];
                if (Resolve(name, file, files) is { } value)
                {
                    Add(value, file.Path);
                }
                else
                {
                    unresolved.Add($"{file.Path}: EntityType: {expression}");
                }
            }

            foreach (Match match in RawWriter().Matches(file.Text))
            {
                Add(match.Groups["type"].Value, file.Path);
            }
        }

        return result;
    }

    /// <summary>Значення <c>const string {name}</c>: спершу в тому самому файлі, потім (однозначно) будь-де в <c>src</c>.</summary>
    private static string? Resolve(string name, SourceFile file, IReadOnlyList<SourceFile> files)
    {
        var pattern = new Regex($@"const\s+string\s+{Regex.Escape(name)}\s*=\s*""(?<value>[^""]*)""");
        if (pattern.Match(file.Text) is { Success: true } local)
        {
            return local.Groups["value"].Value;
        }

        var values = files
            .SelectMany(f => pattern.Matches(f.Text).Select(m => m.Groups["value"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values.Count == 1 ? values[0] : null;
    }

    [GeneratedRegex(@"TemplateVersionId:\s*0\s*,(?:(?!TemplateVersionId:)[\s\S]){0,600}?EntityType:\s*(?<expr>[^,\r\n)]+)")]
    private static partial Regex NamedWriter();

    [GeneratedRegex(@"INSERT\s+INTO\s+aud\.StructureChange[\s\S]{0,400}?VALUES\s*\(\s*@\w+\s*,\s*0\s*,\s*N'(?<type>[^']+)'")]
    private static partial Regex RawWriter();

    [GeneratedRegex(@"const\s+string\s+\w+\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex ConstString();
}
