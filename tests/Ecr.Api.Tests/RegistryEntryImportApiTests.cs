// tests/Ecr.Api.Tests/RegistryEntryImportApiTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>POST /api/v1/registries/{code}/entries/import</c> — імпорт записів
/// довідника з CSV (директива №15, <c>BE-24</c> крок 3).
/// </summary>
/// <remarks>
/// ⛔ Колонки — коди полів ОПУБЛІКОВАНОГО опису плюс <c>code</c>. Валідація
/// значень — ТОЙ САМИЙ код, що ручний upsert
/// (<c>UpsertRegistryEntryHandler.ApplyValuesAsync</c>), і посилання
/// <c>Lookup</c>-поля на СУСІДНІЙ довідник резолвиться за бізнес-кодом ТИМ
/// САМИМ методом сховища (<c>FindEntryByCodeAsync</c>), яким upsert перевіряє
/// зайнятість коду — жодне правило тут не продубльоване.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryImportApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Import-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task DryRun_не_записує_в_базу_і_звітує_додавання()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);
        var before = await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true);

        var csv = $"code,Name,Amount,RefCode\r\nNEW{fixture.Tag},New entry,12.5,{fixture.OtherEntryCode}\r\n";
        var response = await ImportAsync(client, fixture.Code, csv, dryRun: true).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        Assert.False(body.GetProperty("applied").GetBoolean());
        Assert.Equal(1, body.GetProperty("added").GetInt32());
        Assert.Empty(body.GetProperty("errors").EnumerateArray());

        // ⚠ Головне твердження dryRun: ані запису, ані ревізії.
        Assert.False(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
        Assert.Equal(before, await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Валідні_рядки_додаються_і_оновлюються_а_ревізія_даних_росте()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);
        var before = await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true);

        // Один рядок додає новий запис, другий оновлює наявний (уже заведений
        // у SeedAsync без значення Amount).
        var csv = $"code,Name,Amount,RefCode\r\n"
            + $"NEW{fixture.Tag},New entry,12.5,{fixture.OtherEntryCode}\r\n"
            + $"{fixture.ExistingCode},Existing,99.75,\r\n";

        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(body.GetProperty("applied").GetBoolean());
        Assert.Equal(1, body.GetProperty("added").GetInt32());
        Assert.Equal(1, body.GetProperty("updated").GetInt32());
        Assert.Empty(body.GetProperty("errors").EnumerateArray());

        Assert.True(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
        Assert.Equal(99.75m, await AmountAsync(fixture.ExistingId).ConfigureAwait(true));
        Assert.True(
            await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true) > before,
            "DataRevision не зросла після імпорту, що щось змінив.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Зміна_поля_через_імпорт_пише_RegistryValueChanged_з_Id_нового_запису()
    {
        // ⛔ Закриває прогалину: ручне редагування (UpsertRegistryEntryHandler)
        // пише aud.SecurityEvent на КОЖНУ зміну поля, а імпорт CSV досі
        // відкидав RegistryValueFieldChange з ApplyValuesAsync мовчки — та сама
        // зміна лишала слід лише зробленою руками. МУТАЦІЙНИЙ ДОКАЗ: прибрати
        // цикл запису per-row подій в ImportRegistryEntriesHandler.HandleAsync
        // (після SaveChangesAsync у транзакційному блоці) — і цей тест першим
        // стає червоним.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        // Один рядок додає НОВИЙ запис (Id відомий лише після SaveChanges),
        // другий змінює Amount наявного запису — обидва мають дати подію.
        var csv = $"code,Name,Amount,RefCode\r\n"
            + $"NEW{fixture.Tag},New entry,12.5,{fixture.OtherEntryCode}\r\n"
            + $"{fixture.ExistingCode},Existing,99.75,\r\n";

        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(body.GetProperty("applied").GetBoolean());

        var newEntryId = await EntryIdAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true);

        // ⚠ Фільтр за registryDefId у DetailsJson, а не за ChangedByUserId:
        // fixture.DefinitionId унікальний на прогін (новий довідник у SeedAsync),
        // тож сторонні події з паралельних тестів це не зачепить. Весь
        // патерн — ОДНА інтерпольована діра (як у MethodologyVersionMaintenanceTests),
        // а не значення, вбудоване всередину рядкового літералу SQL: @-параметр
        // усередині N'...' лишився б текстом, а не підставленим значенням.
        var registryDefIdPattern = $"%\"registryDefId\":{fixture.DefinitionId},%";

        await using var db = new EcrDbContext(Options());
        var events = await db.Database
            .SqlQuery<string>(
                $"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = {UpsertRegistryEntryHandler.ValueChangedEventType} AND DetailsJson LIKE {registryDefIdPattern} ORDER BY Id")
            .ToListAsync().ConfigureAwait(true);

        // Обидва рядки змінили хоча б одне поле — дві події, по одній на запис.
        Assert.Equal(2, events.Count);

        var byEntryId = events
            .Select(json => JsonDocument.Parse(json).RootElement)
            .ToDictionary(e => e.GetProperty("entryId").GetInt64());

        Assert.True(byEntryId.ContainsKey(newEntryId));
        Assert.True(byEntryId.ContainsKey(fixture.ExistingId));

        var newEntryDetails = byEntryId[newEntryId];
        Assert.Equal(fixture.DefinitionId, newEntryDetails.GetProperty("registryDefId").GetInt32());
        var newEntryChanges = newEntryDetails.GetProperty("changes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("field").GetString()!);
        // Amount — CellDataType.Decimal: RawValue serialises його числом, не
        // рядком (на відміну від LIMIT/String у RegistryValueAuditTests).
        Assert.True(newEntryChanges.ContainsKey("Amount"));
        Assert.Equal(12.5m, newEntryChanges["Amount"].GetProperty("newValue").GetDecimal());

        var existingChanges = byEntryId[fixture.ExistingId].GetProperty("changes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("field").GetString()!);
        Assert.True(existingChanges.ContainsKey("Amount"));
        Assert.Equal(99.75m, existingChanges["Amount"].GetProperty("newValue").GetDecimal());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Рядок_без_фактичної_зміни_поля_не_пише_RegistryValueChanged()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        // Рядок називає лише код — жодного поля не передано (unchanged), тож
        // ApplyValuesAsync поверне порожній перелік змін.
        var csv = $"code,Name\r\n{fixture.ExistingCode},\r\n";

        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        // Нічого не додано й не оновлено (жодних переданих значень) — імпорт
        // виходить РАНІШЕ транзакції (added + updated == 0), тож і StructureChange
        // не пишеться. Головне твердження тесту нижче: RegistryValueChanged
        // теж відсутній.
        Assert.False(body.GetProperty("applied").GetBoolean());

        var count = await ValueChangedEventCountAsync(fixture.DefinitionId).ConfigureAwait(true);

        Assert.Equal(0, count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Помилка_одного_рядка_блокує_весь_файл()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Прибрати гейт «dryRun || errors.Count > 0» в
        // ImportRegistryEntriesHandler.HandleAsync (застосовувати рядки, що
        // пройшли, попри помилку в іншому) — і саме цей тест першим стає
        // червоним: другий, коректний рядок з'явиться в базі, хоча файл разом
        // мав відхилитися цілком.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);
        var before = await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true);

        // Другий рядок валідний сам по собі — саме тому й доводить «усе».
        var csv = $"code,Name,Amount,RefCode\r\n"
            + "BAD,Broken,not-a-number,\r\n"
            + $"NEW{fixture.Tag},New entry,12.5,\r\n";

        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(body.GetProperty("applied").GetBoolean());

        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, error.GetProperty("row").GetInt32());
        Assert.Equal("Amount", error.GetProperty("field").GetString());
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", error.GetProperty("messageKey").GetString());

        // Ані валідного, ані невалідного рядка в базі — і ревізія не зрушила.
        Assert.False(await EntryExistsAsync(fixture.DefinitionId, "BAD").ConfigureAwait(true));
        Assert.False(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
        Assert.Equal(before, await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Невідоме_поле_в_заголовку_дає_422_на_весь_файл()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var csv = $"code,Name,NoSuchField\r\nNEW{fixture.Tag},New entry,x\r\n";
        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0422.entriesCsvUnknownColumn", problem.GetProperty("messageKey").GetString());

        Assert.False(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Посилання_на_неіснуючий_запис_іншого_довідника_дає_помилку_рядка()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var csv = $"code,Name,Amount,RefCode\r\nNEW{fixture.Tag},New entry,1,NO-SUCH-CODE\r\n";
        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        Assert.False(body.GetProperty("applied").GetBoolean());
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal("RefCode", error.GetProperty("field").GetString());
        Assert.Equal("err.ECR-REG-0422.entryRefNotFound", error.GetProperty("messageKey").GetString());

        Assert.False(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Без_права_Registry_EditData_імпорт_дає_403()
    {
        // ⚠ Користувач із ЧИТАННЯМ довідників: інакше тест не розрізняв би
        // Registry.View і Registry.EditData.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View").ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var csv = $"code,Name\r\nNEW{fixture.Tag},New entry\r\n";
        var response = await ImportAsync(client, fixture.Code, csv, dryRun: false).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await EntryExistsAsync(fixture.DefinitionId, $"NEW{fixture.Tag}").ConfigureAwait(true));
    }

    private static async Task<HttpResponseMessage> ImportAsync(
        HttpClient client, string registryCode, string csv, bool dryRun)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "entries.csv");

        return await client
            .PostAsync(
                new Uri($"/api/v1/registries/{registryCode}/entries/import?dryRun={dryRun}", UriKind.Relative),
                content)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Довідник із трьома полями (обов'язкове рядкове, число, посилання на
    /// СУСІДНІЙ довідник) і одним наявним записом — плюс сусідній довідник
    /// із одним записом-ціллю для <c>Lookup</c>.
    /// </summary>
    private async Task<ImportFixture> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = new EcrDbContext(Options());

        var other = new RegistryDef(EcrCode.Create($"OTHR{tag}"), Name($"Other {tag}"), isTemporal: false);
        db.RegistryDefs.Add(other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var otherEntry = new RegistryEntry(other.Id, EcrCode.Create($"OE{tag}"), Name($"Other entry {tag}"));
        db.RegistryEntries.Add(otherEntry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var mine = new RegistryDef(EcrCode.Create($"REGI{tag}"), Name($"Registry {tag}"), isTemporal: false);
        db.RegistryDefs.Add(mine);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var nameField = new RegistryFieldDef(mine.Id, EcrCode.Create("Name"), Name("Name"), CellDataType.String, 0);
        nameField.Update(Name("Name"), 0, isRequired: true);

        var amountField = new RegistryFieldDef(mine.Id, EcrCode.Create("Amount"), Name("Amount"), CellDataType.Decimal, 1);
        var refField = new RegistryFieldDef(mine.Id, EcrCode.Create("RefCode"), Name("RefCode"), CellDataType.Lookup, 2);
        refField.PointTo(other.Id);

        db.RegistryFieldDefs.AddRange(nameField, amountField, refField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var existing = new RegistryEntry(mine.Id, EcrCode.Create($"E{tag}"), Name("Existing"));
        db.RegistryEntries.Add(existing);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Значення "Name" для наявного запису — інакше required-перевірка при
        // оновленні (де рядок не передає Name знову) відмовила б помилково.
        var existingName = new RegistryValue(existing, nameField.Id);
        existingName.Set(CellDataType.String, "Existing", null);
        db.RegistryValues.Add(existingName);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new ImportFixture(
            mine.Id, mine.Code, tag, other.Code, otherEntry.Code, existing.Code, existing.Id);
    }

    private async Task<bool> EntryExistsAsync(int registryDefId, string code)
    {
        await using var db = new EcrDbContext(Options());

        return await db.RegistryEntries
            .AsNoTracking()
            .AnyAsync(e => e.RegistryDefId == registryDefId && e.Code == code && !e.IsDeleted)
            .ConfigureAwait(false);
    }

    private async Task<long> EntryIdAsync(int registryDefId, string code)
    {
        await using var db = new EcrDbContext(Options());

        return await db.RegistryEntries
            .AsNoTracking()
            .Where(e => e.RegistryDefId == registryDefId && e.Code == code && !e.IsDeleted)
            .Select(e => e.Id)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<decimal?> AmountAsync(long entryId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.RegistryValues
            .AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId)
            .Join(
                db.RegistryFieldDefs.AsNoTracking().Where(f => f.Code == "Amount"),
                v => v.RegistryFieldDefId,
                f => f.Id,
                (v, f) => v.ValueNumeric)
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> DataRevisionAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.RegistryDefs
            .AsNoTracking()
            .Where(d => d.Id == registryDefId)
            .Select(d => d.DataRevision)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Скільки подій <c>RegistryValueChanged</c> лишив імпорт цього довідника —
    /// <c>registryDefId</c> у <c>DetailsJson</c> робить фільтр незалежним від
    /// паралельних тестів (той самий підхід, що <c>SecurityEventsWithReasonAsync</c>
    /// у <c>ConsistencyIssuesControllerTests</c>).
    /// </summary>
    private async Task<int> ValueChangedEventCountAsync(int registryDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM aud.SecurityEvent
            WHERE EventType = @type AND DetailsJson LIKE @pattern;
            """;
        command.Parameters.AddWithValue("@type", UpsertRegistryEntryHandler.ValueChangedEventType);
        command.Parameters.AddWithValue("@pattern", $"%\"registryDefId\":{registryDefId},%");

        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"regimp_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (permissions.Length > 0)
            {
                var role = new Role(
                    EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    Name("Registry import test"));

                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <param name="DefinitionId">Довідник, у який імпортуємо.</param>
    /// <param name="Code">Його код.</param>
    /// <param name="Tag">Спільний суфікс кодів цього прогону — для унікальних кодів нових записів.</param>
    /// <param name="OtherCode">Код сусіднього довідника-джерела для <c>Lookup</c>.</param>
    /// <param name="OtherEntryCode">Код наявного запису сусіднього довідника.</param>
    /// <param name="ExistingCode">Код запису, уже заведеного в цільовому довіднику.</param>
    /// <param name="ExistingId">Його ідентифікатор.</param>
    private sealed record ImportFixture(
        int DefinitionId, string Code, string Tag, string OtherCode, string OtherEntryCode,
        string ExistingCode, long ExistingId);
}
