// tests/Ecr.Api.Tests/Security/DenyReadTests.ImportPreview.cs
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S6: перегляд імпорту не відповідає на питання «чи дорівнює приховане
/// значення тому, що я вписав у книгу».
/// </summary>
/// <remarks>
/// ⛔ До фіксу перегляд порівнював книгу з поточними значеннями ДО рішення про
/// доступ: число в прихованій колонці чи таблиці, що збігалося зі збереженим,
/// давало «нічого не зміниться», а інше — відмову правами (для прихованої
/// таблиці ще й з її справжнім кодом і назвою зі знімка). Підставляючи числа,
/// користувач читав приховане, маючи лише право імпорту.
/// </remarks>
public sealed partial class DenyReadTests
{
    private const string FileTableCode = "FILE_DT";

    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Перегляд_імпорту_не_є_оракулом_прихованих_значень()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await GrantImportAsync(s).ConfigureAwait(true);

        var guessed = Book(s, DeniedColumn, DeniedTable);
        var missed = Book(s, DeniedColumn + 1, DeniedTable + 1);

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var hit = await PreviewAsync(app, reader, s, guessed).ConfigureAwait(true);
            var miss = await PreviewAsync(app, reader, s, missed).ConfigureAwait(true);

            // ⛔ Головне: «вгадав» і «не вгадав» — однаковий перегляд.
            Assert.Equal(Shape(hit), Shape(miss));

            foreach (var body in new[] { hit, miss })
            {
                var rejected = Rejected(body);

                // Прихована колонка — відмова правами незалежно від значення.
                Assert.Contains(rejected, r => r.Column == s.ColumnCodes[2] && r.Key == "deny.NoGrant");

                // Прихована таблиця — як неіснуючий екземпляр, з кодом із ФАЙЛУ.
                Assert.Contains(rejected, r => r.Column == FileTableCode && r.Key == ImportMessageKeys.InstanceMissing);

                foreach (var secret in new[] { Digits(DeniedColumn), Digits(DeniedTable), s.DeniedTable.TableCode })
                {
                    Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
                }
            }
        }

        // Регресія: без заборон порівняння як було — збіг мовчить, розбіжність
        // названа, і прихована для reader таблиця — своїм справжнім кодом.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var hit = await PreviewAsync(app, plain, s, guessed).ConfigureAwait(true);
            var miss = await PreviewAsync(app, plain, s, missed).ConfigureAwait(true);

            Assert.DoesNotContain(Rejected(hit), r => r.Column == s.ColumnCodes[2]);
            Assert.DoesNotContain(s.DeniedTable.TableCode, hit, StringComparison.Ordinal);

            Assert.Contains(s.ColumnCodes[2], miss, StringComparison.Ordinal);
            Assert.Contains(s.DeniedTable.TableCode, miss, StringComparison.Ordinal);
            Assert.DoesNotContain(Rejected(miss), r => r.Key == ImportMessageKeys.InstanceMissing);
        }
    }

    /// <summary>Роль із правом імпорту для обох користувачів сценарію.</summary>
    private async Task GrantImportAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var importer = NewRole("DNR_I");
        db.Roles.Add(importer);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(importer.Id, "Document.Import"));
        foreach (var name in new[] { s.Reader, s.Plain })
        {
            var user = db.Users.Single(u => u.UserName == name);
            db.RoleAssignments.Add(new RoleAssignment(importer.Id, user.Id, null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Книга: видима C2 і прихована C3 основної таблиці (рядок 1) і прихована
    /// таблиця (обидва рядки) — з картою, де код прихованої таблиці вигаданий.
    /// </summary>
    private static byte[] Book(Scenario s, decimal deniedColumn, decimal deniedTable)
    {
        using var workbook = new XLWorkbook();

        var main = workbook.Worksheets.Add("MAIN");
        main.Cell(2, 1).Value = Visible;
        main.Cell(2, 2).Value = deniedColumn;

        var other = workbook.Worksheets.Add("DT");
        other.Cell(2, 1).Value = deniedTable;
        other.Cell(3, 1).Value = 5;

        var blocks = new List<ExcelTableBlock>
        {
            new(
                s.Doc.TableInstanceId, s.Doc.TableDefId, "MAIN_FILE", "MAIN", HeaderRow: 1,
                Columns:
                [
                    new ExcelColumnRef(s.Doc.ColumnDefIds[1], s.ColumnCodes[1], 1, false, null),
                    new ExcelColumnRef(s.Doc.ColumnDefIds[2], s.ColumnCodes[2], 2, false, null),
                ],
                Rows: [new ExcelRowRef(s.RowKey, 2)]),
            new(
                s.DeniedTable.TableInstanceId, s.DeniedTable.TableDefId, FileTableCode, "DT", HeaderRow: 1,
                Columns: [new ExcelColumnRef(s.DeniedTable.ColumnDefIds[0], "FILE_C", 1, false, null)],
                Rows:
                [
                    new ExcelRowRef(s.DeniedTable.RowKeys[0], 2),
                    new ExcelRowRef(s.DeniedTable.RowKeys[1], 3),
                ]),
        };

        var map = new ExcelWorkbookMap(s.Doc.DocumentId, s.Doc.PeriodKey.Value, s.Doc.TemplateVersionId, blocks);
        workbook.Worksheets.Add(ExcelWorkbookMap.SheetName).Cell(1, 1).Value =
            JsonSerializer.Serialize(map, MapJson);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static async Task<string> PreviewAsync(EcrApiFactory app, HttpClient client, Scenario s, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        using var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"перегляд: {response.StatusCode}\n{body}\n{app.ErrorsText}");
        return body;
    }

    private static List<(string Column, string? Key)> Rejected(string body)
        => [.. JsonDocument.Parse(body).RootElement.GetProperty("rejected").EnumerateArray()
            .Select(r => (
                r.GetProperty("columnCode").GetString()!,
                r.TryGetProperty("messageKey", out var key) ? key.GetString() : null))];

    /// <summary>Перегляд без токена — те, що бачить людина.</summary>
    private static string Shape(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        return root.GetProperty("changes").GetRawText() + "\n" + root.GetProperty("rejected").GetRawText();
    }
}
