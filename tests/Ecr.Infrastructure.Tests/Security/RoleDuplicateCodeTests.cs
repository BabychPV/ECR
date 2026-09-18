// tests/Ecr.Infrastructure.Tests/Security/RoleDuplicateCodeTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Finding 1: створення ролі з уже зайнятим кодом падало НЕОБРОБЛЕНИМ
/// <c>500 ECR-SYS-0500</c> — модалка не показувала ні тосту, ні помилки
/// поля, просто лишалася як є.
/// </summary>
/// <remarks>
/// ⛔ <c>CreateRoleHandler</c> не мав перевірки коду заздалегідь узагалі (на
/// відміну, наприклад, від <c>CreatePeriodPolicyHandler</c>, у якого є хоча б
/// пре-чек): другий запит тим самим кодом доходив до
/// <c>UserStore.AddRoleAsync</c> → <c>SaveRoleAsync</c> → перший
/// <c>SaveChangesAsync</c>, і <c>UQ_Role</c> ловив дублікат уже в БАЗІ —
/// винятком, якого до фіксу не ловив ніхто.
///
/// Той самий клас дефекту, що вже виправлений для <c>doc.TableRow</c>
/// (Q-241/Q-245, <c>RowStoreRaceTests</c>): перевірка ідентична за формою,
/// різниться лише сутність і повідомлення.
/// </remarks>
[Collection("SqlServer")]
public sealed class RoleDuplicateCodeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "lane1-2-4-unhandled-500-pattern")]
    public async Task Дублікат_коду_ролі_дає_ECR_SEC_0409_а_не_сирий_виняток_бази()
    {
        var code = $"DUP_ROLE_{_tag}";

        await using (var db1 = Context())
        {
            var winner = new UserStore(db1);
            var role = new Role(EcrCode.Create(code), Text("First"));
            await winner.AddRoleAsync(role, [], CancellationToken.None);
        }

        // Другий запис — ОКРЕМИЙ DbContext (як окремий HTTP-запит), той самий
        // код: саме цей шлях доходив би необробленим до
        // ExceptionHandlingMiddleware до фіксу.
        await using var db2 = Context();
        var loser = new UserStore(db2);
        var duplicate = new Role(EcrCode.Create(code), Text("Second"));

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => loser.AddRoleAsync(duplicate, [], CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", thrown.ErrorCode);
        Assert.Contains(code, thrown.Message, StringComparison.Ordinal);

        // ⛔ Q-30x: без Details["messageKey"] подробиця доїжджала клієнту
        // сирим українським реченням незалежно від мови інтерфейсу —
        // ExceptionHandlingMiddleware.LocalizedDetailAsync резолвить це
        // ЛИШЕ коли Details несе цей ключ.
        Assert.NotNull(thrown.Details);
        Assert.Equal("err.ECR-SEC-0409.roleCodeTaken", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
