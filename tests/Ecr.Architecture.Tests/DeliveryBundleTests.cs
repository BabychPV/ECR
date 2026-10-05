using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>tools/make-delivery-bundle.ps1</c> (A1-08): набір для першого тестування — один zip, з якого
/// <c>deploy-ecr.ps1</c> розгортає базу без клону репозиторію і без .NET SDK.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Артефакт CI <c>ecr-msi</c> містив лише <c>Ecr.msi</c>: без <c>migration.sql</c> і
/// <c>sql\</c> поруч із <c>deploy-ecr.ps1</c> пакований режим (<c>$isPackagedSchema</c>) не вмикався, і
/// перший користувач не мав чим створити схему. Тест запускає СПРАВЖНІЙ скрипт у <c>pwsh</c> на
/// підробленому MSI (довільні байти — скрипт MSI не розбирає, лише звіряє хеш) і готовому
/// <c>migration.sql</c>, а потім читає zip.
/// <para>
/// Мутації (прогнано локально): прибрати <c>sql/</c> зі складу → червоний <c>Набір_містить</c>; прибрати
/// звірку хешу MSI → червоний випадок <c>E03</c>; прибрати перевірку останньої міграції → червоний
/// <c>E06</c>.
/// </para>
/// </remarks>
public sealed class DeliveryBundleTests
{
    private const string Version = "0.0.7";
    private const string Top = "ECR-first-test-" + Version;

    private static string Tools => Path.Combine(SourceTree.Root, "tools");

    private static string SqlDir => Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");

