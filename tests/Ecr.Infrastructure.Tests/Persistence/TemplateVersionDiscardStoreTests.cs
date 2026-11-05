// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionDiscardStoreTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Відкидання чернетки версії шаблону (<see cref="TemplateVersionDiscardStore"/>) на реальній базі:
/// каскад не лишає жодного рядка структури, джерело клону не зачеплене, використання поза
/// конфігурацією знаходяться, а обидва переліки сховища покривають живий граф FK.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати з <c>DeleteSql</c> рядок <c>sec.ResourceGrant</c> — червоніє
/// «0 сиріт» (гранти на Id видалених аркуша/таблиці/колонки лишаються, FK їх не тримає); прибрати
/// <c>doc.Project</c> з <c>UsageSql</c> — червоніє «використання версії з проєктом»; прибрати назву з
/// <c>CascadeTables</c> — червоніє сторож графа FK.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionDiscardStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Відкинута_чернетка_не_лишає_жодного_рядка_структури_а_джерело_клону_ціле()
    {
        var ct = CancellationToken.None;
        var (builder, doc, draftId) = await DraftCloneWithRichStructureAsync(ct);
        var sourceBefore = await StructureCountsAsync(doc.TemplateVersionId, ct);
        var draftBefore = await StructureCountsAsync(draftId, ct);

        // Контроль: структура чернетки справді населена — інакше «0 після» нічого б не доводив.
        foreach (var table in new[]
                 {
                     "cfg.SheetDef", "cfg.TableDef", "cfg.ColumnDef", "cfg.RowDef", "cfg.PeriodAccessRuleDef",
                     "cfg.StyleDef", "cfg.SheetGroupRule", "cfg.HeaderFieldDef", "cfg.ValidationRule", "sec.ResourceGrant",
                 })
        {
            Assert.True(draftBefore[table] > 0, $"{table}: чернетка не населена");
        }

        var ids = await IdsOfAsync(draftId, ct);

        IReadOnlyDictionary<string, int> deleted;
        await using (var db = builder.CreateContext())
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var store = new TemplateVersionDiscardStore(db);
            Assert.NotNull(await store.LockAsync(draftId, ct));
            Assert.Empty(await store.FindUsagesAsync(draftId, ct));
            deleted = await store.DeleteAsync(draftId, ct);
            await tx.CommitAsync(ct);
        }

        Assert.Equal(1, deleted["cfg.TemplateVersion"]);
        Assert.Equal(draftBefore["sec.ResourceGrant"], deleted["sec.ResourceGrant"]);

        var orphans = await OrphansAsync(draftId, ids, ct);
        Assert.All(orphans, kv => Assert.True(kv.Value == 0, $"{kv.Key}: лишилося {kv.Value} рядків"));
        Assert.Equal(sourceBefore, await StructureCountsAsync(doc.TemplateVersionId, ct));
    }

    private sealed record StructureIds(string Sheets, string Tables, string Columns, string Rows, string Formulas);

    private async Task<StructureIds> IdsOfAsync(int versionId, CancellationToken ct)
    {
        const string query = """
            SELECT
              (SELECT STRING_AGG(CAST(Id AS nvarchar(max)), ',') FROM cfg.SheetDef WHERE TemplateVersionId = @v),
              (SELECT STRING_AGG(CAST(t.Id AS nvarchar(max)), ',') FROM cfg.TableDef t
                 JOIN cfg.SheetDef s ON s.Id = t.SheetDefId WHERE s.TemplateVersionId = @v),
              (SELECT STRING_AGG(CAST(c.Id AS nvarchar(max)), ',') FROM cfg.ColumnDef c JOIN cfg.TableDef t ON t.Id = c.TableDefId
                 JOIN cfg.SheetDef s ON s.Id = t.SheetDefId WHERE s.TemplateVersionId = @v),
              (SELECT STRING_AGG(CAST(r.Id AS nvarchar(max)), ',') FROM cfg.RowDef r JOIN cfg.TableDef t ON t.Id = r.TableDefId
                 JOIN cfg.SheetDef s ON s.Id = t.SheetDefId WHERE s.TemplateVersionId = @v),
              (SELECT STRING_AGG(CAST(f.Id AS nvarchar(max)), ',') FROM cfg.FormulaDef f JOIN cfg.TableDef t ON t.Id = f.TableDefId
                 JOIN cfg.SheetDef s ON s.Id = t.SheetDefId WHERE s.TemplateVersionId = @v);
            """;

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@v", versionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        string Of(int i) => reader.IsDBNull(i) ? string.Empty : reader.GetString(i);

        return new StructureIds(Of(0), Of(1), Of(2), Of(3), Of(4));
    }

    /// <summary>Рядки, що досі посилаються на Id структури, знятої ДО видалення (за версією їх уже не знайти).</summary>
    private async Task<Dictionary<string, int>> OrphansAsync(int versionId, StructureIds ids, CancellationToken ct)
    {
        const string query = """
            SET NOCOUNT ON;
            DECLARE @S TABLE (Id int PRIMARY KEY); INSERT @S SELECT CAST(value AS int) FROM STRING_SPLIT(@idsSheets, ',') WHERE value <> N'';
            DECLARE @T TABLE (Id int PRIMARY KEY); INSERT @T SELECT CAST(value AS int) FROM STRING_SPLIT(@idsTables, ',') WHERE value <> N'';
            DECLARE @C TABLE (Id int PRIMARY KEY); INSERT @C SELECT CAST(value AS int) FROM STRING_SPLIT(@idsColumns, ',') WHERE value <> N'';
            DECLARE @R TABLE (Id int PRIMARY KEY); INSERT @R SELECT CAST(value AS int) FROM STRING_SPLIT(@idsRows, ',') WHERE value <> N'';
            DECLARE @F TABLE (Id int PRIMARY KEY); INSERT @F SELECT CAST(value AS int) FROM STRING_SPLIT(@idsFormulas, ',') WHERE value <> N'';
            SELECT N'cfg.TemplateVersion', COUNT(*) FROM cfg.TemplateVersion WHERE Id = @v
            UNION ALL SELECT N'cfg.SheetDef', COUNT(*) FROM cfg.SheetDef WHERE Id IN (SELECT Id FROM @S) OR TemplateVersionId = @v
            UNION ALL SELECT N'cfg.TableDef', COUNT(*) FROM cfg.TableDef WHERE Id IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.ColumnDef', COUNT(*) FROM cfg.ColumnDef WHERE Id IN (SELECT Id FROM @C)
            UNION ALL SELECT N'cfg.RowDef', COUNT(*) FROM cfg.RowDef WHERE Id IN (SELECT Id FROM @R)
            UNION ALL SELECT N'cfg.FormulaDef', COUNT(*) FROM cfg.FormulaDef WHERE Id IN (SELECT Id FROM @F)
            UNION ALL SELECT N'cfg.FormulaDependency', COUNT(*) FROM cfg.FormulaDependency
                WHERE FormulaDefId IN (SELECT Id FROM @F) OR ColumnDefId IN (SELECT Id FROM @C)
            UNION ALL SELECT N'cfg.ColumnHeaderGroupDef', COUNT(*) FROM cfg.ColumnHeaderGroupDef WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.ValidationRule', COUNT(*) FROM cfg.ValidationRule WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.TableRelationDef', COUNT(*) FROM cfg.TableRelationDef
                WHERE SourceTableDefId IN (SELECT Id FROM @T) OR TargetTableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.PeriodAccessRuleDef', COUNT(*) FROM cfg.PeriodAccessRuleDef
                WHERE TemplateVersionId = @v OR SheetDefId IN (SELECT Id FROM @S) OR TableDefId IN (SELECT Id FROM @T)
                   OR SourceColumnDefId IN (SELECT Id FROM @C)
            UNION ALL SELECT N'cfg.CalculationBinding', COUNT(*) FROM cfg.CalculationBinding
                WHERE TableDefId IN (SELECT Id FROM @T) OR ColumnDefId IN (SELECT Id FROM @C)
            UNION ALL SELECT N'cfg.PermitSheetBinding', COUNT(*) FROM cfg.PermitSheetBinding
                WHERE TemplateVersionId = @v OR SheetTableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.PermitSheetBindingColumn', COUNT(*) FROM cfg.PermitSheetBindingColumn
                WHERE SheetColumnDefId IN (SELECT Id FROM @C)
            UNION ALL SELECT N'cfg.ConditionalFormatRule', COUNT(*) FROM cfg.ConditionalFormatRule WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.SheetGroupRule', COUNT(*) FROM cfg.SheetGroupRule WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.HeaderFieldDef', COUNT(*) FROM cfg.HeaderFieldDef WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.StyleDef', COUNT(*) FROM cfg.StyleDef WHERE TemplateVersionId = @v
            UNION ALL SELECT N'sec.ResourceGrant', COUNT(*) FROM sec.ResourceGrant
                WHERE (ResourceKind = 1 AND ResourceId IN (SELECT Id FROM @S))
                   OR (ResourceKind = 2 AND ResourceId IN (SELECT Id FROM @T))
                   OR (ResourceKind = 3 AND ResourceId IN (SELECT Id FROM @C));
            """;

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@v", versionId);
        command.Parameters.AddWithValue("@idsSheets", ids.Sheets);
        command.Parameters.AddWithValue("@idsTables", ids.Tables);
        command.Parameters.AddWithValue("@idsColumns", ids.Columns);
        command.Parameters.AddWithValue("@idsRows", ids.Rows);
        command.Parameters.AddWithValue("@idsFormulas", ids.Formulas);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Використання_версії_з_проєктом_документом_і_клоном_знаходяться()
    {
        var ct = CancellationToken.None;
        var (builder, doc, draftId) = await DraftCloneWithRichStructureAsync(ct);

        await using var db = builder.CreateContext();
        var store = new TemplateVersionDiscardStore(db);

        // Версія з проєктом і документом (TestDocumentBuilder) — і від неї ж клон.
        var usages = await store.FindUsagesAsync(doc.TemplateVersionId, ct);
        Assert.True(usages.ContainsKey("doc.Project"), string.Join(", ", usages.Keys));
        Assert.True(usages.ContainsKey("doc.TableInstance"), string.Join(", ", usages.Keys));
        Assert.Equal(1, usages["cfg.TemplateVersion"]);

        // Чернетка без жодного посилання — порожньо.
        Assert.Empty(await store.FindUsagesAsync(draftId, ct));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Кожен_FK_під_структурою_версії_покритий_каскадом_або_переліком_використань()
    {
        // Обхід графа FK від cfg.TemplateVersion лише крізь таблиці, які каскад видаляє: кожна таблиця, що на
        // них посилається, мусить бути або в каскаді, або причиною відмови. Нова таблиця з FK на структуру
        // версії без рішення «видаляти чи відмовляти» червонить цей тест, а не живе вікно.
        const string query = """
            SELECT OBJECT_SCHEMA_NAME(fk.referenced_object_id) + N'.' + OBJECT_NAME(fk.referenced_object_id),
                   OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id)
              FROM sys.foreign_keys fk;
            """;

        var edges = new List<(string Parent, string Child)>();
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(query, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                edges.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { "cfg.TemplateVersion" };
        var queue = new Queue<string>(visited);
        var uncovered = new SortedSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var (_, child) in edges.Where(e => e.Parent == parent))
            {
                if (TemplateVersionDiscardStore.CascadeTables.Contains(child))
                {
                    if (visited.Add(child))
                    {
                        queue.Enqueue(child);
                    }
                }
                else if (!TemplateVersionDiscardStore.UsageTables.Contains(child)
                         && !TemplateVersionDiscardStore.CoveredByTableInstance.Contains(child))
                {
                    uncovered.Add($"{parent} <- {child}");
                }
            }
        }

        Assert.True(uncovered.Count == 0, "Не покрито: " + string.Join("; ", uncovered));
    }

    /// <summary>
    /// Версія-джерело (з проєктом і документом) із правилами доступу, гранти, стилем і правилом складу,
    /// її клон-чернетка (усе це скопійоване) плюс поле шапки й правило валідації на самій чернетці.
    /// </summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Doc, int DraftId)> DraftCloneWithRichStructureAsync(
        CancellationToken ct)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using (var setup = builder.CreateContext())
        {
            var roleId = await setup.Roles.AsNoTracking().Select(r => r.Id).FirstAsync(ct);
            setup.PeriodAccessRules.AddRange(
                PeriodAccessRuleDef.EditablePeriodOnly(doc.TemplateVersionId, OutOfWindowBehavior.ReadOnly, 2, 5)
                    .ForSheet(doc.SheetDefId).ForTable(doc.TableDefId),
                PeriodAccessRuleDef.ForSourceWindow(doc.TemplateVersionId, doc.ColumnDefIds[1], OutOfWindowBehavior.ReadOnly)
                    .ForTable(doc.TableDefId));
            setup.ResourceGrants.AddRange(
                new ResourceGrant(roleId, ResourceKind.Sheet, doc.SheetDefId, GrantLevel.Read, isDeny: true),
                new ResourceGrant(roleId, ResourceKind.Table, doc.TableDefId, GrantLevel.Read, isDeny: true),
                new ResourceGrant(roleId, ResourceKind.Column, doc.ColumnDefIds[0], GrantLevel.Read, isDeny: true));
            setup.StyleDefs.Add(new StyleDef(doc.TemplateVersionId, EcrCode.Create("ST_" + tag)));
            setup.SheetGroupRules.Add(new SheetGroupRule(doc.TemplateVersionId, "G_" + tag, 1, null));
            await setup.SaveChangesAsync(ct);
        }

        int draftId;
        await using (var db = builder.CreateContext())
        {
            draftId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"9.{tag.GetHashCode() & 0xFFF}.0.1", 1, Now, ct);
        }

        await using (var setup = builder.CreateContext())
        {
            var tableId = await setup.TableDefs.AsNoTracking()
                .Where(t => setup.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == draftId))
                .Select(t => t.Id).FirstAsync(ct);
            setup.HeaderFieldDefs.Add(new HeaderFieldDef(
                draftId, EcrCode.Create("HF_" + tag), new LocalizedText(), 1, CellDataType.String));
            setup.ValidationRules.Add(new ValidationRule(
                tableId, EcrCode.Create("VR_" + tag), ValidationSeverity.Warning, 0, "1 = 1", new LocalizedText()));
            await setup.SaveChangesAsync(ct);
        }

        return (builder, doc, draftId);
    }

    /// <summary>Рядки кожної таблиці каскаду, що належать версії (через аркуші, таблиці, колонки…).</summary>
    private async Task<Dictionary<string, int>> StructureCountsAsync(int versionId, CancellationToken ct)
    {
        const string query = """
            SET NOCOUNT ON;
            DECLARE @S TABLE (Id int PRIMARY KEY); INSERT @S SELECT Id FROM cfg.SheetDef WHERE TemplateVersionId = @v;
            DECLARE @T TABLE (Id int PRIMARY KEY); INSERT @T SELECT Id FROM cfg.TableDef WHERE SheetDefId IN (SELECT Id FROM @S);
            DECLARE @C TABLE (Id int PRIMARY KEY); INSERT @C SELECT Id FROM cfg.ColumnDef WHERE TableDefId IN (SELECT Id FROM @T);
            DECLARE @F TABLE (Id int PRIMARY KEY); INSERT @F SELECT Id FROM cfg.FormulaDef WHERE TableDefId IN (SELECT Id FROM @T);
            SELECT N'cfg.TemplateVersion', COUNT(*) FROM cfg.TemplateVersion WHERE Id = @v
            UNION ALL SELECT N'cfg.SheetDef', COUNT(*) FROM @S
            UNION ALL SELECT N'cfg.TableDef', COUNT(*) FROM @T
            UNION ALL SELECT N'cfg.ColumnDef', COUNT(*) FROM @C
            UNION ALL SELECT N'cfg.RowDef', COUNT(*) FROM cfg.RowDef WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.FormulaDef', COUNT(*) FROM @F
            UNION ALL SELECT N'cfg.FormulaDependency', COUNT(*) FROM cfg.FormulaDependency WHERE FormulaDefId IN (SELECT Id FROM @F)
            UNION ALL SELECT N'cfg.ColumnHeaderGroupDef', COUNT(*) FROM cfg.ColumnHeaderGroupDef WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.ValidationRule', COUNT(*) FROM cfg.ValidationRule WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.TableRelationDef', COUNT(*) FROM cfg.TableRelationDef
                WHERE SourceTableDefId IN (SELECT Id FROM @T) OR TargetTableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.PeriodAccessRuleDef', COUNT(*) FROM cfg.PeriodAccessRuleDef WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.CalculationBinding', COUNT(*) FROM cfg.CalculationBinding WHERE TableDefId IN (SELECT Id FROM @T)
            UNION ALL SELECT N'cfg.PermitSheetBinding', COUNT(*) FROM cfg.PermitSheetBinding WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.ConditionalFormatRule', COUNT(*) FROM cfg.ConditionalFormatRule WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.SheetGroupRule', COUNT(*) FROM cfg.SheetGroupRule WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.HeaderFieldDef', COUNT(*) FROM cfg.HeaderFieldDef WHERE TemplateVersionId = @v
            UNION ALL SELECT N'cfg.StyleDef', COUNT(*) FROM cfg.StyleDef WHERE TemplateVersionId = @v
            UNION ALL SELECT N'sec.ResourceGrant', COUNT(*) FROM sec.ResourceGrant
                WHERE (ResourceKind = 1 AND ResourceId IN (SELECT Id FROM @S))
                   OR (ResourceKind = 2 AND ResourceId IN (SELECT Id FROM @T))
                   OR (ResourceKind = 3 AND ResourceId IN (SELECT Id FROM @C));
            """;

        // ⚠ Після видалення множини аркушів/таблиць порожні, тож сироту, що лишилася за СТАРИМ Id, цей запит
        // не побачить. Тому для чернетки Id знімаються ДО видалення й перевіряються окремо (OrphansAsync).
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@v", versionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }
}
