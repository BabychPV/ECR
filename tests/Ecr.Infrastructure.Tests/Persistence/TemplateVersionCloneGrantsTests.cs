// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneGrantsTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// A1-04 (приймальний прохід A1, 2026-10-05): клон версії шаблону переносить ресурсні гранти
/// (Deny і дозволи на аркуш/таблицю/колонку) на нові Id за кодом — на живому SQL.
/// </summary>
/// <remarks>
/// ⛔ Id аркушів, таблиць і колонок свої в кожної версії, а <c>sec.ResourceGrant</c> посилається на
/// Id: без копіювання Deny на колонку версії 1.0 мовчки не діяв на документ версії 1.1 (fail-open).
/// Той самий принцип, що в перенесенні документа між версіями (<c>DocumentVersionMigrationStore</c>, п. 6a).
/// Мутаційний доказ: прибрати <c>CloneResourceGrantsAsync</c> із <c>SaveCloneAsync</c> — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneGrantsTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-04")]
    public async Task Клон_версії_копіює_Deny_і_дозволи_на_аркуш_таблицю_колонку_на_нові_Id()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var deniedColumn = doc.ColumnDefIds[0];

        int roleId;
        await using (var setup = builder.CreateContext())
        {
            var role = new Role(
                EcrCode.Create($"A104{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "A1-04" }));
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(ct);
            roleId = role.Id;

            setup.ResourceGrants.AddRange(
                new ResourceGrant(roleId, ResourceKind.Sheet, doc.SheetDefId, GrantLevel.None, isDeny: true),
                new ResourceGrant(roleId, ResourceKind.Table, doc.TableDefId, GrantLevel.None, isDeny: true),
                new ResourceGrant(roleId, ResourceKind.Column, deniedColumn, GrantLevel.None, isDeny: true),
                new ResourceGrant(roleId, ResourceKind.Column, doc.ColumnDefIds[1], GrantLevel.Read),
                // Дозвіл вище Read міг би ПІДНЯТИ доступ на клоні (ent7 P2-2) — не копіюється.
                new ResourceGrant(roleId, ResourceKind.Column, doc.ColumnDefIds[2], GrantLevel.Write));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"8.{tag[..4].GetHashCode() & 0xFFF}.0.1", 1,
                new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloneSheet = await read.SheetDefs.AsNoTracking()
            .SingleAsync(s => s.TemplateVersionId == cloneId && s.Code == doc.SheetCode, ct);
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => t.SheetDefId == cloneSheet.Id, ct);
        var sourceColumns = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == doc.TableDefId).ToListAsync(ct);
        var cloneColumns = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).ToListAsync(ct);
        var cloneDenied = cloneColumns.Single(c => c.Code == sourceColumns.Single(s => s.Id == deniedColumn).Code);
        var cloneAllowed = cloneColumns.Single(c => c.Code == sourceColumns.Single(s => s.Id == doc.ColumnDefIds[1]).Code);

        var grants = await read.ResourceGrants.AsNoTracking().Where(g => g.RoleId == roleId).ToListAsync(ct);

        // Заборони — на Id клону.
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Sheet && g.ResourceId == cloneSheet.Id && g.IsDeny);
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Table && g.ResourceId == cloneTable.Id && g.IsDeny);
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Column && g.ResourceId == cloneDenied.Id && g.IsDeny);

        // Дозвіл теж звужує доступ — переноситься з рівнем.
        Assert.Contains(grants, g =>
            g.ResourceKind == ResourceKind.Column && g.ResourceId == cloneAllowed.Id && !g.IsDeny && g.Level == GrantLevel.Read);

        // Write-дозвіл не скопійований; джерело не зачеплене: п'ять своїх + чотири на клон.
        var cloneWriteColumn = cloneColumns.Single(c => c.Code == sourceColumns.Single(s => s.Id == doc.ColumnDefIds[2]).Code);
        Assert.DoesNotContain(grants, g => g.ResourceKind == ResourceKind.Column && g.ResourceId == cloneWriteColumn.Id);
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Column && g.ResourceId == deniedColumn && g.IsDeny);
        Assert.Equal(9, grants.Count);
    }
}
