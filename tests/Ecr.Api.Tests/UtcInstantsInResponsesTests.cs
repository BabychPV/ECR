using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `V-13`: кожен момент часу у відповіді API несе зону — «Z» або зсув.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який стереже цей файл: <c>datetime2</c> з бази приходив із
/// <c>Kind=Unspecified</c>, JSON писав його без «Z», і браузер показував
/// перелік документів, History, задачі й зрізи зі зсувом на пояс оператора
/// (Київ −3 год). Перелік користувачів і журнал змін при цьому були правильні
/// — кожен латав себе поштучно, — тож на одному екрані жили дві шкали часу.
///
/// ⚠ Які поля перевіряти, вирішує НЕ цей файл, а контракт: імена всіх
/// властивостей із <c>format: date-time</c> у <c>openapi.snapshot.json</c>.
/// Нове поле-момент у будь-якому DTO потрапляє під перевірку саме, щойно
/// з'являється у відповіді котрогось із ендпоінтів нижче. Для кожного ендпоінта
/// названо й поля, які МУСЯТЬ бути в ньому непорожні: інакше «жодного
/// порушення» могло б означати «жодного значення».
///
/// ⚠ Наскрізно — справжній HTTP, справжня серіалізація, справжній SQL Server:
/// <c>Kind</c> губився саме між <c>datetime2</c> і матеріалізацією EF, і тест
/// на підмінному сховищі був би зелений на зламаному коді.
///
/// ⛔ Мутаційний доказ: прибрати <c>UtcDateTimeColumns.Apply(modelBuilder)</c> з
/// <c>EcrDbContext.OnModelCreating</c> — і червоніють документи, версії,
/// History, задачі, зрізи й картка шаблону (лишаються зеленими лише
/// користувачі — їх латає <c>UserStore</c> окремо).
/// </remarks>
[Collection("SqlServer")]
public sealed partial class UtcInstantsInResponsesTests(SqlServerFixture sql)
{
    private const string Password = "Api-Utc-Instants-2026!";

    /// <summary>Рядок моменту часу з позначкою зони: <c>…Z</c> або <c>…+05:00</c>.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ZonedInstant();

