// tests/Ecr.Worker.Tests/WorkerPoolOptionsTests.cs

using System.Text.Json;
using Ecr.TestKit;
using Ecr.Worker.Isolation;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Ecr.Worker.Tests.WorkerProcess;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, P1): <c>Jobs:Workers:*</c> — дефолти цільового профілю
/// (24 ядра, 32 ГБ) і відмова старту на недійсному значенні.
/// </summary>
[Collection(WorkerProcessSerial.Name)]
public sealed class WorkerPoolOptionsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Без_конфігурації_діють_дефолти_цільового_профілю()
    {
        var problems = WorkerPoolOptions.TryLoad(new ConfigurationBuilder().Build(), out var options);

        Assert.Empty(problems);
        Assert.Equal(10, options.Count);
        Assert.Equal(2048, options.MemoryLimitMb);
        Assert.Equal(22528, options.JobMemoryLimitMb);
        Assert.Equal(TimeSpan.FromMinutes(30), options.MaxDuration);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Файл_налаштувань_воркера_оголошує_саме_ключі_й_дефолти_коду()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "worker.settings.json");
        using var document = JsonDocument.Parse(
            File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var declared = document.RootElement.GetProperty("Jobs").GetProperty("Workers")
            .EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        var known = typeof(WorkerPoolOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        // Ручка без читача або читач без ручки — два джерела правди (D14-06).
        Assert.Equal(known, declared);

        var problems = WorkerPoolOptions.TryLoad(
            new ConfigurationBuilder().AddJsonFile(path).Build(), out var fromFile);
        Assert.Empty(problems);
        var defaults = new WorkerPoolOptions();
        Assert.Equal(
            (defaults.Count, defaults.MemoryLimitMb, defaults.JobMemoryLimitMb, defaults.MaxDuration),
            (fromFile.Count, fromFile.MemoryLimitMb, fromFile.JobMemoryLimitMb, fromFile.MaxDuration));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData("Count", "0")]
    [InlineData("Count", "-1")]
    [InlineData("Count", "десять")]
    [InlineData("MemoryLimitMb", "32")]
    [InlineData("MemoryLimitMb", "2GB")]
    [InlineData("JobMemoryLimitMb", "1024")]
    [InlineData("MaxDuration", "0")]
    [InlineData("MaxDuration", "-00:05:00")]
    [InlineData("MaxDuration", "півгодини")]
    public void Недійсне_значення_називає_ключ_і_не_підміняється_мовчки(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new($"Jobs:Workers:{key}", value)])
            .Build();

        var problems = WorkerPoolOptions.TryLoad(configuration, out _);

        var problem = Assert.Single(problems);
        Assert.Contains($"Jobs:Workers:{key} = «{value}»", problem, StringComparison.Ordinal);
        Assert.Contains($"ECR_Jobs__Workers__{key}", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Наглядач_із_недійсною_конфігурацією_не_стартує_і_називає_ключ()
    {
        // ⚠ Перевірка конфігурації йде ДО перевірки платформи — цей тест
        // справжній і на Linux-CI.
        using var supervisor = Start(Command("--supervisor"), PoolEnvironment("0"));
        try
        {
            // ⛔ Без перевірки наглядач піднімає пул і живе — тоді тут таймаут.
            Assert.True(supervisor.WaitForExit(30_000), "наглядач із Count=0 мав зупинитися одразу");
            Assert.Equal(WorkerProgram.ExitInvalidConfiguration, supervisor.ExitCode);
            Assert.Contains("Jobs:Workers:Count = «0»", supervisor.StandardError.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            Kill(supervisor);
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData]
    [InlineData("--worker")]
    [InlineData("--supervisor", "--hang")]
    [InlineData("--child", "--eat-mb")]
    public void Невідомий_режим_чи_аргумент_дає_код_використання(params string[] args)
    {
        using var process = Start(Command(args));
        try
        {
            Assert.True(process.WaitForExit(30_000));
            Assert.Equal(WorkerProgram.ExitUsage, process.ExitCode);
        }
        finally
        {
            Kill(process);
        }
    }
}
