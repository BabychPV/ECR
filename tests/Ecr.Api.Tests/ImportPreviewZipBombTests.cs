// tests/Ecr.Api.Tests/ImportPreviewZipBombTests.cs
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Api.Controllers;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `S10` (аудит безпеки) наскрізно: перегляд імпорту відхиляє zip-bomb і
/// пошкоджений пакет відмовою <c>422 ECR-IMP-0422</c> з ключем
/// <c>notAWorkbook</c>, а ендпоінт оголошує стелю тіла запиту.
/// </summary>
/// <remarks>
/// ⚠ Поведінку самого запобіжника (межі, вердикти, відсутність розгортання)
/// доводить <c>XlsxZipBombTests</c> в Adapters; тут — що відмова доходить до
/// клієнта тим самим кодом і ключем, що й «не книга», а не 500 і не
/// «немає карти» після розбору.
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportPreviewZipBombTests(SqlServerFixture sql)
{
    private const string Password = "Api-Xlsx-Bomb-2026!";

    private const int MiB = 1024 * 1024;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S10")]
    public void Перегляд_імпорту_оголошує_стелю_тіла_запиту_Kestrel()
    {
        var action = typeof(DocumentsController)
            .GetMethod(nameof(DocumentsController.ImportPreview), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(action);

        var limit = action!.GetCustomAttributesData()
            .SingleOrDefault(a => a.AttributeType == typeof(RequestSizeLimitAttribute));

        Assert.NotNull(limit);

        // ⛔ Явне число — стандартна межа Kestrel; не більше за неї.
        Assert.Equal(30_000_000L, Convert.ToInt64(limit!.ConstructorArguments[0].Value, null));

        // Буфер потоку без позиціювання в запобіжнику — з тією самою стелею.
        Assert.Equal(DocumentsController.MaxImportBodyBytes, XlsxSafetyGate.MaxPackageBytes);
    }

    [Fact(Timeout = 180_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S10")]
    public async Task Zip_bomb_і_пошкоджений_пакет_дають_422_notAWorkbook()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // ── Бомба: справжня книга, аркуш роздуто до 48 МБ (стиснуто — КБ). ──
        // До фіксу: 422 `noMapSheet` — тобто книгу РОЗІБРАНО до кінця.
        var bomb = InflatedWorkbook(48L * MiB);
        await AssertNotAWorkbookAsync(app, client, s.DocumentId, bomb).ConfigureAwait(true);

        // ── Пошкоджений zip: обрізаний центральний каталог. ──
        var normal = InflatedWorkbook(0);
        await AssertNotAWorkbookAsync(app, client, s.DocumentId, normal[..(normal.Length / 2)]).ConfigureAwait(true);
    }

    private static async Task AssertNotAWorkbookAsync(
        EcrApiFactory app, HttpClient client, long documentId, byte[] book)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        using var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{documentId}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-IMP-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", problem.GetProperty("messageKey").GetString());
    }

    /// <summary>
    /// Справжня книга без карти; XML першого аркуша роздуто пробілами на
    /// <paramref name="padding"/> байтів — потоково, шматками.
    /// </summary>
    private static byte[] InflatedWorkbook(long padding)
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Data").Cell(1, 1).Value = 1;

        using var source = new MemoryStream();
        workbook.SaveAs(source);
        source.Position = 0;

        using var output = new MemoryStream();
        using (var original = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var inflated = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                using var target = inflated.CreateEntry(entry.FullName, CompressionLevel.SmallestSize).Open();
                using var from = entry.Open();

                if (!entry.FullName.Equals("xl/worksheets/sheet1.xml", StringComparison.Ordinal))
                {
                    from.CopyTo(target);
                    continue;
                }

                using var reader = new StreamReader(from, Encoding.UTF8);
                var xml = reader.ReadToEnd();
                var close = xml.LastIndexOf("</", StringComparison.Ordinal);

                target.Write(Encoding.UTF8.GetBytes(xml[..close]));

                var chunk = new byte[MiB];
                Array.Fill(chunk, (byte)' ');
                for (long written = 0; written < padding; written += chunk.Length)
                {
                    target.Write(chunk, 0, (int)Math.Min(chunk.Length, padding - written));
                }

                target.Write(Encoding.UTF8.GetBytes(xml[close..]));
            }
        }

        return output.ToArray();
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

    /// <summary>Документ і користувач із правом імпорту та грантом на проєкт.</summary>
    private async Task<(long DocumentId, string UserName)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var userName = $"s10_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль своя на кожен прогін: база спільна на всю збірку.
        var role = new Role(
            EcrCode.Create($"S10_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "S10 import bomb" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.Import"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (document.DocumentId, userName);
    }
}