    private readonly string _tag = $"U{Guid.NewGuid():N}"[..12];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "V-13")]
    public async Task Кожен_момент_часу_у_відповідях_має_позначку_зони()
    {
        var instantFields = InstantFieldNames();

        // Сторож контракту: якщо колись схема перестане позначати моменти
        // `date-time`, перевіряти стане нічого — і тест мусить сказати це прямо.
        Assert.Contains("modifiedAt", instantFields);
        Assert.Contains("builtAt", instantFields);

        using var app = new EcrApiFactory(sql);

        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var (client, userId) = await SignedInAsync(app, document.ProjectId).ConfigureAwait(true);
        using var _ = client;

        var jobId = await ArrangeHistoryAsync(document, userId).ConfigureAwait(true);

        var d = document.DocumentId;
        var p = document.PeriodKey.Value;

        // Адреса → поля, які там мусять бути непорожні.
        var endpoints = new (string Path, string[] Required)[]
        {
            ($"/api/v1/documents?projectId={document.ProjectId}", ["createdAt", "modifiedAt"]),
            // Картка документа `modifiedAt` не віддає за задумом (`DocumentStore.FindAsync`).
            ($"/api/v1/documents/{d}", ["createdAt"]),
            ($"/api/v1/documents/{d}/versions?periodKey={p}", ["submittedAt"]),
            ($"/api/v1/documents/{d}/workflow/history?periodKey={p}", ["at"]),
            ("/api/v1/jobs?mine=true", ["createdAt", "updatedAt"]),
            ($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", ["createdAt"]),
            ($"/api/v1/reports/snapshots?projectId={document.ProjectId}", ["builtAt"]),
            ($"/api/v1/templates/{document.TemplateId}", ["createdAt"]),
            ("/api/v1/users?limit=200", []),
        };

        var violations = new List<string>();

        foreach (var (path, required) in endpoints)
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} на {path}: {body} {app.ErrorsText}");

            using var json = JsonDocument.Parse(body);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            Walk(json.RootElement, "$", instantFields, seen, violations, path);

            foreach (var field in required)
            {
                Assert.True(
                    seen.Contains(field),
                    $"{path}: поле «{field}» не прийшло непорожнім — перевіряти нема чого. Тіло: {body}");
            }
        }

        // ⛔ Повідомлення несе адресу, шлях у JSON і саме значення: «десь без Z»
        // не сказало б, яке сховище ще читає момент без зони.
        Assert.True(
            violations.Count == 0,
            "Моменти без зони:" + Environment.NewLine + string.Join(Environment.NewLine, violations));

        // ⚠ Друга половина доказу: «Z» стоїть на ТОМУ САМОМУ моменті, що в базі,
        // а не на зсунутому вдруге. Будівник пише 2026-01-15 10:00 UTC.
        var single = await client
            .GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/documents/{d}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(
            new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            DateTimeOffset.Parse(
                single.GetProperty("createdAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
    }

    /// <summary>Імена всіх властивостей схем із <c>format: date-time</c> у знімку контракту.</summary>
    private static HashSet<string> InstantFieldNames()
    {
        using var contract = JsonDocument.Parse(OpenApiSnapshot.Read());
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var schema in contract.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (IsDateTime(property.Value))
                {
                    names.Add(property.Name);
                }
            }
        }

        return names;
    }

    /// <summary><c>format: date-time</c> прямо або в одній із гілок <c>oneOf</c>/<c>anyOf</c> (nullable).</summary>
    private static bool IsDateTime(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time")
        {
            return true;
        }

        foreach (var branch in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (schema.TryGetProperty(branch, out var items) && items.EnumerateArray().Any(IsDateTime))
            {
                return true;
            }
        }

        return false;
    }

    private static void Walk(
        JsonElement node, string at, HashSet<string> instantFields,
        HashSet<string> seen, List<string> violations, string endpoint)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    var here = $"{at}.{property.Name}";

                    if (instantFields.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        seen.Add(property.Name);

                        var text = property.Value.GetString()!;
                        if (!ZonedInstant().IsMatch(text))
                        {
                            violations.Add($"{endpoint} {here} = \"{text}\"");
                        }
                    }

                    Walk(property.Value, here, instantFields, seen, violations, endpoint);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{at}[{index++}]", instantFields, seen, violations, endpoint);
                }

                break;
        }
    }

    /// <summary>Подання, перехід History, зріз і фонова задача — рядки, які читають ендпоінти вище.</summary>
    /// <returns>Ідентифікатор фонової задачі.</returns>
    private async Task<string> ArrangeHistoryAsync(TestDocument document, int userId)
    {
        await using var db = Context();
        var now = DateTime.UtcNow;

        db.SubmissionSnapshots.Add(new SubmissionSnapshot(
            document.DocumentId, document.SheetDefId, document.PeriodKey.Value, document.TemplateVersionId,
            "[]", numericMode: null, calendarMode: null, "[]", new byte[32], now, userId));

        db.ApprovalEvents.Add(new ApprovalEvent(
            document.DocumentId, document.SheetDefId, document.PeriodKey.Value,
            DocumentStatus.Draft, DocumentStatus.Submitted, ApprovalAction.Submit, userId, now));

        var version = await db.ReportVersions.AsNoTracking().OrderBy(v => v.Id).FirstAsync().ConfigureAwait(false);
        db.ReportSnapshots.Add(new ReportSnapshot(
            version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, now, userId));

        var jobId = $"{_tag}#{Guid.NewGuid():N}"[..40];
        var job = new JobProgress(jobId, _tag, now, userId);
        job.Queue(now);
        job.Report(10, "step", now);
        db.JobProgresses.Add(job);

        await db.SaveChangesAsync().ConfigureAwait(false);

        // Правка документа — той самий «дотик», що й `DocumentStore.TouchAsync`:
        // без автора правки картка віддає `modifiedAt: null`.
        await db.Documents
            .Where(x => x.Id == document.DocumentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ModifiedAt, now)
                .SetProperty(x => x.ModifiedByUserId, userId))
            .ConfigureAwait(false);

        return jobId;
    }

    /// <summary>Користувач із правами на все, що читає тест, і грантом Read на проєкт.</summary>
    private async Task<(HttpClient Client, int UserId)> SignedInAsync(EcrApiFactory app, int projectId)
    {
        var name = $"utc_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "UTC instants test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in new[]
                     {
                         "Document.View", "Report.ViewRegulatory", "Security.ManageUsers", "Template.View",
                     })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);

            userId = user.Id;
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return (client, userId);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
