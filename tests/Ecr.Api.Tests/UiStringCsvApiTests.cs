using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>`BE-13` ч.2: CSV перекладу через HTTP — файл, multipart і право.</summary>
[Collection("SqlServer")]
public sealed class UiStringCsvApiTests(SqlServerFixture sql)
{
    private const string Permission = "System.ManageLocalization";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-13")]
    public async Task Експорт_віддає_CSV_з_BOM_а_імпорт_dryRun_звітує_без_запису()
    {
        using var app = new EcrApiFactory(sql);
        var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission);

        var response = await client.GetAsync(new Uri("/api/v1/ui-strings/export.csv?lang=ru", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.StartsWith("key,scope,en,ru,updatedAt\r\n", Encoding.UTF8.GetString(bytes[3..]), StringComparison.Ordinal);

        var revision = await RevisionAsync(client);
        var report = await ImportAsync(client, "key,ru\r\ncommon.save,Сохранить-тест\r\nno.such.key,X\r\n");

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var body = JsonDocument.Parse(await report.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("applied").GetBoolean());
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal((3, "err.ECR-REQ-0422.uiStringUnknownKey"),
            (error.GetProperty("row").GetInt32(), error.GetProperty("messageKey").GetString()));
        Assert.Equal(revision, await RevisionAsync(client));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-13")]
    public async Task Без_права_локалізації_403_на_обидва_маршрути()
    {
        using var app = new EcrApiFactory(sql);
        var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.Edit");

        var export = await client.GetAsync(new Uri("/api/v1/ui-strings/export.csv?lang=ru", UriKind.Relative));
        var import = await ImportAsync(client, "key,ru\r\n");

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (export.StatusCode, import.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-13")]
    public async Task Файл_cp1251_відхиляється_422_і_каталог_не_змінюється()
    {
        // S1-03: «Зберегти як CSV» в Excel з ru/kk-локаллю пише cp1251. Нестрогий UTF-8 читав кирилицю як «�»,
        // обробник цього не бачив, і не-dryRun імпорт перезаписував переклад мови для всіх.
        using var app = new EcrApiFactory(sql);
        var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission);
        var revision = await RevisionAsync(client);

        // "key,ru\r\ncommon.save,Сохранить" у cp1251 (кирилиця — одиночні байти 0xC0..0xFF, невалідний UTF-8).
        var cp1251 = Encoding.ASCII.GetBytes("key,ru\r\ncommon.save,")
            .Concat(new byte[] { 0xD1, 0xEE, 0xF5, 0xF0, 0xE0, 0xED, 0xE8, 0xF2, 0xFC })
            .ToArray();
        var response = await ImportBytesAsync(client, cp1251, dryRun: false);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {app.ErrorsText}");
        Assert.Contains("err.ECR-REQ-0422.uiStringCsvNotUtf8", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(revision, await RevisionAsync(client));
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string csv)
        => ImportBytesAsync(client, Encoding.UTF8.GetBytes(csv), dryRun: true);

    private static async Task<HttpResponseMessage> ImportBytesAsync(HttpClient client, byte[] bytes, bool dryRun)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "ru.csv");

        var flag = dryRun ? "true" : "false";
        return await client.PostAsync(new Uri($"/api/v1/ui-strings/import?lang=ru&dryRun={flag}", UriKind.Relative), content);
    }

    private static async Task<int> RevisionAsync(HttpClient client)
    {
        var json = await client.GetStringAsync(new Uri("/api/v1/ui-strings/en?scope=private", UriKind.Relative));
        return JsonDocument.Parse(json).RootElement.GetProperty("revision").GetInt32();
    }
}
