using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// CL-10b: індекс <c>IX_CellChange_DocPeriodChanged</c> під карту застарілості (<c>StaleResultsMapCache</c>:
/// <c>DocumentId</c>, <c>PeriodKey = @p</c>, <c>ChangedAt &gt; StartedAt</c>, фільтр <c>Origin</c>, автори) є і в
/// <c>aud.CellChange</c>, і в дзеркалі <c>arc.AuditCellChange</c> — однаковий, ідемпотентний, вирівняний по
/// <c>ps_AuditByMonth</c>.
/// </summary>
/// <remarks>
/// ⛔ Розбіжність між <c>11-audit-tables.sql</c> і <c>12-archive-tables.sql</c> валить SWITCH партиції в архів
/// (Msg 4913). Мутація: прибрати блок з 12-archive-tables.sql - червоний рядок archive.
/// </remarks>
public sealed class AuditDocPeriodIndexTests
{
    [Theory]
    [InlineData("11-audit-tables.sql", "aud.CellChange")]
    [InlineData("12-archive-tables.sql", "arc.AuditCellChange")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Індекс_документ_період_час_є_ідемпотентний_дзеркальний_і_вирівняний(string script, string table)
    {
        var path = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", script);
        var sql = Regex.Replace(File.ReadAllText(path), @"\s+", " ");

        Assert.Contains(
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_DocPeriodChanged' "
            + $"AND object_id = OBJECT_ID(N'{table}'))",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"N'CREATE INDEX IX_CellChange_DocPeriodChanged ' "
            + $"+ N'ON {table} (DocumentId, PeriodKey, ChangedAt) INCLUDE (Origin, ChangedByUserId)' "
            + "+ CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) "
            + "THEN N' WITH (ONLINE = ON)' ELSE N'' END + N' ON ps_AuditByMonth(ChangedAt);'",
            sql,
            StringComparison.Ordinal);
    }
}
