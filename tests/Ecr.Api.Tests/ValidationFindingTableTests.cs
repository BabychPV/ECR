// tests/Ecr.Api.Tests/ValidationFindingTableTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Знахідка перевірки несе таблицю, у якій вона знайдена (`BE-04`).
/// </summary>
/// <remarks>
/// ⛔ Аркуш містить кілька таблиць, а <c>RowKey</c> унікальний лише в межах
/// СВОЄЇ таблиці. Доки відображення в <c>ValidationFindingDto</c> відкидало
/// <c>ValidationMessage.TableDefId</c>, панель зауважень отримувала адресу
/// <c>(RowKey, ColumnCode)</c>, за якою «стрибок до комірки» неоднозначний:
/// той самий ключ рядка є в кількох таблицях одного аркуша.
///
/// ⚠ Таблиць у сценарії саме ДВІ, і порушення є в кожній. Тест із однією
/// таблицею не довів би нічого: будь-яка підстановка сталої — і <c>0</c>, і
/// «завжди перша таблиця» — лишилася б зеленою. Тут падають обидві.
///
/// ⚠ Перевіряються ОБИДВА місця відображення: свіжий прогін
/// (<c>POST …/validate</c>) і збережений підсумок (<c>GET …/validation</c>).
/// Друге заразом доводить, що <c>wf.ValidationResult.MessagesJson</c> везе
/// таблицю крізь серіалізацію, — тобто міграція сховища для цього поля не
/// потрібна.
/// </remarks>
[Collection("SqlServer")]
public sealed class ValidationFindingTableTests(SqlServerFixture sql)
{
    private const string Password = "Api-Finding-Table-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.1")]
    public async Task Знахідка_у_другій_таблиці_аркуша_несе_її_TableDefId()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        // Передумова самого тесту названа явно: якби друга таблиця мала
        // ідентифікатор 0 або збігалася з першою, підстановка сталої лишилась
        // би непоміченою, і тест доводив би нуль.
        Assert.NotEqual(0, scenario.SecondTableDefId);
        Assert.NotEqual(scenario.FirstTableDefId, scenario.SecondTableDefId);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var validateUri = new Uri(
            $"/api/v1/documents/{scenario.DocumentId}/validate", UriKind.Relative);

        var response = await client
            .PostAsJsonAsync(validateUri, new { periodKey = scenario.PeriodKey })
            .ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST перевірки: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        AssertTables(body, scenario);

        // ── Те саме з ЧИТАННЯ збереженого підсумку ───────────────────────
        // ⚠ Це не повтор: тут значення проходить крізь
        // `JsonSerializer.Serialize(messages)` у `wf.ValidationResult` і
        // назад. Друге місце відображення (`LastValidation`) живе окремо від
        // першого, і одного з них зробити досить не можна.
        var stored = await client
            .GetAsync(new Uri(
                $"/api/v1/documents/{scenario.DocumentId}/validation?periodKey={scenario.PeriodKey}",
                UriKind.Relative))
            .ConfigureAwait(true);

        var storedBody = await stored.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            stored.IsSuccessStatusCode,
            $"GET підсумку: {stored.StatusCode}\n{storedBody}\n{app.ErrorsText}");

        AssertTables(storedBody, scenario);
    }

    /// <summary>Кожна знахідка названа своєю таблицею, а не однією на всіх.</summary>
    private static void AssertTables(string body, Scenario scenario)
    {
        var messages = JsonDocument.Parse(body).RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .ToList();

        // Правило рівня таблиці дає рівно одне порушення на таблицю, тож
        // повідомлень має бути два — по одному з кожної.
        var byRule = messages.ToDictionary(
            m => m.GetProperty("ruleCode").GetString()!,
            m => m.GetProperty("tableDefId").GetInt32(),
            StringComparer.Ordinal);

        Assert.True(
            byRule.ContainsKey(scenario.FirstRuleCode) && byRule.ContainsKey(scenario.SecondRuleCode),
            $"У відповіді немає обох порушень: {body}");

        // ⛔ Головне твердження: знахідка ДРУГОЇ таблиці несе саме її
        // ідентифікатор. Підстановка `0` (або будь-якої іншої сталої) валить
        // цей рядок, а разом із ним і рядок першої таблиці нижче.
        Assert.Equal(scenario.SecondTableDefId, byRule[scenario.SecondRuleCode]);
        Assert.Equal(scenario.FirstTableDefId, byRule[scenario.FirstRuleCode]);
    }

    /// <summary>Клієнт із чинним сеансом уже заведеного локального користувача.</summary>
    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Аркуш із ДВОМА таблицями, у кожної — правило рівня таблиці, яке
    /// порушується завжди.
    /// </summary>
    /// <remarks>
    /// ⚠ Правила саме рівня таблиці (<c>scope = 2</c>): вони не потребують ні
    /// рядків, ні значень, тож сценарій не тягне за собою запис комірок —
    /// предмет тесту в адресі знахідки, а не в тому, що саме порушено.
    ///
    /// ⚠ Другу таблицю будує сам тест, а не <c>TestDocumentBuilder</c>:
    /// будівник спільний, і розширювати його заради одного сценарію означало б
    /// змінювати фікстуру, якою користуються чужі тести.
    /// </remarks>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var firstRule = $"T1_{tag}";
        var secondRule = $"T2_{tag}";

        await using var db = builder.CreateContext();

        // ── Друга таблиця ТОГО САМОГО аркуша ─────────────────────────────
        var second = new TableDef(
            document.SheetDefId, EcrCode.Create($"TBL2_{tag}"), Name($"Table two {tag}"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(second);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.ColumnDefs.Add(new ColumnDef(
            second.Id, EcrCode.Create($"C1_{tag}"), Name("Col 1"), 1, CellDataType.Decimal));

        // `FALSE` — предикат, який не виконується ніколи: правило порушене
        // завжди, незалежно від даних.
        db.ValidationRules.Add(new ValidationRule(
            document.TableDefId, EcrCode.Create(firstRule), ValidationSeverity.Error,
            scope: 2, "FALSE", Name("перша таблиця")));
        db.ValidationRules.Add(new ValidationRule(
            second.Id, EcrCode.Create(secondRule), ValidationSeverity.Error,
            scope: 2, "FALSE", Name("друга таблиця")));

        await db.SaveChangesAsync().ConfigureAwait(false);

        // Екземпляр другої таблиці в тому самому документі й періоді —
        // інакше валідація її не побачить узагалі.
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None)
            .ConfigureAwait(false);

        db.TableInstances.Add(new TableInstance(
            document.PeriodKey, instanceId, document.DocumentId, second.Id, now));

        // ── Права: функціональне + грант на ресурс ───────────────────────
        var userName = $"valid_{Guid.NewGuid():N}"[..20];

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль СВОЯ на кожен прогін: база одна на всю збірку, і правка
        // вбудованої ролі розповзлася б на сусідні тести.
        var role = new Role(
            EcrCode.Create($"VALIDATOR_{Guid.NewGuid():N}"),
            Name("Validation reader"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Read));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, document.PeriodKey.Value,
            document.TableDefId, second.Id,
            firstRule, secondRule, userName);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Підготовлений сценарій: документ, його дві таблиці й читач.</summary>
    private sealed record Scenario(
        long DocumentId,
        int PeriodKey,
        int FirstTableDefId,
        int SecondTableDefId,
        string FirstRuleCode,
        string SecondRuleCode,
        string UserName);
}
