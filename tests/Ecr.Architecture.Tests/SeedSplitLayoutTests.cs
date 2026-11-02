using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Форма сіду після поділу <c>09-seed.sql</c> (<c>tools/split-seed.ps1</c>):
/// <c>seed/00-prelude.sql</c> → <c>seed/features/&lt;AREA&gt;-&lt;slug&gt;.sql</c> → <c>seed/99-epilogue.sql</c>.
/// </summary>
/// <remarks>
/// ⛔ Порядок файлів фіч — за іменем (ordinal), тобто випадковий щодо змісту. Це
/// безпечно лише доти, доки файл фічі ЛИШЕ наповнює тимчасові таблиці прологу
/// (<c>#I18N</c>, <c>#UiChanged</c>, <c>#UiRemoved</c>), а застосовує їх епілог:
/// вставки в таблицю з ключем комутують. Файл, що пише в справжню таблицю чи
/// читає її, зробив би результат залежним від імені файла — тому сторож нижче.
/// Фіча, якій потрібен порядок (роздача прав ролі, одиниці з UPDATE), іде в
/// пролог або епілог на своє місце.
/// </remarks>
public sealed partial class SeedSplitLayoutTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файли_сіду_лише_пролог_епілог_і_фічі_з_іменем_за_шаблоном()
    {
        var dir = SeedSql.SeedDirectory();
        var parts = SeedScript.ReadDirectoryParts(dir).Keys.ToList();

        var badNames = parts
            .Where(p => p.StartsWith(SeedScript.FeaturesFolder + "/", StringComparison.Ordinal))
            .Select(p => p[(SeedScript.FeaturesFolder.Length + 1)..])
            .Where(name => !FeatureName().IsMatch(name))
            .ToList();
        Assert.True(
            badNames.Count == 0,
            $"Імена файлів фіч у {SeedSql.DisplayPath}{SeedScript.FeaturesFolder}/ — лише <AREA>-<slug>.sql "
            + "(AREA: A-Z0-9, slug: a-z0-9 і дефіс): " + string.Join(", ", badNames));

        var other = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(f => !f.EndsWith(".sql", StringComparison.Ordinal))
            .ToList();
        Assert.True(other.Count == 0, $"У {SeedSql.DisplayPath} лише .sql: " + string.Join(", ", other));

        // Пролог, епілог, нуль сторонніх частин — інакше Order кидає.
        var order = SeedScript.Order(parts);
        Assert.Equal(SeedScript.PreludeFile, order[0]);
        Assert.Equal(SeedScript.EpilogueFile, order[^1]);
        Assert.True(order.Count > 2, "Жодного файла фічі — поділ не відбувся або glob їх не бачить.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Старого_09_seed_немає_а_збірка_несе_рівно_файли_дерева()
    {
        var sqlDir = Path.GetDirectoryName(SeedSql.SeedDirectory())!;
        Assert.False(
            File.Exists(Path.Combine(sqlDir, "09-seed.sql")),
            "Persistence/Sql/09-seed.sql повернувся — сід живе в seed/ (tools/split-seed.ps1); "
            + "дві копії розійшлися б, а виконується лише seed/.");

        var resources = typeof(SeedScript).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(resources, r => r.EndsWith("09-seed.sql", StringComparison.Ordinal));

        // ⛔ Файл, якого не підхопив <EmbeddedResource>, тести бачили б, а застосунок — ні.
        var embedded = SeedScript.ReadEmbeddedParts();
        var onDisk = SeedScript.ReadDirectoryParts(SeedSql.SeedDirectory());
        Assert.Equal(
            onDisk.Keys.OrderBy(k => k, StringComparer.Ordinal),
            embedded.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(SeedSql.Text, SeedScript.ReadEmbedded());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_фічі_лише_наповнює_тимчасові_таблиці()
    {
        var problems = new List<string>();
        foreach (var (path, text) in SeedScript.ReadDirectoryParts(SeedSql.SeedDirectory())
                     .Where(p => p.Key.StartsWith(SeedScript.FeaturesFolder + "/", StringComparison.Ordinal))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            problems.AddRange(FeatureProblems(text).Select(p => $"  {path}: {p}"));
        }

        Assert.True(
            problems.Count == 0,
            "Файл фічі виконується в довільному порядку (за іменем), тому лише наповнює #I18N / #UiChanged / "
            + "#UiRemoved порціями `INSERT INTO #… ` + GO. Що залежить від порядку — у 00-prelude.sql або "
            + "99-epilogue.sql на своє місце:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_фіч_червоніє_на_записі_в_справжню_таблицю()
    {
        // Доказ, що сторож уміє червоніти: та сама перевірка на зразках.
        const string good = "-- X:y ── ru ──\nINSERT INTO #I18N ([Key], Lang, Val)\nSELECT v.[Key], v.Lang, v.Val\n"
                            + "  FROM (VALUES\n    (N'a', N'ru', N'b')\n       ) AS v ([Key], Lang, Val)\nOPTION (RECOMPILE);\nGO\n"
                            + "-- X:y ── кінець секції ──\n";
        Assert.Empty(FeatureProblems(good));
        Assert.NotEmpty(FeatureProblems(good.Replace("INSERT INTO #I18N", "INSERT INTO sys_ecr.UiString", StringComparison.Ordinal)));
        Assert.NotEmpty(FeatureProblems(good.Replace("OPTION (RECOMPILE);\n", "OPTION (RECOMPILE);\nUPDATE sec.Role SET Code = Code;\n", StringComparison.Ordinal)));
        Assert.NotEmpty(FeatureProblems(good.Replace("GO\n-- X:y ── кінець", "-- X:y ── кінець", StringComparison.Ordinal)));
        Assert.NotEmpty(FeatureProblems("MERGE sec.Role AS t USING (VALUES (1)) AS s (x) ON 1 = 0 WHEN NOT MATCHED THEN INSERT (Code) VALUES (N'x');\nGO\n"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Пара_ключ_мова_заведена_у_сіді_рівно_один_раз()
    {
        var duplicates = Duplicates(SeedSql.Text);

        // ⛔ Порожній розбір дав би зелене на будь-якому сіді.
        Assert.True(CatalogRows(SeedSql.Text).Count > 1000, "Розібрано підозріло мало рядків каталогу — сторож дивиться не туди.");
        Assert.True(
            duplicates.Count == 0,
            "Пара (ключ, мова) заведена в сіді більше одного разу (en-MERGE прологу + порції #I18N усіх файлів): "
            + "MERGE вставив би одну, решта мовчки зникла б або впала б на PRIMARY KEY #I18N:"
            + Environment.NewLine + string.Join(Environment.NewLine, duplicates));

        // Доказ, що сторож уміє червоніти: перший рядок каталогу, повторений порцією #I18N.
        var first = CatalogRows(SeedSql.Text)[0];
        var injected = SeedSql.Text + $"\nINSERT INTO #I18N ([Key], Lang, Val)\nSELECT v.[Key], v.Lang, v.Val\n  FROM (VALUES\n"
                       + $"    (N'{first.Key}', N'{first.Lang}', N'x')\n       ) AS v ([Key], Lang, Val)\nOPTION (RECOMPILE);\nGO\n";
        Assert.NotEmpty(Duplicates(injected));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порядок_частин_ordinal_і_сторонню_частину_не_приймає()
    {
        var order = SeedScript.Order(["99-epilogue.sql", "features/b-x.sql", "features/B-x.sql", "00-prelude.sql", @"features\A-x.sql"]);
        Assert.Equal(["00-prelude.sql", "features/A-x.sql", "features/B-x.sql", "features/b-x.sql", "99-epilogue.sql"], order);

        Assert.Throws<InvalidOperationException>(() => SeedScript.Order(["00-prelude.sql", "99-epilogue.sql", "extra.sql"]));
        Assert.Throws<InvalidOperationException>(() => SeedScript.Order(["00-prelude.sql", "99-epilogue.sql", "features/sub/A-x.sql"]));
        Assert.Throws<InvalidOperationException>(() => SeedScript.Order(["00-prelude.sql", "features/A-x.sql"]));

        // Файл без переводу рядка в кінці не склеює свій GO з наступним файлом.
        var composed = SeedScript.Compose(new Dictionary<string, string>
        {
            ["00-prelude.sql"] = "SELECT 1;\nGO",
            ["features/A-x.sql"] = "-- a\n",
            ["99-epilogue.sql"] = "SELECT 2;\n",
        });
        Assert.Equal("SELECT 1;\nGO\n-- a\nSELECT 2;\n", composed);
    }

    /// <summary>Порушення форми файла фічі; порожньо — файл безпечний у будь-якому порядку.</summary>
    internal static List<string> FeatureProblems(string text)
    {
        var problems = new List<string>();
        var code = text.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        if (code.Count == 0 || !string.Equals(code[^1], "GO", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("останній рядок коду — не GO (наступний файл склеївся б із цим батчем)");
        }

        foreach (var batch in SqlBatches.Split(text))
        {
            var lines = batch.Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
                .ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            if (!AllowedInsert().IsMatch(lines[0]))
            {
                problems.Add($"батч починається не з INSERT INTO #I18N|#UiChanged|#UiRemoved: «{lines[0]}»");
                continue;
            }

            var statement = lines.Skip(1).FirstOrDefault(l => StatementStart().IsMatch(l));
            if (statement is not null)
            {
                problems.Add($"у батчі ще одна інструкція: «{statement}»");
            }
        }

        return problems;
    }

    /// <summary>en-рядки MERGE каталогу (пролог) і рядки всіх порцій <c>#I18N</c>.</summary>
    private static List<(string Key, string Lang)> CatalogRows(string text)
    {
        var rows = new List<(string Key, string Lang)>();

        var start = text.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedSql.DisplayPath} немає блоку MERGE sys_ecr.UiString AS t.");
        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, "Блок MERGE sys_ecr.UiString AS t не закінчується.");
        rows.AddRange(EnRow().Matches(text[start..end]).Select(m => (m.Groups[1].Value, m.Groups[2].Value)));

        var pos = 0;
        while ((pos = text.IndexOf("INSERT INTO #I18N", pos, StringComparison.Ordinal)) >= 0)
        {
            var stop = text.IndexOf("OPTION (RECOMPILE)", pos, StringComparison.Ordinal);
            Assert.True(stop > pos, "Порція #I18N не закінчується OPTION (RECOMPILE).");
            rows.AddRange(TranslationRow().Matches(text[pos..stop]).Select(m => (m.Groups[1].Value, m.Groups[2].Value)));
            pos = stop;
        }

        return rows;
    }

    private static List<string> Duplicates(string text)
        => [.. CatalogRows(text)
            .GroupBy(r => r)
            .Where(g => g.Count() > 1)
            .Select(g => $"  {g.Key.Key} ({g.Key.Lang}) × {g.Count()}")];

    [GeneratedRegex(@"^[A-Z0-9]+-[a-z0-9-]+\.sql$")]
    private static partial Regex FeatureName();

    [GeneratedRegex(@"^INSERT\s+INTO\s+#(?:I18N|UiChanged|UiRemoved)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedInsert();

    [GeneratedRegex(@"^(?:;\s*)?(?:MERGE|UPDATE|DELETE|INSERT|DROP|CREATE|ALTER|EXEC|EXECUTE|TRUNCATE|DECLARE|SET|IF|BEGIN|WITH|GRANT|DENY|REVOKE|USE|PRINT|RAISERROR|THROW)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatementStart();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'(?:[^']|'')*'\s*,\s*[01]\s*\)")]
    private static partial Regex EnRow();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'(?:[^']|'')*'\s*\)")]
    private static partial Regex TranslationRow();
}
