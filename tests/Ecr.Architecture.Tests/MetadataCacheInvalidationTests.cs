// tests/Ecr.Architecture.Tests/MetadataCacheInvalidationTests.cs
using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож класу дефектів T6-01: кожен обробник, що пише в те, з чого
/// <c>MetadataCache</c> будує знімок версії шаблону, після коміту викликає
/// <c>IMetadataCache.InvalidateAsync</c>.
/// </summary>
/// <remarks>
/// ⛔ Чому це не очевидно. Ключ кешу — <c>v{id}:r{PresentationRevision}</c>, і структурна правка
/// чернетки ревізію не піднімає, тож без явної інвалідації прогрітий знімок живе до 30 хв. T6-01:
/// <c>Save/DeleteTableRelationHandler</c> не інвалідували, бо коментар стверджував, що зв'язки йдуть
/// повз кеш, — а D-230 уже додав їх у знімок (<c>HasActiveRollupOrCheck</c>). <c>Validate</c> читав
/// без кешу й показував Error, а <c>SubmitSheetHandler</c> дивився на старий прапор і пропускав Block.
/// <para>
/// Тому набір «що кешується» тут НЕ виписаний руками: він береться з тексту <c>MetadataCache.cs</c>
/// (усі <c>db.&lt;DbSet&gt;</c>, що є властивостями <see cref="EcrDbContext"/>). Новий DbSet у знімку
/// автоматично робить його записувачів підозрюваними, а виняток «пише не в кеш» — хибним.
/// </para>
/// <para>
/// Записувач — клас (у <c>src/**</c>, без <c>Ecr.Domain</c> і міграцій), що: бере блок версії
/// (<c>LockVersionForUpdateAsync</c> / <c>DraftVersionLock.EnsureDraftUnderLockAsync</c>), або кличе
/// <c>ApplyPresentationAsync</c>/<c>IncrementPresentationRevisionAsync</c>, або створює кешовану
/// сутність (<c>new SheetDef(</c>…), або пише в кешований DbSet (<c>db.TableRelations.Add(</c>…), або
/// пише через <c>IRepository&lt;КешованаСутність, …&gt;</c>.
/// </para>
/// <para>
/// ⚠ Межі, названі прямо: текстовий пошук по коду без коментарів і рядкових літералів. Сирий SQL до
/// <c>cfg.*</c> і правку сутності, взятої з агрегата, БЕЗ блоку версії сторож не побачить — блок
/// версії перед структурною правкою вимагає <c>DraftVersionLockTests</c> (C5). Порядок
/// «інвалідація після коміту» перевіряється на рівні класу (остання інвалідація нижче останнього
/// <c>SaveChangesAsync</c>/<c>ExecuteInTransactionAsync</c>), не по кожному шляху методу.
/// <c>tools/**</c> (генератори даних поза застосунком) не скануються.
/// </para>
/// </remarks>
public sealed partial class MetadataCacheInvalidationTests
{
    private const string MetadataCacheFile = "src/Ecr.Infrastructure/Caching/MetadataCache.cs";

    /// <summary>
    /// Записувачі, що пишуть ЛИШЕ в те, чого немає в знімку. Значення — DbSet, у який вони пишуть:
    /// тест перевіряє, що його справді немає серед кешованих, тобто обґрунтування лишається правдою.
    /// </summary>
    private static readonly Dictionary<string, string> NotCachedWriters = new(StringComparer.Ordinal)
    {
        // Правила доступу до періодів читаються повз кеш (ListPeriodAccessRulesAsync, AsNoTracking).
        ["CreatePeriodAccessRuleHandler"] = nameof(EcrDbContext.PeriodAccessRules),
        ["SavePeriodAccessRuleHandler"] = nameof(EcrDbContext.PeriodAccessRules),
        ["DeletePeriodAccessRuleHandler"] = nameof(EcrDbContext.PeriodAccessRules),

        // Умовне форматування і стилі — окремі сховища (IConditionalFormatStore, IStyleCatalog).
        ["SaveConditionalFormatsHandler"] = nameof(EcrDbContext.ConditionalFormatRules),
        ["SaveStyleDefHandler"] = nameof(EcrDbContext.StyleDefs),

        // Прив'язки методики до колонок бере блок версії, але пише лише cfg.CalculationBinding.
        ["SaveCalculationBindingHandler"] = nameof(EcrDbContext.CalculationBindings),
    };

