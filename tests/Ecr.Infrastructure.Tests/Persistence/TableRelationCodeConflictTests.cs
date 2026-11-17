// tests/Ecr.Infrastructure.Tests/Persistence/TableRelationCodeConflictTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// T2-03 (тестувальний прохід №2): код зв'язку таблиць унікальний на ВСЮ систему
/// (<c>UQ_TableRelationDef</c> лише по <c>Code</c>); колізія в іншій версії/шаблоні —
/// <c>409 ECR-TMPL-0409</c> з ключем, а не голий <c>500</c>. Живий SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: (1) прибрати арм <c>TableRelationDef</c> у
/// <c>UnitOfWork.TryMapDuplicateKey</c> — перший тест червоніє (DbUpdateException);
/// (2) змінити ім'я індексу в <c>ViolatesIndex</c> — теж червоніє.
/// Інше порушення унікальності (код шаблону) НЕ перехоплюється цим армом.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableRelationCodeConflictTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "T2-03")]
    public async Task Той_самий_код_зв_язку_в_іншому_шаблоні_дає_409_з_ключем_а_не_500()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var first = await builder.BuildAsync(ct: ct);
        var second = await builder.BuildAsync(ct: ct);
        var code = $"DUP{Guid.NewGuid():N}"[..16].ToUpperInvariant();

        await using (var db = builder.CreateContext())
        {
            db.TableRelations.Add(Relation(code, first.TableDefId, second.TableDefId));
            await new UnitOfWork(db).SaveChangesAsync(ct);
        }

        await using var other = builder.CreateContext();
        other.TableRelations.Add(Relation(code, second.TableDefId, first.TableDefId));

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(other).SaveChangesAsync(ct));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.Equal("err.ECR-TMPL-0409.relationCodeTaken", ex.Details["messageKey"]);
        Assert.Equal(code, ex.Details["relationCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "T2-03")]
    public async Task Інше_порушення_унікальності_цим_армом_не_перехоплюється()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        var code = $"HG{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        await using (var seed = builder.CreateContext())
        {
            seed.ColumnHeaderGroupDefs.Add(Group(doc.TableDefId, code, 900));
            await seed.SaveChangesAsync(ct);
        }

        // Той самий код групи шапки в тій самій таблиці — `UQ_ColumnHeaderGroupDef`, не `UQ_TableRelationDef`.
        // ✎ B158 (batch-1117-3): раніше тут був дубль коду ШАБЛОНУ, але `UQ_Template_Code` (і структура версії:
        // `UQ_SheetDef`, `UQ_TableDef`, `UQ_ColumnDef`…) тепер законно мапить `TemplateDuplicateKeys` (B158b) —
        // передумова «індекс без арму» застаріла. `UQ_ColumnHeaderGroupDef` арму не має в жодному `TryMap`;
        // остання перевірка тримає саму передумову: з'явиться арм — тест скаже про це прямо, а не мовчки.
        await using var db = builder.CreateContext();
        db.ColumnHeaderGroupDefs.Add(Group(doc.TableDefId, code, 901));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new UnitOfWork(db).SaveChangesAsync(ct));
        Assert.IsNotType<ConcurrencyConflictException>(ex);
        Assert.IsType<DbUpdateException>(ex);
        Assert.Contains("UQ_ColumnHeaderGroupDef", ex.GetBaseException().Message, StringComparison.Ordinal);
    }

    private static ColumnHeaderGroupDef Group(int tableDefId, string code, int ordinal)
        => new(tableDefId, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = "dup" }), ordinal);

    private static TableRelationDef Relation(string code, int source, int target)
        => new(EcrCode.Create(code), source, target, TableRelationKind.Check, "{}");
}
