// tests/Ecr.Infrastructure.Tests/Security/ServiceAccountStampTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// (є) Cookie службового запису <c>svc-integration</c> не проходить перевірку
/// штампа на жодному запиті — другий рубіж після відмови у вході.
/// </summary>
[Collection("SqlServer")]
public sealed class ServiceAccountStampTests(SqlServerFixture sql)
{
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати умову на <c>UserName</c> у
    /// <c>SecurityStampValidator.ReadStampAsync</c> → чинний штамп приймається.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Чинний_штамп_службового_запису_не_приймається()
    {
        await using var db = sql.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        // Людина-контроль: свіжа, бо в спільній базі іншого активного запису може не бути.
        var name = $"st_{Guid.NewGuid():N}"[..20];
        var human = new User(name, name, AuthProvider.Local);
        human.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(human);
        await db.SaveChangesAsync();

        var svc = await db.Users.AsNoTracking().SingleAsync(u => u.UserName == User.IntegrationServiceUserName);

        var configuration = Substitute.For<IConfiguration>();
        configuration["Auth:StampCacheSeconds"].Returns("0");
        var validator = new SecurityStampValidator(db, memory, configuration);

        Assert.False(await validator.IsCurrentAsync(svc.Id, svc.SecurityStamp, CancellationToken.None));

        // Контроль: той самий механізм приймає чинний штамп людини.
        Assert.True(await validator.IsCurrentAsync(human.Id, human.SecurityStamp, CancellationToken.None));
    }
}