    /// <summary>
    /// Інфраструктура, що містить тригер, але сама записувачем-обробником не є.
    /// </summary>
    /// <remarks>
    /// <c>TemplateVersionStore</c> — адаптер: <c>ApplyPresentationAsync</c> і підйом ревізії кличуть
    /// обробники, які самі тут перевіряються (тригер «презентація»); клон і нова чернетка пишуть лише в
    /// НОВУ версію, ключа якої в кеші ще немає. <c>DraftVersionLock</c> — помічник блоку, його
    /// викликачі перевіряються.
    /// </remarks>
    private static readonly string[] Plumbing = ["TemplateVersionStore", "DraftVersionLock"];

    [Fact]
    [Trait("Requirement", "ФВ-2.12")]
    public void Кожен_записувач_кешованих_метаданих_інвалідує_кеш_після_коміту()
    {
        var cached = CachedSets();
        var classes = Classes();
        var writers = classes.Where(c => IsWriter(c, cached)).ToList();

        var missing = new List<string>();
        foreach (var writer in writers)
        {
            if (NotCachedWriters.ContainsKey(writer.Name) || Plumbing.Contains(writer.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var verdict = InvalidationVerdict(writer);
            if (verdict is not null)
            {
                missing.Add($"{writer.Name} ({writer.Path}): {verdict}");
            }
        }

        // ⚠ Не порожньо: інакше перейменування тригерів зробило б сторож вічнозеленим. Саме ці два
        // обробники пропустили інвалідацію в T6-01.
        var names = writers.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SaveTableRelationHandler", names);
        Assert.Contains("DeleteTableRelationHandler", names);
        Assert.True(writers.Count >= 15, $"Знайдено лише {writers.Count} записувачів — тригери сторожа зламались?");

        Assert.True(
            missing.Count == 0,
            "T6-01: записувач кешованих метаданих шаблону не інвалідує IMetadataCache після коміту — прогрітий "
            + "знімок (до 30 хв) розійдеться з базою, і Submit/перерахунок діятимуть за старою структурою. "
            + "Додай `await metadataCache.InvalidateAsync(templateVersionId, ct)` ПІСЛЯ транзакції "
            + "або, якщо клас пише лише некешоване, рядок у NotCachedWriters:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    [Trait("Requirement", "ФВ-2.12")]
    public void Винятки_пишуть_лише_некешоване_і_не_застаріли()
    {
        var cached = CachedSets();
        var classes = Classes().ToLookup(c => c.Name, StringComparer.Ordinal);
        var dbSets = DbSets();
        var wrong = new List<string>();

        foreach (var (name, set) in NotCachedWriters)
        {
            if (!dbSets.ContainsKey(set))
            {
                wrong.Add($"{name}: DbSet {set} не існує в EcrDbContext");
            }
            else if (cached.ContainsKey(set))
            {
                // Саме T6-01: «пише повз кеш» стало неправдою, щойно знімок почав читати цей набір.
                wrong.Add($"{name}: {set} тепер читає MetadataCache — виняток хибний, потрібна інвалідація");
            }

            var bodies = classes[name].ToList();
            if (bodies.Count == 0)
            {
                wrong.Add($"{name}: класу більше немає — прибери рядок");
                continue;
            }

            if (!bodies.Any(cls => IsWriter(cls, cached)))
            {
                wrong.Add($"{name}: більше не записувач — прибери рядок");
            }

            foreach (var cls in bodies)
            {
                var cachedWrite = CachedWrite(cls, cached);
                if (cachedWrite is not null)
                {
                    wrong.Add($"{name}: пише в кешоване ({cachedWrite}) — виняток хибний");
                }
            }
        }

        foreach (var name in Plumbing)
        {
            if (!classes.Contains(name))
            {
                wrong.Add($"{name}: класу більше немає — прибери з Plumbing");
            }
        }

        Assert.True(wrong.Count == 0, "Винятки сторожа інвалідації кешу метаданих: " + string.Join("; ", wrong));
    }

    [Fact]
    public void Набір_кешованого_береться_з_MetadataCache()
    {
        var cached = CachedSets();

        // Контроль розбору: структура і зв'язки (D-230) мусять бути розпізнані, інакше сторож сліпий.
        foreach (var set in new[]
                 {
                     nameof(EcrDbContext.TemplateVersions), nameof(EcrDbContext.SheetDefs),
                     nameof(EcrDbContext.TableDefs), nameof(EcrDbContext.ColumnDefs), nameof(EcrDbContext.RowDefs),
                     nameof(EcrDbContext.ValidationRules), nameof(EcrDbContext.FormulaDefs),
                     nameof(EcrDbContext.HeaderFieldDefs), nameof(EcrDbContext.TableRelations),
                 })
        {
            Assert.True(cached.ContainsKey(set), $"MetadataCache.cs читає {set}, а розбір сторожа його не бачить.");
        }
    }

    /// <summary>Чому клас не проходить; <c>null</c> — проходить.</summary>
    private static string? InvalidationVerdict(ClassBody writer)
    {
        var field = MetadataCacheParameter().Match(writer.Code);
        if (!field.Success)
        {
            return "немає залежності IMetadataCache";
        }

        var call = $"{field.Groups[1].Value}.InvalidateAsync(";
        var invalidate = writer.Code.LastIndexOf(call, StringComparison.Ordinal);
        if (invalidate < 0)
        {
            return $"немає виклику {call}";
        }

        var commit = Math.Max(
            writer.Code.LastIndexOf("SaveChangesAsync(", StringComparison.Ordinal),
            writer.Code.LastIndexOf("ExecuteInTransactionAsync(", StringComparison.Ordinal));
        if (commit > invalidate)
        {
            return "інвалідація стоїть ДО коміту — читач між ними збудує знімок зі старих даних";
        }

        return null;
    }

    private static bool IsWriter(ClassBody cls, IReadOnlyDictionary<string, string> cached)
        => cls.Code.Contains("LockVersionForUpdateAsync(", StringComparison.Ordinal)
           || cls.Code.Contains("EnsureDraftUnderLockAsync(", StringComparison.Ordinal)
           || cls.Code.Contains("ApplyPresentationAsync(", StringComparison.Ordinal)
           || cls.Code.Contains("IncrementPresentationRevisionAsync(", StringComparison.Ordinal)
           || CachedWrite(cls, cached) is not null;

    /// <summary>Перший знайдений прямий запис у кешоване; <c>null</c> — немає.</summary>
    private static string? CachedWrite(ClassBody cls, IReadOnlyDictionary<string, string> cached)
    {
        foreach (var (set, entity) in cached)
        {
            if (Regex.IsMatch(cls.Code, $@"\bnew\s+{entity}\s*\("))
            {
                return $"new {entity}(";
            }

            if (Regex.IsMatch(cls.Code, $@"\.{set}\s*\.\s*(Add|AddRange|Remove|RemoveRange|Update|UpdateRange|Attach)\s*\(")
                || Regex.IsMatch(cls.Code, $@"\.{set}\b[^;]*\.\s*Execute(Update|Delete)(Async)?\s*\("))
            {
                return $"{set}: Add/Remove/Update/Execute*";
            }

            foreach (Match repository in Regex.Matches(cls.Code, $@"IRepository<\s*{entity}\s*,\s*\w+\s*>\s+(\w+)"))
            {
                var variable = repository.Groups[1].Value;
                if (Regex.IsMatch(cls.Code, $@"\b{variable}\s*\.\s*(Add|Remove|Update|Delete)\w*\s*\("))
                {
                    return $"IRepository<{entity}>.{variable}";
                }
            }
        }

        return null;
    }

    /// <summary>DbSet → тип сутності, які читає <c>MetadataCache</c>.</summary>
    private static Dictionary<string, string> CachedSets()
    {
        var dbSets = DbSets();
        var file = SourceTree.Production("Ecr.Infrastructure").Single(f => f.Path == MetadataCacheFile);
        var code = string.Join('\n', file.CodeLines().Select(l => l.Text));

        return DbAccess().Matches(code)
            .Select(m => m.Groups[1].Value)
            .Where(dbSets.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(s => s, s => dbSets[s], StringComparer.Ordinal);
    }

    private static Dictionary<string, string> DbSets()
        => typeof(EcrDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0].Name, StringComparer.Ordinal);

    /// <summary>Класи верхнього рівня з кодом без коментарів і рядкових літералів.</summary>
    private static List<ClassBody> Classes()
    {
        var result = new List<ClassBody>();

        foreach (var file in SourceTree.Production())
        {
            if (file.Path.StartsWith("src/Ecr.Domain/", StringComparison.Ordinal) || file.Path == MetadataCacheFile)
            {
                continue;
            }

            var lines = file.CodeLines().Select(l => l.Text).ToList();
            string? current = null;
            var body = new List<string>();

            foreach (var line in lines)
            {
                var declaration = ClassDeclaration().Match(line);
                if (declaration.Success)
                {
                    Flush();
                    current = declaration.Groups[1].Value;
                }

                body.Add(line);
            }

            Flush();

            void Flush()
            {
                if (current is not null)
                {
                    result.Add(new ClassBody(current, file.Path, string.Join('\n', body)));
                }

                body.Clear();
            }
        }

        return result;
    }

    private sealed record ClassBody(string Name, string Path, string Code);

    [GeneratedRegex(@"^(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\bIMetadataCache\s+(\w+)")]
    private static partial Regex MetadataCacheParameter();

    [GeneratedRegex(@"\bdb\.(\w+)\b")]
    private static partial Regex DbAccess();
}
