// tests/Ecr.Infrastructure.Tests/Localization/UiStringMailKeyCanonicalTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>
/// S7: колація стовпця <c>[Key]</c> (<c>Latin1_General_100_CI_AS_SC</c>) зіставляє зі справжнім ключем листа не лише інший
/// регістр і кінцевий пробіл, а й тисячі символів, що в ній не мають ваги (виміряно на живій базі: U+0000, U+00AD,
/// U+034F, U+0370, U+0620, U+202A.. та ін. — ~5000 кодів BMP, з них ~3100 НЕ категорії Format). Лояльний
/// <c>IsMailTemplate</c> цього не покриває: лише звірка з КАНОНІЧНИМ ключем бази (<c>FindKeyAsync</c>) закриває обхід.
/// Живий SQL Server: фейк колації не має.
/// </summary>
[Collection("SqlServer")]
public sealed class UiStringMailKeyCanonicalTests(SqlServerFixture sql)
{
    private const int Editor = 5;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("exact")]
    [InlineData("case")]
    [InlineData("upper")]
    [InlineData("trailingSpace")]
    [InlineData("softHyphen")] // U+00AD: Format, лояльний IsMailTemplate його знімає
    [InlineData("combiningGraphemeJoiner")] // U+034F: NonSpacingMark, у суфіксі — IsMailTemplate не бачить
    [InlineData("greekHeta")] // U+0370: літера без ваги в колації
    [InlineData("arabicLetter")] // U+0620: літера без ваги в колації
    public async Task Ключ_листа_із_ігнорованим_колацією_символом_без_права_на_сповіщення_це_403_і_текст_не_змінюється(string variant)
    {
        // ⛔ МУТАЦІЇ: (1) повернути Ordinal-порівняння в IsMailTemplate і прибрати звірку з `FindKeyAsync` в
        // SetUiStringHandler → червоні всі варіанти, крім `exact`; (2) лише прибрати `|| IsMailTemplate(canonical)`
        // (лояльний IsMailTemplate лишити) → червоні варіанти з символами поза Format: U+034F, U+0370, U+0620.
        var real = $"notifications.s7{Guid.NewGuid():N}.body";
        var spelled = variant switch
        {
            "exact" => real,
            "case" => real.Replace("notifications", "Notifications", StringComparison.Ordinal)
                .Replace(".body", ".Body", StringComparison.Ordinal),
            "upper" => real.ToUpperInvariant(),
            "trailingSpace" => real + " ",
            "softHyphen" => real.Replace(".body", ".bo\u00ADdy", StringComparison.Ordinal),
            "combiningGraphemeJoiner" => real.Replace(".body", ".bo\u034Fdy", StringComparison.Ordinal),
            "greekHeta" => real.Replace(".body", ".bo\u0370dy", StringComparison.Ordinal),
            "arabicLetter" => real.Replace(".body", ".bo\u0620dy", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        await store.SetAsync(
            new UiStringWrite(real, "en", "original", UiStringScope.Private, null, DateTime.UtcNow), CancellationToken.None);

        try
        {
            // Доказ механізму: база СПРАВДІ зіставляє написання зі справжнім ключем.
            Assert.Equal(real, await store.FindKeyAsync(spelled, CancellationToken.None));

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
                .Returns(new AccessBuilder { UserId = Editor }.Permission(SetUiStringHandler.Permission).Build());
            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(Editor);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(DateTime.UtcNow);
            var handler = new SetUiStringHandler(
                store, access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), user, clock);

            await Assert.ThrowsAsync<AccessDeniedException>(() => handler.HandleAsync(
                spelled, "en", "evil http://phish.example", (byte)UiStringScope.Private, CancellationToken.None));

            var catalog = await store.GetAsync("en", CancellationToken.None);
            Assert.Equal("original", catalog.Strings[real]);
        }
        finally
        {
            // Власний ключ тесту прибираємо: посіяних рядків це не торкається.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sys_ecr.UiString WHERE [Key] = {0}", real);
        }
    }
}