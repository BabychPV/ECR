// tests/Ecr.Api.Tests/ScopedRegistryViewApiTests.HeaderValue.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AU-13 P3-7 (S18): <c>GET …/header</c> віддавав глядачеві під Deny на довідник поля Lookup сирий ідентифікатор обраного
/// запису (<c>value: 3</c>) - факт про запис схованого довідника.
/// </summary>
/// <remarks>
/// Мутація: у <c>HeaderVersion.ToDto</c> завжди брати збережене значення (без <c>registryDenied</c>) - червоніють випадки Deny.
/// </remarks>
public sealed partial class ScopedRegistryViewApiTests
{
    [Theory]
    [InlineData("global", true)]
    [InlineData("scoped", true)]
    [InlineData("deniedUnscopedRole", false)]
    [InlineData("scopedSelfDeny", false)]
    [InlineData("narrowedDenyPlusView", false)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Шапка_під_Deny_на_довідник_не_віддає_ідентифікатора_запису(string kind, bool visible)
    {
        var s = await ArrangeAsync(kind).ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                s.Doc.DocumentId, s.PermitFieldId, new DocumentHeaderValueData { ValueRegistryEntryId = s.EntryId }));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await client.GetAsync(HeaderUri(s.Doc.DocumentId)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var field = JsonDocument.Parse(body).RootElement.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("code").GetString() == "PERMIT");
        var value = field.GetProperty("value");
        if (visible)
        {
            Assert.Equal(s.EntryId.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }
}
