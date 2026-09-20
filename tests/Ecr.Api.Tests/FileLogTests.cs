using System.Net;
using System.Text.Json;
using Ecr.Api.Observability;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Файловий журнал (<c>D14-09</c>, рішення №10 директиви №15).</summary>
/// <remarks>
/// ⚠ <see cref="EcrApiFactory"/> прибирає ВСІХ постачальників журналу, тому
/// файловий тут повертається явно — тим самим <c>AddEcrFileLog()</c>, що й у
/// <c>Program.cs</c>, — і лише в тимчасову теку.
/// </remarks>
[Collection("SqlServer")]
public sealed class FileLogTests(SqlServerFixture sql)
{
    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D14-09")]
    public async Task Запит_лишає_у_файлі_рядок_із_CorrelationId_і_UserId_а_факти_називають_теку()
    {
        var directory = Directory.CreateTempSubdirectory("ecr-log-").FullName;
        try
        {
            string text;
            using (var app = new EcrApiFactory(sql))
            using (var host = WithFileLog(app, directory))
            {
                // Користувач БЕЗ права: 403 пише `ExceptionHandlingMiddleware` — рядок,
                // що гарантовано є на рівні Information.
                using var stranger = await SystemHealthControllerTests.SignedInAsync(sql, host);
                using var request = new HttpRequestMessage(HttpMethod.Get, Facts);
                request.Headers.Add("X-Correlation-Id", "filelog-test-7f3a");
                Assert.Equal(HttpStatusCode.Forbidden, (await stranger.SendAsync(request)).StatusCode);

                using var admin = await SystemHealthControllerTests.SignedInAsync(sql, host, "System.ViewHealth");
                using var facts = JsonDocument.Parse(await admin.GetStringAsync(Facts));
                Assert.Equal(directory, facts.RootElement.GetProperty("logDirectory").GetString());

                text = ReadLog(directory);            }

            var line = Assert.Single(
                text.Split('\n'), l => l.Contains("ECR-AUTH", StringComparison.Ordinal)
                    && l.Contains("filelog-test-7f3a", StringComparison.Ordinal));

            // ⚠ Саме ПОЗИЦІЇ шаблону, а не згадка в тексті повідомлення: за
            // ідентифікатором у дужках журнал і шукають.
            Assert.Contains("[filelog-test-7f3a]", line, StringComparison.Ordinal);
            Assert.Matches(@"\[uid:\d+\]", line);

            // ⚠ Рядок вище несе ідентифікатор ще й у тексті повідомлення. Рядки EF
            // — ні: до них він доходить лише зі scope `CorrelationIdMiddleware`.
            Assert.Contains(text.Split('\n'), l =>
                l.Contains("EntityFrameworkCore", StringComparison.Ordinal)
                && l.Contains("[filelog-test-7f3a]", StringComparison.Ordinal));
            Assert.Contains($"[{Environment.MachineName}]", line, StringComparison.Ordinal);

            // ⛔ Ні пароля, ні рядка підключення — на жодному рівні.
            Assert.DoesNotContain("Api-Health-Facts-2026!", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Integrated Security", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// ⚠ Прибирання — не предмет тесту. У повному прогоні свіжий файл у <c>%TEMP%</c>
    /// ще мить тримає сторонній процес (двічі з чотирьох прогонів: «being used by
    /// another process»), і червоний тест через це нічого не каже про журнал.
    /// </summary>
    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D14-09")]
    public async Task Недоступна_тека_не_валить_старт_а_лишає_попередження()
    {
        // Тека «всередині файлу»: створити її не може жодна ОС і жоден обліковий запис.
        var blocker = Path.GetTempFileName();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var host = WithFileLog(app, Path.Combine(blocker, "logs"));
            using var client = host.CreateClient();

            var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.True(live.IsSuccessStatusCode, $"{live.StatusCode}: {app.ErrorsText}");

            var status = host.Services.GetRequiredService<FileLogStatus>();
            Assert.Null(status.Directory);
            Assert.Contains(app.ServerLog, l =>
                l.Contains("Файловий журнал вимкнено", StringComparison.Ordinal)
                && l.Contains(blocker, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D14-09")]
    public void Тека_розгортається_а_строк_зберігання_тридцять_файлів()
    {
        Assert.Null(FileLog.ResolveDirectory("  "));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), FileLog.ResolveDirectory("logs"));

        Environment.SetEnvironmentVariable("ECR_TEST_LOGROOT", Path.GetTempPath());
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "ECR", "logs"),
            FileLog.ResolveDirectory(Path.Combine("%ECR_TEST_LOGROOT%", "ECR", "logs")));

        // ⛔ Число — вимога (рішення №10: 30 днів), тому літералом.
        Assert.Equal(30, new FileLogOptions().RetainedFiles);
    }

    private static WebApplicationFactory<Program> WithFileLog(EcrApiFactory app, string directory)
        => app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddEcrFileLog());
            services.PostConfigure<FileLogOptions>(options => options.Directory = directory);
        }));

    private static string ReadLog(string directory)
    {
        var file = Assert.Single(Directory.GetFiles(directory, "ecr-*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
