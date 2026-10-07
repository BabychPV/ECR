// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Race.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// L6-02 (аудит 2026-10-03): перенос версії шаблону серіалізований із записом комірок —
/// два з'єднання, справжній HTTP і справжній SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Що було. Перенос блокував лише рядок <c>doc.Project</c>. Правка, що комітилася між
/// його плануванням (у колонці <c>C3</c>, яку нова версія прибирає, значень немає — режим
/// <c>Safe</c> дозволяє) і <c>ApplyAsync</c> (видаляє комірки колонок без відповідника),
/// отримувала 200, а її значення зникало разом із колонкою.
///
/// ⚠ Як відтворено. Декоратор сховища зупиняє перенос перед <c>ApplyAsync</c> — план уже
/// пораховано, блокування переносу взяті. Друге з'єднання шле PATCH у <c>C3</c>. Тест чекає,
/// доки PATCH або завершиться (дефект: він не чекає ні на що), або стане в чергу на
/// блокування структури документа (фікс), і лише тоді відпускає перенос.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private static readonly TimeZoneInfo AtyrauZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перенос_версії_не_губить_правку_що_прийшла_під_час_переносу()
    {
        // ⚠ Період — поточний місяць майданчика: інакше він закритий за часом і правку
        // відхилили б права (`PeriodClosed`), а не перенос.
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AtyrauZone));
        var s = await ArrangeAsync(
            Target.DropsC3AddsC4AndRow, extraPermission: "Document.View",
            periodKey: (siteToday.Year * 100) + siteToday.Month).ConfigureAwait(true);
        await OpenForEditingAsync(s).ConfigureAwait(true);
        var pause = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new PausingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()), pause))));

        using var migrator = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var editor = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var rowKey = $"R1_{s.Tag}";
        var baseVersion = await RowVersionAsync(editor, s, rowKey).ConfigureAwait(true);

        // ── З'єднання 1: перенос доходить до `ApplyAsync` і стоїть ──
        var migration = PostAsync(migrator, s, "Safe", dryRun: false);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        // ── З'єднання 2: правка колонки, яку нова версія прибирає ──
        var patch = editor.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.Doc.PeriodKey.Value,
                origin = "UserEdit",
                rows = new object[]
                {
                    new { rowKey, baseVersion, cells = new object[] { new { columnCode = $"C3_{s.Tag}", value = 7m } } },
                },
            });

        var blocked = await WaitUntilBlockedOrDoneAsync(patch).ConfigureAwait(true);
        pause.Release.TrySetResult();

        using var migrated = await migration.ConfigureAwait(true);
        var migratedBody = await migrated.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(migrated.StatusCode == HttpStatusCode.OK, $"Перенос: {migrated.StatusCode}: {migratedBody} {baseApp.ErrorsText}");

        using var patched = await patch.ConfigureAwait(true);
        var patchedBody = await patched.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Предмет тесту: прийнята правка не губиться. Колонки C3 після переносу немає,
        // тож «прийнята» тут могла означати лише втрату — правка мусить отримати відмову
        // «структуру змінено», а не 200.
        Assert.True(
            patched.StatusCode == HttpStatusCode.Conflict,
            $"Правка під час переносу відповіла {patched.StatusCode}, а її значення зникло з колонкою: {patchedBody}");
        var problem = JsonDocument.Parse(patchedBody).RootElement;
        Assert.Equal("ECR-DOC-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal(DocumentStructure.StructureChangedKey, problem.GetProperty("messageKey").GetString());

        // ⚠ Відмова саме тому, що правка ЧЕКАЛА на перенос, — а не через збіг часу.
        Assert.True(blocked, "Правка не стала в чергу на блокування структури документа.");
    }

    /// <summary>
    /// Чекає, доки запит завершиться або хтось стане в чергу на блокування структури
    /// документа; <c>true</c> — друге.
    /// </summary>
    private async Task<bool> WaitUntilBlockedOrDoneAsync(Task patch)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (patch.IsCompleted)
            {
                return false;
            }

            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT'
                  AND resource_description LIKE '%doc-struc%'
                """;
            if ((int)(await command.ExecuteScalarAsync().ConfigureAwait(false))! > 0)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Проєкт активний, період відкритий — комірки можна правити.</summary>
    private async Task OpenForEditingAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var now = DateTime.UtcNow;

        var project = await db.Projects.FirstAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == s.Doc.ProjectId && p.PeriodKeyValue == s.Doc.PeriodKey.Value)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, AtyrauZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<string> RowVersionAsync(HttpClient client, Scenario s, string rowKey)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Doc.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"Зріз: {response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == rowKey)
            .GetProperty("rowVersion").GetString()!;
    }

    private static async Task<HttpClient> SignedInAsync(
        WebApplicationFactory<Program> app, string userName, EcrApiFactory errors)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {errors.ErrorsText}");
        return client;
    }

    /// <summary>Точка зупинки переносу перед <c>ApplyAsync</c>.</summary>
    private sealed class ApplyPause
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Справжнє сховище переносу, що стоїть перед <c>ApplyAsync</c>, доки тест не відпустить.</summary>
    private sealed class PausingMigrationStore(IDocumentVersionMigrationStore inner, ApplyPause pause)
        : IDocumentVersionMigrationStore
    {
        public Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct)
            => inner.LockProjectVersionAsync(projectId, ct);

        public Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct)
            => inner.ListDocumentIdsAsync(projectId, ct);

        public Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct)
            => inner.ReadScopeAsync(projectId, ct);

        public Task<int> CountDenyGrantsAsync(
            IReadOnlyCollection<int> sheetIds, IReadOnlyCollection<int> tableIds, IReadOnlyCollection<int> columnIds,
            CancellationToken ct)
            => inner.CountDenyGrantsAsync(sheetIds, tableIds, columnIds, ct);

        public Task<int> CountUnmappedBindingsAsync(
            int sourceVersionId, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct)
            => inner.CountUnmappedBindingsAsync(sourceVersionId, columnMap, ct);

        public Task<GrantedUsers> ListUsersWithGrantsAsync(VersionMigrationPlan plan, int limit, CancellationToken ct)
            => inner.ListUsersWithGrantsAsync(plan, limit, ct);

        public async Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct)
        {
            pause.Reached.TrySetResult();
            await pause.Release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            await inner.ApplyAsync(projectId, targetVersionId, plan, ct).ConfigureAwait(false);
        }
    }
}
