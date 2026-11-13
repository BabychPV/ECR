// tests/Ecr.Api.Tests/MentionCandidatesScanBudgetTests.cs
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.Discussions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// P-7 (аудит 2026-11-10, рішення PO 11.11 варіант А): кандидати згадок чату перевіряють доступ лише для
/// <see cref="TableDiscussionQueries.MentionScanBudget"/> (80) найближчих за алфавітом збігів, а не до 200.
/// </summary>
/// <remarks>
/// <para>
/// Межа видна поведінкою, без лічильника команд: перед видимим кандидатом стоять N невидимих збігів (користувачі БЕЗ
/// жодної ролі, порядок — <c>DisplayName</c>, потім <c>Id</c>, як у <c>SearchCandidatesAsync</c>). Видимий на позиції
/// 80 (нумерація з 0, тобто 81-й) уже поза бюджетом — відповідь порожня; на позиції 79 — у бюджеті й повертається.
/// Кожен збіг поза бюджетом — це рішення доступу (профіль + видимість + межі), яке код більше не робить.
/// </para>
/// <para>
/// ⛔ Мутація: повернути <c>skip &lt; 200</c> у циклі <c>MentionCandidatesAsync</c> — перебір доходить до 81-го збігу,
/// і перший кейс повертає видимого користувача (червоний). Обидва запити — від одного користувача, менше за межу
/// <c>search</c> (S-6), а дозволений запит обмежувач до БД не веде.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MentionCandidatesScanBudgetTests(SqlServerFixture sql)
{
    private const string Password = "Api-MentionScan-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кандидати_згадок_перевіряють_лише_80_найближчих_збігів()
    {
        Assert.Equal(80, TableDiscussionQueries.MentionScanBudget);

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);
        await using var db = builder.CreateContext();
        var tableCode = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(true);

        string[] discuss = ["Document.View", "Document.Discuss"];
        var writer = await NewUserAsync(db, $"mcw_{Guid.NewGuid():N}"[..20], document.ProjectId, discuss).ConfigureAwait(true);

        // Поза бюджетом: 80 невидимих (позиції 0..79), видимий — 81-й (позиція 80).
        var beyond = $"mcb{Guid.NewGuid():N}"[..12];
        await InvisibleAsync(db, beyond, 80).ConfigureAwait(true);
        var beyondVisible = await NewUserAsync(db, Name(beyond, 80), document.ProjectId, ["Document.View"]).ConfigureAwait(true);

        // У бюджеті: 79 невидимих (позиції 0..78), видимий — 80-й (позиція 79).
        var within = $"mcn{Guid.NewGuid():N}"[..12];
        await InvisibleAsync(db, within, 79).ConfigureAwait(true);
        var withinVisible = await NewUserAsync(db, Name(within, 79), document.ProjectId, ["Document.View"]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        using (var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = writer, password = Password }).ConfigureAwait(true))
        {
            Assert.True(login.IsSuccessStatusCode, $"Вхід {writer}: {login.StatusCode}: {app.ErrorsText}");
        }

        // Без фіксу (перебір до 200) тут повертався б видимий 81-й збіг.
        Assert.DoesNotContain(beyondVisible, await CandidatesAsync(client, document.DocumentId, tableCode, beyond).ConfigureAwait(true));

        // Межа не ламає підказку: видимий у межах 80 найближчих — повертається, невидимі — ні.
        Assert.Equal([withinVisible], await CandidatesAsync(client, document.DocumentId, tableCode, within).ConfigureAwait(true));
    }

    private static string Name(string tag, int position) => $"{tag}_{position.ToString("D3", CultureInfo.InvariantCulture)}";

    private static async Task<List<string>> CandidatesAsync(HttpClient client, long documentId, string tableCode, string query)
    {
        var text = await client.GetStringAsync(new Uri(
            $"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/tables/{Uri.EscapeDataString(tableCode)}/mention-candidates?q={query}",
            UriKind.Relative)).ConfigureAwait(false);

        return [.. JsonDocument.Parse(text).RootElement.EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];
    }

    /// <summary>Активні користувачі БЕЗ жодної ролі: збігаються з пошуком, але таблиці не бачать.</summary>
    private static async Task InvisibleAsync(EcrDbContext db, string tag, int count)
    {
        // Локальний запис без пароля забороняє CK_User_Provider; хеш один на всіх — PBKDF2 не рахується 80 разів.
        var hash = new PasswordHasher().Hash(Password);
        for (var i = 0; i < count; i++)
        {
            var user = new User($"{tag}{i.ToString("D3", CultureInfo.InvariantCulture)}", Name(tag, i), AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Користувач із роллю (права <paramref name="permissions"/>) і грантом Write на проєкт; ім'я для показу = логін.</summary>
    private static async Task<string> NewUserAsync(EcrDbContext db, string userName, int projectId, string[] permissions)
    {
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"MC_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Mention scan test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return userName;
    }
}
