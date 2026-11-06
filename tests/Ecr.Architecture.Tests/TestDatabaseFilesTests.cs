using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// 06.11 (coordinator): test databases never put files on <c>H:\EcrData</c> — the live <c>EcrLandUi</c> stand's disk, which
/// <c>EcrTest_*</c> twice brought to 0.1 GB. The folder comes from <c>ECR_TEST_DATAPATH</c>, else <c>C:\EcrTestData</c> when it
/// exists, else the server default; every <c>CREATE DATABASE</c> in the tests takes it through <see cref="TestDatabaseFiles"/>.
/// </summary>
/// <remarks>
/// Mutations (run, red): the old default <c>H:\EcrData</c> back in <c>Resolve</c>; <c>IsStandPath</c> without the sub-folder
/// check; a test's <c>CREATE DATABASE [{name}] COLLATE …</c> without <c>TestDatabaseFiles.Clause</c>.
/// </remarks>
public sealed class TestDatabaseFilesTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Шлях_зі_змінної_далі_стара_назва_далі_CEcrTestData_якщо_є_інакше_типовий_сервера()
    {
        Assert.Equal(@"D:\T", TestDatabaseFiles.Resolve(Env((TestDatabaseFiles.PathVariable, @"D:\T\")), _ => true));
        Assert.Equal(@"E:\Old", TestDatabaseFiles.Resolve(Env((TestDatabaseFiles.LegacyPathVariable, "E:/Old")), _ => false));
        Assert.Equal(
            @"D:\New",
            TestDatabaseFiles.Resolve(Env((TestDatabaseFiles.PathVariable, @"D:\New"), (TestDatabaseFiles.LegacyPathVariable, @"E:\Old")), _ => false));

        Assert.Equal(@"C:\EcrTestData", TestDatabaseFiles.Resolve(Env(), p => p == @"C:\EcrTestData"));
        // No variable, no C:\EcrTestData (CI container, a machine without the folder): the server default — and never H:.
        Assert.Equal(string.Empty, TestDatabaseFiles.Resolve(Env(), _ => false));
        Assert.Equal(string.Empty, TestDatabaseFiles.Resolve(Env(), p => p.StartsWith("H:", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(@"H:\EcrData")]
    [InlineData(@"h:\ecrdata\")]
    [InlineData("H:/EcrData/tests")]
    [InlineData(@" H:\EcrData\EcrTest ")]
    public void Шлях_живого_стенда_відмовляє_з_поясненням(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => TestDatabaseFiles.Resolve(Env((TestDatabaseFiles.PathVariable, configured)), _ => true));

        Assert.Contains(TestDatabaseFiles.StandDataPath, error.Message, StringComparison.Ordinal);
        Assert.False(TestDatabaseFiles.IsStandPath(@"H:\EcrDataOther"));
        Assert.False(TestDatabaseFiles.IsStandPath(@"C:\EcrTestData"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_CREATE_DATABASE_у_тестах_бере_файли_через_TestDatabaseFiles()
    {
        // DatabaseGrowthTrackerTests sizes its probe files explicitly in the shared fixture database's folder (which already
        // follows the rule); SqlServerFixture builds the clause itself through FilesClause → TestDatabaseFiles.Clause.
        var exempt = new[] { "DatabaseGrowthTrackerTests.cs", "SqlServerFixture.cs", "TestDatabaseFilesTests.cs" };
        var create = new Regex(@"CREATE DATABASE \[\{(?<var>\w+)\}\](?<rest>[^;\r\n]*)", RegexOptions.CultureInvariant);

        var offenders = Directory.EnumerateFiles(Path.Combine(SourceTree.Root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !exempt.Contains(Path.GetFileName(f)))
            .SelectMany(f => create.Matches(File.ReadAllText(f))
                .Where(m => !m.Groups["rest"].Value.StartsWith($"{{TestDatabaseFiles.Clause({m.Groups["var"].Value})}}", StringComparison.Ordinal))
                .Select(m => $"{Path.GetFileName(f)}: {m.Value.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "CREATE DATABASE without TestDatabaseFiles.Clause:\n" + string.Join('\n', offenders));

        var fixture = File.ReadAllText(Path.Combine(SourceTree.Root, "tests", "Ecr.TestKit", "SqlServerFixture.cs"));
        Assert.Contains("TestDatabaseFiles.Clause(DatabaseName)", fixture, StringComparison.Ordinal);
        Assert.Contains("TestDatabaseFiles.DropStaleAsync(", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("\"H:\\\\EcrData\"", fixture, StringComparison.Ordinal);
    }
}
