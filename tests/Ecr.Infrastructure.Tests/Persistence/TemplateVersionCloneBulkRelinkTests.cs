// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneBulkRelinkTests.cs
using System.Data.Common;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// W15z (клон LAND_UI ~2 хв, з них ~45 с — перев'язка): групи заголовків, стилі, каскади колонок і батьки рядків клону
/// пишуться ОДНИМ set-based UPDATE на таблицю, а не UPDATE на кожен рядок (кожен — з тригером незмінності).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати виклик <c>TemplateCloneBulkRelink.ApplyAsync</c> у <c>SaveCloneAsync</c> — EF пише
/// <c>UPDATE [cfg].[ColumnDef]</c> на кожну з 60 колонок, тест червоний. Коректність перев'язки — тут і в
/// <c>ColumnHeaderGroupCloneTests</c> / <c>TemplateVersionCloneGraphStyleTests</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneBulkRelinkTests(SqlServerFixture sql)
{
    private const int Columns = 60;

    private static LocalizedText L(string en) => new(new Dictionary<string, string> { ["en"] = en });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_перев_язує_групи_стилі_й_батьків_одним_UPDATE_на_таблицю_а_не_на_кожен_рядок()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: Columns, rowCount: 6, ct: ct);
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using (var setup = builder.CreateContext())
        {
            var group = new ColumnHeaderGroupDef(doc.TableDefId, EcrCode.Create("G" + suffix), L("Group"), 1);
            setup.ColumnHeaderGroupDefs.Add(group);
            var style = new StyleDef(doc.TemplateVersionId, EcrCode.Create("S" + suffix));
            style.SetAppearance(null, null, true, false, null, null, null, null, null, false, null);
            setup.StyleDefs.Add(style);
            await setup.SaveChangesAsync(ct);
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.ColumnDef SET HeaderGroupId = {0}, StyleId = CASE WHEN Ordinal % 2 = 0 THEN {1} END WHERE TableDefId = {2}",
                [group.Id, style.Id, doc.TableDefId], ct);
            var rows = await setup.RowDefs.Where(r => r.TableDefId == doc.TableDefId).OrderBy(r => r.Ordinal).ToListAsync(ct);
            for (var i = 1; i < rows.Count; i++)
            {
                rows[i].SetParent(rows[0].Id);
            }

            await setup.SaveChangesAsync(ct);
        }

        var recorder = new StatementRecorder();
        int cloneId;
        await using (var db = sql.CreateContext(recorder))
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"9.{Random.Shared.Next(1, 99999)}.{Random.Shared.Next(1, 99999)}.0", 1,
                new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        // Кількість UPDATE не залежить від кількості колонок: жодного рядкового UPDATE від EF, по одному set-based.
        Assert.Equal(0, recorder.Count(@"UPDATE \[cfg\]\.\[ColumnDef\] SET"));
        Assert.Equal(0, recorder.Count(@"UPDATE \[cfg\]\.\[RowDef\] SET"));
        Assert.Equal(1, recorder.Count(@"UPDATE t SET .* FROM \[cfg\]\.\[ColumnDef\] t"));
        Assert.Equal(1, recorder.Count(@"UPDATE t SET .* FROM \[cfg\]\.\[RowDef\] t"));

        await using var verify = builder.CreateContext();
        var table = await verify.TableDefs.AsNoTracking()
            .SingleAsync(t => verify.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId), ct);
        var cloneGroup = await verify.ColumnHeaderGroupDefs.AsNoTracking().SingleAsync(g => g.TableDefId == table.Id, ct);
        var cloneStyle = await verify.StyleDefs.AsNoTracking().SingleAsync(s => s.TemplateVersionId == cloneId, ct);
        var cols = await verify.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == table.Id).ToListAsync(ct);
        var cloneRows = await verify.RowDefs.AsNoTracking().Where(r => r.TableDefId == table.Id).OrderBy(r => r.Ordinal).ToListAsync(ct);

        Assert.Equal(Columns, cols.Count);
        Assert.All(cols, c => Assert.Equal(cloneGroup.Id, c.HeaderGroupId));
        Assert.All(cols, c => Assert.Equal(c.Ordinal % 2 == 0 ? cloneStyle.Id : null, c.StyleId));
        Assert.Null(cloneRows[0].ParentRowDefId);
        Assert.All(cloneRows.Skip(1), r => Assert.Equal(cloneRows[0].Id, r.ParentRowDefId));
    }

    /// <summary>Тексти всіх команд (читання, NonQuery, скаляр); лічить входження шаблону оператора.</summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];

        public int Count(string pattern)
        {
            lock (_texts)
            {
                return _texts.Sum(t => Regex.Count(t, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(5)));
            }
        }

        private void Add(DbCommand command)
        {
            lock (_texts)
            {
                _texts.Add(command.CommandText);
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
