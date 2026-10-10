using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// U1-04: сід не віддає права ВБУДОВАНОЇ ролі ВЛАСНІЙ ролі з тим самим кодом.
/// </summary>
/// <remarks>
/// ⛔ <c>MERGE sec.Role … WHEN NOT MATCHED</c> власну роль із кодом вбудованої не чіпає (код зайнятий), а
/// <c>MERGE sec.RolePermission</c> шукав роль лише за кодом — і на наступному старті власна роль (наприклад,
/// створена замовником до появи вбудованої <c>ReportViewer</c>) отримувала її права, а <c>BootstrapAdministrator</c> —
/// ще й <c>Security.ManageRoles</c>. Тепер роль береться лише з <c>IsBuiltIn = 1</c>.
///
/// ⚠ Стан «власна роль із кодом вбудованої, без прав» відтворюється на ВБУДОВАНІЙ <c>ReportViewer</c> цієї бази
/// (<c>IsBuiltIn = 0</c>, пари прав видалено) і ЗАВЖДИ повертається назад у <c>finally</c> повторним сідом; колекція
/// <c>SqlServer</c> виконується послідовно, тож чужі тести проміжного стану не бачать. Мутація: прибрати
/// <c>AND r.IsBuiltIn = 1</c> з MERGE <c>ReportViewer</c> у <c>09-seed.sql</c> — тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class SeedBuiltInRoleRightsTests(SqlServerFixture sql)
{
    private const string RoleCode = "ReportViewer";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "U1-04")]
    public async Task Власна_роль_із_кодом_вбудованої_не_отримує_її_прав_від_сіду()
    {
        await using var db = Context();

        // Контроль: вбудована роль права має (сід їх видав).
        await new SeedRunner(db).RunAsync(CancellationToken.None).ConfigureAwait(true);
        var builtInRights = await CountRightsAsync(db).ConfigureAwait(true);
        Assert.True(builtInRights > 0, "вбудована ReportViewer має права після сіду");

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.[Role] SET IsBuiltIn = 0 WHERE Code = {RoleCode}").ConfigureAwait(true);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE rp FROM sec.RolePermission rp JOIN sec.[Role] r ON r.Id = rp.RoleId WHERE r.Code = {RoleCode}")
                .ConfigureAwait(true);
            Assert.Equal(0, await CountRightsAsync(db).ConfigureAwait(true));

            await new SeedRunner(db).RunAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(0, await CountRightsAsync(db).ConfigureAwait(true));
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.[Role] SET IsBuiltIn = 1 WHERE Code = {RoleCode}").ConfigureAwait(true);
            await new SeedRunner(db).RunAsync(CancellationToken.None).ConfigureAwait(true);
        }

        // Після повернення вбудованої ролі сід видає їй права знову.
        Assert.Equal(builtInRights, await CountRightsAsync(db).ConfigureAwait(true));
    }

    private static async Task<int> CountRightsAsync(EcrDbContext db)
        => (await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM sec.RolePermission rp JOIN sec.[Role] r ON r.Id = rp.RoleId WHERE r.Code = {RoleCode}")
            .ToListAsync().ConfigureAwait(false)).Single();

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
