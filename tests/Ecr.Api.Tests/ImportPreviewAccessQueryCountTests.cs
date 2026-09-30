// tests/Ecr.Api.Tests/ImportPreviewAccessQueryCountTests.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// P8 (перф-аудит): храповик звернень до БД на перегляді імпорту — їх
/// кількість НЕ залежить від числа таблиць у книзі.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти <c>ExcelImporter.PreviewAsync</c> кликав
/// <c>CanEditSliceAsync</c> на КОЖНУ таблицю, і кожен виклик коштував
/// ~5 звернень (екземпляр, знімок, умови доступу, правила періоду, рядки):
/// на типовому шаблоні з ~91 таблицею — сотні звернень на одне відкриття
/// перегляду. Міряється не абсолютне число, а ЗАЛЕЖНІСТЬ: книга на 3 і на 12
/// таблиць мусить коштувати однаково.
/// </para>
/// <para>
/// ⚠ Лічильник — <see cref="SqlClientCommandCounter"/> з
/// <c>AsyncLocal</c>-міткою на сам запит, той самий прийом, що й
/// <c>PatchCellsQueryCountTests</c>: фонові задачі в число не потрапляють.
/// </para>
/// <para>
/// ⚠ Теплий стан: перед заміром — такий самий перегляд (кеш метаданих,
/// профіль, штамп сеансу). Інакше різниця між документами була б різницею
/// холодного й теплого кешу, а не кількості таблиць.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportPreviewAccessQueryCountTests(SqlServerFixture sql)
{
    private const string Password = "Api-Import-Ratchet-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly JsonSerializerOptions MapOptions = new(JsonSerializerDefaults.Web);

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Перегляд_імпорту_коштує_однаково_на_3_і_на_12_таблиць()
    {
        var userName = $"p8_{Guid.NewGuid():N}"[..20];
        var roleId = await ArrangeUserAsync(userName).ConfigureAwait(true);

        // 3 таблиці на двох аркушах і 12 — на трьох: в обох книгах аркушів
        // більше одного, тож обидві йдуть тим самим пакетним шляхом.
        var small = await ArrangeDocumentAsync(roleId, [2, 1]).ConfigureAwait(true);
        var large = await ArrangeDocumentAsync(roleId, [4, 4, 4]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;
        using var client = await SignedInAsync(app, userName).ConfigureAwait(true);

        var texts = new ConcurrentQueue<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (Measuring.Value is not { Value: true })
            {
                return false;
            }

            texts.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });

        var (smallTotal, smallDetail) = await MeasureAsync(app, client, counter, texts, small).ConfigureAwait(true);
        var (largeTotal, largeDetail) = await MeasureAsync(app, client, counter, texts, large).ConfigureAwait(true);

        Assert.True(
            smallTotal == largeTotal,
            $"Перегляд книги на {small.Tables.Count} таблиць: {smallTotal} звернень, "
            + $"на {large.Tables.Count}: {largeTotal}. Різниця росте з кількістю таблиць.\n"
            + $"── {small.Tables.Count} ──\n{smallDetail}\n── {large.Tables.Count} ──\n{largeDetail}");
    }

    /// <summary>Розігрів і один замір перегляду книги документа.</summary>
    private static async Task<(int Total, string Detail)> MeasureAsync(
        EcrApiFactory app, HttpClient client, SqlClientCommandCounter counter, ConcurrentQueue<string> texts,
        Scenario scenario)
    {
        var book = BuildWorkbook(scenario);

        // ── Розігрів ─────────────────────────────────────────────────────
        await PreviewAsync(app, client, scenario, book).ConfigureAwait(false);

        // ── Замір ────────────────────────────────────────────────────────
        counter.Tally.Reset();
        texts.Clear();

        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        try
        {
            await PreviewAsync(app, client, scenario, book).ConfigureAwait(false);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        var seen = counter.Tally.Snapshot();

        // ⛔ Підлога: лічильник мусить бачити команди цього запиту — інакше
        // «однаково» означало б «фільтр усе відкинув в обох».
        counter.AssertObserved();
        Assert.True(seen.Total >= 1, "Лічильник не побачив жодного звернення перегляду.");

        return (seen.Total, Describe(seen, texts));
    }

    /// <summary>Перегляд книги; перевіряє, що diff справді побудовано на всі таблиці.</summary>
    private static async Task PreviewAsync(EcrApiFactory app, HttpClient client, Scenario scenario, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{scenario.DocumentId}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"перегляд: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;

        // ⛔ Кожна комірка кожної таблиці — зміна, і жодна не відхилена: інакше
        // «мало звернень» могло б означати «перегляд пропустив таблиці» або
        // «доступ відмовив усе» (тоді зріз і не потрібен).
        var expected = scenario.Tables.Sum(t => t.RowIds.Count * t.ColumnDefIds.Count);
        Assert.Equal(expected, root.GetProperty("changes").GetArrayLength());
        Assert.Equal(0, root.GetProperty("rejected").GetArrayLength());
    }

    /// <summary>Книга з картою: аркуш на таблицю, значення в кожній комірці.</summary>
    private static byte[] BuildWorkbook(Scenario scenario)
    {
        using var workbook = new XLWorkbook();
        var blocks = new List<ExcelTableBlock>();

        for (var i = 0; i < scenario.Tables.Count; i++)
        {
            var table = scenario.Tables[i];
            var sheetName = $"T{i}";
            var sheet = workbook.Worksheets.Add(sheetName);

            for (var r = 0; r < table.RowKeys.Count; r++)
            {
                for (var c = 0; c < table.ColumnDefIds.Count; c++)
                {
                    sheet.Cell(r + 2, c + 1).Value = (i * 100) + (r * 10) + c + 1;
                }
            }

            blocks.Add(new ExcelTableBlock(
                table.TableInstanceId, table.TableDefId, table.TableCode, sheetName, HeaderRow: 1,
                Columns: [.. table.ColumnDefIds.Select((id, c) =>
                    new ExcelColumnRef(id, table.ColumnCodes[c], Number: c + 1, IsCalculated: false, LookupRegistryDefId: null))],
                Rows: [.. table.RowKeys.Select((key, r) => new ExcelRowRef(key, Number: r + 2))]));
        }

        var map = new ExcelWorkbookMap(scenario.DocumentId, scenario.PeriodKey, scenario.TemplateVersionId, blocks);
        var mapSheet = workbook.Worksheets.Add(ExcelWorkbookMap.SheetName);
        mapSheet.Cell(1, 1).Value = JsonSerializer.Serialize(map, MapOptions);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Розклад звернень: категорії і тексти, скорочені до суті.</summary>
    private static string Describe(CommandTallySnapshot seen, IEnumerable<string> texts)
    {
        var text = new StringBuilder(seen.Format());
        text.AppendLine("Команди по черзі:");

        var i = 0;
        foreach (var command in texts)
        {
            var flat = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            text.AppendLine(CultureInfo.InvariantCulture, $"  {++i,2}. {(flat.Length > 160 ? flat[..160] + "…" : flat)}");
        }

        return text.ToString();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>Користувач із роллю, що має право переглядати й імпортувати.</summary>
    private async Task<int> ArrangeUserAsync(string userName)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"P8_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P8 import ratchet" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return role.Id;
    }

    /// <summary>
    /// Документ із таблицями на нових аркушах, активний проєкт, відкритий
    /// поточний період, грант <c>Write</c> на проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeDocumentAsync(int roleId, IReadOnlyList<int> tablesPerSheet)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // Поточний період: `PeriodStateJob` на старті закрив би минулий.
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 2).ConfigureAwait(false);
        var tables = await MultiTableDocument.AddTablesAsync(builder, document, tablesPerSheet).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        await using var db = builder.CreateContext();

        db.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        // ⚠ Одна вже заповнена комірка в книзі. Книга, у якій ЖОДНА таблиця
        // не має гарячих комірок, іде в архівний фолбек `ReadSlicesAsync`
        // (F-13), а той досі питає `arc.TableInstance` на кожен екземпляр —
        // окрема N+1 у сховищі комірок, не предмет цього храповика (названа
        // у звіті P8). Значення відрізняється від книжкового: зміна лишається.
        var first = tables[0];
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(periodKey), first.RowIds[0], first.ColumnDefIds[0]),
            first.TableDefId,
            new CellValueData { ValueNumeric = 0.5m }));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.DocumentId, document.TemplateVersionId, periodKey, tables);
    }

    private sealed record Scenario(
        long DocumentId, int TemplateVersionId, int PeriodKey, IReadOnlyList<ExtraTable> Tables);
}
