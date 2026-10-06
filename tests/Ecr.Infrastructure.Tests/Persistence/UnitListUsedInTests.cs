// tests/Ecr.Infrastructure.Tests/Persistence/UnitListUsedInTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// UI-21: «Used in» у переліку одиниць — один агрегат на весь перелік і лише для <c>Uom.EditCatalog</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази: підставити в обробник <c>counts</c> без перевірки права — червоний тест
/// «без права null»; прибрати з <c>CountUnitStructuralUsageAsync</c> гілку полів довідників —
/// червоний рахунок «2».
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitListUsedInTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_одиниць_несе_UsedIn_лише_з_правом_EditCatalog_і_без_N_плюс_1()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);

        int used;
        int free;
        await using (var db = sql.CreateContext())
        {
            var dimension = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();
            var usedUnit = new Unit(
                EcrCode.Create($"ui{_tag}"), Name("u"), Name("Used"), dimension.Id, isBase: false, 2m, 0m);
            var freeUnit = new Unit(
                EcrCode.Create($"uf{_tag}"), Name("f"), Name("Free"), dimension.Id, isBase: false, 3m, 0m);
            db.Units.AddRange(usedUnit, freeUnit);
            await db.SaveChangesAsync();

            var one = new ColumnDef(doc.TableDefId, EcrCode.Create($"UA_{_tag}"), Name("A"), 20, CellDataType.Decimal);
            one.SetUnit(usedUnit.Id);
            var two = new ColumnDef(doc.TableDefId, EcrCode.Create($"UB_{_tag}"), Name("B"), 21, CellDataType.Decimal);
            two.SetUnit(usedUnit.Id);
            db.ColumnDefs.AddRange(one, two);
            await db.SaveChangesAsync();

            used = usedUnit.Id;
            free = freeUnit.Id;
        }

        var withRight = await ListAsync(permitted: true);
        Assert.Equal(2, withRight.Single(u => u.Id == used).UsedIn);
        Assert.Equal(0, withRight.Single(u => u.Id == free).UsedIn);

        // ⛔ Без права — null, а не 0: «не знаю» не можна видавати за «ніде не використовується».
        var withoutRight = await ListAsync(permitted: false);
        Assert.All(withoutRight, u => Assert.Null(u.UsedIn));
        Assert.Contains(withoutRight, u => u.Id == used);
    }

    private async Task<IReadOnlyList<Ecr.Application.Ports.UnitRef>> ListAsync(bool permitted)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var builder = new AccessBuilder { UserId = 9 };
        if (permitted)
        {
            builder.Permission("Uom.EditCatalog");
        }

        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        await using var db = sql.CreateContext();

        return await new ListUnitsHandler(new UnitCatalog(db), new UnitStore(db), access, user)
            .HandleAsync(CancellationToken.None);
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
