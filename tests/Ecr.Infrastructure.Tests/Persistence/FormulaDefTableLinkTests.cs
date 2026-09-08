// tests/Ecr.Infrastructure.Tests/Persistence/FormulaDefTableLinkTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>cfg.FormulaDef</c> посилається на <c>cfg.TableDef</c> РІВНО ОДНИМ
/// зовнішнім ключем (`Q-163`, директива №10 ПК-1).
/// </summary>
/// <remarks>
/// ⛔ Знайдено при перегляді PR #53 (директива №09 §8.2): `FormulaDefConfiguration`
/// оголошувала зв'язок `HasOne&lt;TableDef&gt;().WithMany()` без прив'язки до
/// навігації `TableDef.Formulas`. EF Core трактував цю навігацію як окрему,
/// неоголошену, і завів під неї ДРУГИЙ, тіньовий зовнішній ключ —
/// `TableDefId1`. Публікація (яка читає структуру через
/// `TemplateVersionStore.GetWithStructureAsync`, тобто через навігацію)
/// бачила формули за `TableDefId1`; кеш метаданих і перерахунок (які читають
/// напряму `db.FormulaDefs.Where(f => f.TableDefId == ...)`) бачили формули
/// за `TableDefId`. Формула, вставлена єдиним у бою писарем
/// (<c>TableDef.AddFormula</c>, який виставляє лише `TableDefId`), була
/// невидима публікації і рахувалася перерахунком — неправильне число без
/// жодної ознаки, той самий клас, що вже коштував `S-21`.
///
/// ⚠ Виправлення — `.WithMany(t => t.Formulas)` замість голого `.WithMany()`
/// (`FormulaDefConfiguration`, той самий прийом, що вже застосований для
/// `RegistryDef.Fields`), плюс міграція `Q163RemoveFormulaDefShadowFk`, яка
/// прибирає стовпець `TableDefId1` і його зовнішній ключ. Дані переносити не
/// було звідки: стовпець від першої міграції не отримував запису ЖОДНИМ
/// писарем (перевірено прямим запитом на `EcrDev`: `SUM(CASE WHEN
/// TableDefId1 IS NOT NULL THEN 1 ELSE 0 END)` — `NULL` на порожній таблиці,
/// тобто жодного рядка з непорожнім тіньовим ключем ніде).
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaDefTableLinkTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_вставлена_лише_через_TableDefId_видна_і_навігації_і_прямому_запиту()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        int formulaId;
        await using (var write = CreateContext())
        {
            // ⛔ Єдиний реальний писар (`TableDef.AddFormula`) виставляє лише
            // `TableDefId` — жоден шлях у продукційному коді не торкається
            // навігаційної властивості. Тест навмисно повторює саме це, а не
            // ідеальний випадок «завантажити батька й додати через нього».
            var formula = new FormulaDef(
                doc.TableDefId, FormulaScope.Column, "[A] + [B]", ExpressionDialect.Template);
            formula.AssignColumn(doc.ColumnDefIds[0]);

            write.FormulaDefs.Add(formula);
            await write.SaveChangesAsync(ct);
            formulaId = formula.Id;
        }

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: шлях публікації — через навігацію
        // `TableDef.Formulas`. На невиправленій схемі цей запит повертав
        // ПОРОЖНЬО, бо навігація читала за `TableDefId1` (завжди NULL), а не
        // за `TableDefId`.
        await using (var readViaNavigation = CreateContext())
        {
            var table = await readViaNavigation.TableDefs
                .Include(t => t.Formulas)
                .FirstAsync(t => t.Id == doc.TableDefId, ct);

            Assert.Contains(table.Formulas, f => f.Id == formulaId);
        }

        // Шлях кешу метаданих і перерахунку — прямий запит за `TableDefId`.
        // Цей завжди бачив формулу і на невиправленій схемі теж: розбіжність
        // була саме в тому, що ДВА шляхи бачили РІЗНЕ, а не в тому, що один
        // з них був зламаний сам по собі.
        await using (var readDirect = CreateContext())
        {
            var visible = await readDirect.FormulaDefs
                .Where(f => f.TableDefId == doc.TableDefId)
                .Select(f => f.Id)
                .ToListAsync(ct);

            Assert.Contains(formulaId, visible);
        }
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