    /// <summary>Остання міграція дерева — те, що <c>-E06</c> шукає в <c>migration.sql</c>.</summary>
    private static string LatestMigration() =>
        Directory.EnumerateFiles(Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Migrations"), "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n.Length > 15 && n[..14].All(char.IsDigit) && n[14] == '_' && !n.Contains('.', StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Last()!;

    private static string IdempotentMigration(string latest) => $"""
        IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') IS NULL
        BEGIN
            CREATE TABLE [dbo].[__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL);
        END;
        GO
        IF NOT EXISTS (
            SELECT * FROM [dbo].[__EFMigrationsHistory]
            WHERE [MigrationId] = N'{latest}'
        )
        BEGIN
            INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId]) VALUES (N'{latest}');
        END;
        GO
        """;

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Case : IDisposable
    {
        public Case()
        {
            Dir = Path.Combine(Path.GetTempPath(), "ecr-bundle-test-" + Guid.NewGuid().ToString("N"));
            MsiDir = Path.Combine(Dir, "ecr-msi", "en-US");
            Out = Path.Combine(Dir, "out");
            Directory.CreateDirectory(MsiDir);
            MsiBytes = RandomNumberGenerator.GetBytes(64 * 1024);
            File.WriteAllBytes(Path.Combine(MsiDir, "Ecr.msi"), MsiBytes);
            File.WriteAllText(Path.Combine(MsiDir, "Ecr.msi.sha256"), $"{Sha256(MsiBytes)}  Ecr.msi\n", Encoding.ASCII);
            Migration = Path.Combine(Dir, "migration.sql");
            File.WriteAllText(Migration, IdempotentMigration(LatestMigration()));
        }

        public string Dir { get; }

        public string MsiDir { get; }

        public string Out { get; }

        public string Migration { get; }

        public byte[] MsiBytes { get; }

        public string Zip => Path.Combine(Out, Top + ".zip");

        public (int Code, string Output) Run()
        {
            var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", Path.Combine(Tools, "make-delivery-bundle.ps1"),
                         "-MsiDir", Path.Combine(Dir, "ecr-msi"), "-Version", Version,
                         "-MigrationSql", Migration, "-Commit", "test-commit", "-OutDir", Out,
                     })
            {
                start.ArgumentList.Add(a);
            }

            using var p = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException("make-delivery-bundle.ps1 не завершився за 2 хв");
            }

            return (p.ExitCode, stdout.Result + "\n" + stderr.Result);
        }

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Набір_містить_пакований_розклад_deploy_і_суми_збігаються()
    {
        using var c = new Case();
        var (code, output) = c.Run();
        Assert.True(code == 0, $"код {code}\n{output}");
        Assert.True(File.Exists(c.Zip), output);

        using var zip = ZipFile.OpenRead(c.Zip);
        var files = zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }, StringComparer.Ordinal);

        Assert.All(files.Keys, k => Assert.StartsWith(Top + "/", k, StringComparison.Ordinal));
        Assert.DoesNotContain(files.Keys, k => k.Contains('\\', StringComparison.Ordinal));

        // deploy-ecr.ps1 бачить sql\ і migration.sql ПОРУЧ із собою — лише тоді dotnet ef не потрібен.
        foreach (var required in new[]
                 {
                     "Ecr.msi", "Ecr.msi.sha256", "deploy-ecr.ps1", "migration.sql", "verify-msi.ps1",
                     "QUICKSTART-FIRST-TEST.md", "docs/install-guide.md", "docs/TESTER-HANDOVER.md",
                     "BUNDLE-INFO.txt", "SHA256SUMS.txt",
                 })
        {
            Assert.True(files.ContainsKey($"{Top}/{required}"), $"у наборі немає {required}");
        }

        var sql = Directory.GetFiles(SqlDir, "*.sql").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var bundledSql = files.Keys.Where(k => k.StartsWith(Top + "/sql/", StringComparison.Ordinal))
            .Select(k => k[(Top.Length + 5)..]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(sql, bundledSql);
        Assert.Contains(files.Keys, k => k.StartsWith(Top + "/docs/release-notes/", StringComparison.Ordinal));

        Assert.Equal(File.ReadAllBytes(Path.Combine(Tools, "deploy-ecr.ps1")), files[$"{Top}/deploy-ecr.ps1"]);
        Assert.Equal(c.MsiBytes, files[$"{Top}/Ecr.msi"]);
        Assert.Equal($"{Sha256(c.MsiBytes)}  Ecr.msi", Encoding.ASCII.GetString(files[$"{Top}/Ecr.msi.sha256"]).Trim());

        // SHA256SUMS.txt покриває кожен файл набору, крім себе, і кожна сума правильна.
        var sums = Encoding.ASCII.GetString(files[$"{Top}/SHA256SUMS.txt"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split("  ", 2))
            .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);
        Assert.Equal(
            files.Keys.Select(k => k[(Top.Length + 1)..]).Where(k => k != "SHA256SUMS.txt").Order(StringComparer.Ordinal),
            sums.Keys.Order(StringComparer.Ordinal));
        Assert.All(sums, s => Assert.Equal(s.Value, Sha256(files[$"{Top}/{s.Key}"])));

        var info = Encoding.ASCII.GetString(files[$"{Top}/BUNDLE-INFO.txt"]);
        Assert.Contains("commit=test-commit", info, StringComparison.Ordinal);
        Assert.Contains($"latest_migration={LatestMigration()}", info, StringComparison.Ordinal);

        var zipSha = File.ReadAllText(c.Zip + ".sha256", Encoding.ASCII).Trim();
        Assert.Equal($"{Sha256(File.ReadAllBytes(c.Zip))}  {Top}.zip", zipSha);
    }

    public static TheoryData<string> Відмови => ["E01", "E02", "E03", "E05", "E06"];

    [Theory]
    [MemberData(nameof(Відмови))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Неповний_або_зіпсований_вхід_зупиняє_збірку_без_zip(string code)
    {
        using var c = new Case();
        switch (code)
        {
            case "E01": // другий .msi — незрозуміло, який пакувати
                File.WriteAllBytes(Path.Combine(c.Dir, "ecr-msi", "Other.msi"), [1, 2, 3]);
                break;
            case "E02":
                File.Delete(Path.Combine(c.MsiDir, "Ecr.msi.sha256"));
                break;
            case "E03": // MSI підмінено після обчислення еталона
                File.WriteAllBytes(Path.Combine(c.MsiDir, "Ecr.msi"), [.. c.MsiBytes, 0]);
                break;
            case "E05": // звичайний, НЕ ідемпотентний скрипт міграцій
                File.WriteAllText(c.Migration, $"CREATE TABLE [dbo].[__EFMigrationsHistory] ([MigrationId] nvarchar(150));\nINSERT INTO [dbo].[__EFMigrationsHistory] VALUES (N'{LatestMigration()}');\n");
                break;
            case "E06": // ідемпотентний, але з попереднього коміту
                File.WriteAllText(c.Migration, IdempotentMigration("20200101000000_Older"));
                break;
        }

        var (exit, output) = c.Run();
        Assert.True(exit != 0, $"очікувалась відмова {code}, а скрипт завершився з 0\n{output}");
        Assert.Contains($"[BUNDLE-{code}]", output, StringComparison.Ordinal);
        Assert.False(File.Exists(c.Zip), "після відмови zip не має лишатися");
    }
}
