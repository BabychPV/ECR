// tests/Ecr.Infrastructure.Tests/Integration/DataSourceCredentialResolverTests.cs
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// Пароль джерела під DataProtection (P-1126-SRCCRED, R-23, AC-2, AC-R): блоб не несе відкритого тексту, Windows не
/// розшифровує нічого, зіпсований блоб — <see cref="CredentialsUnreadableException"/>, а не сирий виняток.
/// </summary>
public sealed class DataSourceCredentialResolverTests
{
    private readonly EphemeralDataProtectionProvider _provider = new();

    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Блоб_не_містить_пароля_і_розшифровується_назад_лише_своїм_призначенням()
    {
        var resolver = new DataSourceCredentialResolver(_provider);
        var blob = resolver.Protect(new SecretString(_sentinel));

        Assert.DoesNotContain(_sentinel, Encoding.UTF8.GetString(blob), StringComparison.Ordinal);
        Assert.DoesNotContain(_sentinel, Convert.ToBase64String(blob), StringComparison.Ordinal);

        var source = Source(ExternalTransport.PiSqlClient, "reader", blob);
        var resolved = resolver.Resolve(source)!;
        Assert.Equal("reader", resolved.UserName);
        Assert.Equal(_sentinel, resolved.Password.Reveal());
        Assert.DoesNotContain(_sentinel, resolved.ToString(), StringComparison.Ordinal);

        // ⛔ Окреме призначення: SMTP-захисник цей блоб не розшифрує (і навпаки).
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => new SmtpPasswordProtector(_provider).Unprotect(blob));
    }

    [Fact]
    public void Windows_не_розшифровує_нічого()
    {
        var counting = new CountingProvider(_provider);
        var resolver = new DataSourceCredentialResolver(counting);
        var source = Source(ExternalTransport.Sql, null, null);

        Assert.Null(resolver.Resolve(source));
        Assert.Equal(0, counting.Unprotects);
    }

    [Fact]
    public void Зіпсований_блоб_або_інше_кільце_дають_credentialsUnreadable_без_тексту_DataProtection()
    {
        var blob = new DataSourceCredentialResolver(new EphemeralDataProtectionProvider()).Protect(new SecretString(_sentinel));
        var resolver = new DataSourceCredentialResolver(_provider);

        var e = Assert.Throws<CredentialsUnreadableException>(() => resolver.Resolve(Source(ExternalTransport.Sql, "reader", blob)));

        Assert.Equal(CredentialsUnreadableException.MessageKey, e.Details!["messageKey"]);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Convert.ToBase64String(blob), e.Message, StringComparison.Ordinal);
    }

    private static DataSource Source(ExternalTransport transport, string? userName, byte[]? blob)
    {
        var source = new DataSource(
            EcrCode.Create("SRC"), new LocalizedText(new Dictionary<string, string> { ["en"] = "S" }),
            transport, "Server=db", "DataSource.SRC");

        if (userName is not null)
        {
            source.ReplaceCredentials(SourceAuthMode.UserPassword, userName, blob);
        }

        return source;
    }

    /// <summary>Рахує розшифрування (AC-R: для Windows їх нуль).</summary>
    private sealed class CountingProvider(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public int Unprotects { get; private set; }

        public IDataProtector CreateProtector(string purpose) => new Counting(this, inner.CreateProtector(purpose));

        private sealed class Counting(CountingProvider owner, IDataProtector inner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Counting(owner, inner.CreateProtector(purpose));

            public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);

            public byte[] Unprotect(byte[] protectedData)
            {
                owner.Unprotects++;
                return inner.Unprotect(protectedData);
            }
        }
    }
}

/// <summary>
/// Мітка відмови в базі (огляд L3): ставиться лише рядку ТІЄЇ версії, яку завантажив прогін, — заміна пароля посеред
/// прогону мітки не отримує.
/// </summary>
/// <remarks>Мутація: прибрати <c>s.RowVersion == version</c> з умови — тест червоніє.</remarks>
[Collection("SqlServer")]
public sealed class DataSourceCredentialRejectionsTests(Ecr.TestKit.SqlServerFixture sql)
{
    [Fact]
    [Trait(Ecr.TestKit.TestCategories.Category, Ecr.TestKit.TestCategories.Integration)]
    public async Task Мітка_ставиться_лише_рядку_завантаженої_версії()
    {
        var code = "REJ_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int id;
        await using (var db = sql.CreateContext())
        {
            var source = new DataSource(
                EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = "R" }),
                ExternalTransport.Sql, "Server=db", "DataSource." + code);
            source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [1, 2, 3]);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            id = source.Id;
        }

        // Прогін завантажив джерело…
        DataSource loaded;
        await using (var db = sql.CreateContext())
        {
            loaded = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.DataSources.Where(s => s.Id == id));
        }

        // … а людина тим часом замінила пароль.
        await using (var db = sql.CreateContext())
        {
            var fresh = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.DataSources.Where(s => s.Id == id));
            fresh.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [4, 5, 6]);
            await db.SaveChangesAsync();
        }

        await using (var db = sql.CreateContext())
        {
            await new DataSourceCredentialRejections(db, new Ecr.TestKit.TestClock(DateTime.UtcNow)).ReportRejectedAsync(loaded, default);
        }

        await using (var db = sql.CreateContext())
        {
            var after = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.DataSources.Where(s => s.Id == id));
            Assert.Null(after.CredentialsRejectedAt);

            // Поточна версія — мітка ставиться.
            await new DataSourceCredentialRejections(db, new Ecr.TestKit.TestClock(DateTime.UtcNow)).ReportRejectedAsync(after, default);
        }

        await using (var db = sql.CreateContext())
        {
            var marked = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.DataSources.Where(s => s.Id == id));
            Assert.NotNull(marked.CredentialsRejectedAt);
            db.DataSources.Remove(marked);
            await db.SaveChangesAsync();
        }
    }
}
