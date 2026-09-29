using System.Globalization;
using Ecr.Api.Auth;
using Ecr.Application.Documents;
using Ecr.Application.Localization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Рішення людини 2026-09-29, механізм мови: культура розбору числа береться з
/// ТОГО САМОГО <c>CurrentUser.Language</c>, яким сервер локалізує відмови
/// (профіль → <c>Accept-Language</c> → <c>en</c>).
/// </summary>
/// <remarks>
/// ⛔ Мутація: <c>NumberCulture.ForLanguage</c> не переводить код <c>kz</c> у
/// <c>kk-KZ</c> (або бере <c>CultureInfo.CurrentCulture</c> сервера) — рядок
/// казахського браузера читається за чужими роздільниками.
/// </remarks>
public sealed class RequestNumberCultureTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("ru-RU,ru;q=0.9,en;q=0.8", "ru-RU", "1 234,5", "1234.5")]
    [InlineData("kk-KZ", "kk-KZ", "1,234", "1.234")]
    [InlineData("en-US,en;q=0.9", "en-US", "1,234.5", "1234.5")]
    [InlineData(null, "en-US", "1,234.5", "1234.5")]
    public void Культура_розбору_йде_з_мови_запиту(string? acceptLanguage, string culture, string text, string expected)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage is not null)
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        var user = new CurrentUser(new HttpContextAccessor { HttpContext = context });
        var resolved = NumberCulture.ForLanguage(user.Language);

        Assert.Equal(culture, resolved.Name);

        var data = CellValueReader.Read(text, Column(), resolved);

        Assert.NotNull(data);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), data.ValueNumeric);
    }

    /// <summary>
    /// Без мови (en-US) неоднозначне «1,234» — відмова, і вона доїжджає
    /// каталожним реченням наявного ключа, а не новим кодом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Неоднозначне_без_мови_відмовляє_каталожним_реченням()
    {
        var user = new CurrentUser(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var detail = await MainPathLocalizedErrorTests.DetailAsync(() =>
        {
            CellValueReader.Read("1,234", Column(), NumberCulture.ForLanguage(user.Language));
            return Task.CompletedTask;
        });

        Assert.Equal("Column \"C5\" expects a number.", detail);
    }

    private static ColumnDef Column()
        => new(
            tableDefId: 3, EcrCode.Create("C5"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "C5" }), 1, CellDataType.Decimal);
}
