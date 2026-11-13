// src/Ecr.Infrastructure/Persistence/TemplateVersionDiscardStore.cs
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ITemplateVersionDiscardStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Усі зовнішні ключі на структуру версії — <c>NO ACTION</c>, тож вміст видаляється явно, від листя
/// до кореня, ОДНИМ пакетом. Межа проста: усе, що лежить у <c>cfg.*</c> і належить версії (те саме,
/// що копіює клон, <c>TemplateVersionStore.CloneAsync</c>), видаляється; усе поза конфігурацією
/// (дані документів, проєкти, мапінги, входи методик, клони) — причина відмови
/// (<see cref="FindUsagesAsync"/>). Посилання, якого немає в жодному переліку, не мовчить: базу зупинить
/// FK (Msg 547), і це теж відмова «версію використовують», а не 500. Повноту обох переліків проти
/// живого графа FK тримає <c>TemplateVersionDiscardCoverageTests</c>.
/// </remarks>
public sealed class TemplateVersionDiscardStore(EcrDbContext db) : ITemplateVersionDiscardStore
{
    /// <summary>Таблиці, рядки яких каскад видаляє разом із версією.</summary>
    public static readonly IReadOnlySet<string> CascadeTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "cfg.TemplateVersion", "cfg.SheetDef", "cfg.TableDef", "cfg.ColumnDef", "cfg.RowDef",
        "cfg.ColumnHeaderGroupDef", "cfg.FormulaDef", "cfg.FormulaDependency", "cfg.ValidationRule",
        "cfg.TableRelationDef", "cfg.PeriodAccessRuleDef", "cfg.CalculationBinding", "cfg.PermitSheetBinding",
        "cfg.PermitSheetBindingColumn", "cfg.ConditionalFormatRule", "cfg.SheetGroupRule", "cfg.HeaderFieldDef",
        "cfg.StyleDef", "sec.ResourceGrant",

        // Land-year L0 (таблиця з PT01): псевдоніми аргументів методологій на місячні колонки — конфігурація версії.
        "cfg.MonthInputAlias",
    };

    /// <summary>Таблиці поза конфігурацією версії: рядок, що посилається на версію, — відмова.</summary>
    public static readonly IReadOnlySet<string> UsageTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "doc.Project", "doc.ProjectTemplateVersion", "cfg.TemplateVersion", "wf.ApprovalRoute",
        "doc.ProjectTemplate",   // B74 PT01: версія — чинна версія шаблону в проєкті
        "calc.SubmissionSnapshot", "doc.DocumentSheet", "wf.ApprovalEvent", "wf.ApprovalState",
        "ext.LegacySheetMapping", "doc.TableInstance", "arc.TableInstance", "ext.LegacyTableMapping",
        "ext.RowWindowMap", "ext.SourceEventMap", "doc.DocumentHeaderValue",
        "doc.TableRow", "ext.LegacyRowMapping", "doc.DocumentIndexValue", "calc.MethodologyRequiredInput",
        "ext.EntityFieldMap", "ext.LegacyColumnMapping", "ext.RowWindowValue", "ext.SourceEventFieldMap",
        "cfg.FormulaDependency", "cfg.TableRelationDef", "cfg.ColumnDef",
    };

    /// <summary>
    /// Дані документа, які без примірника таблиці (<c>doc.TableInstance</c>, у <see cref="UsageTables"/>)
    /// існувати не можуть. Окремо їх не рахуємо: <c>doc.CellValue</c> — мільйони рядків без індексу за
    /// колонкою, а останній рубіж — той самий FK.
    /// </summary>
    public static readonly IReadOnlySet<string> CoveredByTableInstance = new HashSet<string>(StringComparer.Ordinal)
    {
        "doc.CellValue",

        // Feature (j): parts of month values split between permits — a document's data of a table instance (the
        // month cell they split lives in doc.CellValue), never without one.
        "doc.CellPermitPart",
    };

    // Множини Id структури версії: аркуші, таблиці, колонки, рядки, формули, поля шапки, прив'язки пермітів.
    private const string ScopeSql = """
        SET NOCOUNT ON;
        DECLARE @S TABLE (Id int PRIMARY KEY);
        INSERT @S SELECT Id FROM cfg.SheetDef WHERE TemplateVersionId = @v;
        DECLARE @T TABLE (Id int PRIMARY KEY);
        INSERT @T SELECT Id FROM cfg.TableDef WHERE SheetDefId IN (SELECT Id FROM @S);
        DECLARE @C TABLE (Id int PRIMARY KEY);
        INSERT @C SELECT Id FROM cfg.ColumnDef WHERE TableDefId IN (SELECT Id FROM @T);
        DECLARE @R TABLE (Id int PRIMARY KEY);
        INSERT @R SELECT Id FROM cfg.RowDef WHERE TableDefId IN (SELECT Id FROM @T);
        DECLARE @F TABLE (Id int PRIMARY KEY);
        INSERT @F SELECT Id FROM cfg.FormulaDef WHERE TableDefId IN (SELECT Id FROM @T);
        DECLARE @H TABLE (Id int PRIMARY KEY);
        INSERT @H SELECT Id FROM cfg.HeaderFieldDef WHERE TemplateVersionId = @v;
        DECLARE @out TABLE (Tbl nvarchar(128) NOT NULL, N int NOT NULL);
        """;

    private const string UsageSql = ScopeSql + """

        INSERT @out SELECT N'doc.Project', COUNT(*) FROM doc.Project WHERE TemplateVersionId = @v;
        INSERT @out SELECT N'doc.ProjectTemplateVersion', COUNT(*) FROM doc.ProjectTemplateVersion WHERE TemplateVersionId = @v;
        -- B74 PT01: версія, що є чинною версією шаблону в проєкті, використовується (FK_ProjectTemplate_TemplateVersion).
        INSERT @out SELECT N'doc.ProjectTemplate', COUNT(*) FROM doc.ProjectTemplate WHERE CurrentTemplateVersionId = @v;
        INSERT @out SELECT N'cfg.TemplateVersion', COUNT(*) FROM cfg.TemplateVersion WHERE ClonedFromVersionId = @v;
        INSERT @out SELECT N'wf.ApprovalRoute', COUNT(*) FROM wf.ApprovalRoute WHERE TemplateVersionId = @v;
        INSERT @out SELECT N'calc.SubmissionSnapshot', COUNT(*) FROM calc.SubmissionSnapshot
            WHERE TemplateVersionId = @v OR SheetDefId IN (SELECT Id FROM @S);
        INSERT @out SELECT N'doc.DocumentSheet', COUNT(*) FROM doc.DocumentSheet WHERE SheetDefId IN (SELECT Id FROM @S);
        INSERT @out SELECT N'wf.ApprovalEvent', COUNT(*) FROM wf.ApprovalEvent WHERE SheetDefId IN (SELECT Id FROM @S);
        INSERT @out SELECT N'wf.ApprovalState', COUNT(*) FROM wf.ApprovalState WHERE SheetDefId IN (SELECT Id FROM @S);
        INSERT @out SELECT N'ext.LegacySheetMapping', COUNT(*) FROM ext.LegacySheetMapping WHERE SheetDefId IN (SELECT Id FROM @S);
        INSERT @out SELECT N'doc.TableInstance', COUNT(*) FROM doc.TableInstance WHERE TableDefId IN (SELECT Id FROM @T);
        -- Архівна таблиця з'являється разом з архівом року; на базі без архіву її може не бути.
        IF OBJECT_ID(N'arc.TableInstance', N'U') IS NOT NULL
            INSERT @out SELECT N'arc.TableInstance', COUNT(*) FROM arc.TableInstance WHERE TableDefId IN (SELECT Id FROM @T);
        INSERT @out SELECT N'ext.LegacyTableMapping', COUNT(*) FROM ext.LegacyTableMapping WHERE TableDefId IN (SELECT Id FROM @T);
        INSERT @out SELECT N'ext.RowWindowMap', COUNT(*) FROM ext.RowWindowMap
            WHERE TableDefId IN (SELECT Id FROM @T) OR StartColumnDefId IN (SELECT Id FROM @C)
               OR EndColumnDefId IN (SELECT Id FROM @C) OR SelectorColumnDefId IN (SELECT Id FROM @C)
               OR TargetColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'ext.SourceEventMap', COUNT(*) FROM ext.SourceEventMap WHERE TableDefId IN (SELECT Id FROM @T);
        INSERT @out SELECT N'doc.DocumentHeaderValue', COUNT(*) FROM doc.DocumentHeaderValue WHERE HeaderFieldDefId IN (SELECT Id FROM @H);
        INSERT @out SELECT N'doc.TableRow', COUNT(*) FROM doc.TableRow WHERE RowDefId IN (SELECT Id FROM @R);
        INSERT @out SELECT N'ext.LegacyRowMapping', COUNT(*) FROM ext.LegacyRowMapping WHERE RowDefId IN (SELECT Id FROM @R);
        INSERT @out SELECT N'doc.DocumentIndexValue', COUNT(*) FROM doc.DocumentIndexValue WHERE ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'calc.MethodologyRequiredInput', COUNT(*) FROM calc.MethodologyRequiredInput WHERE ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'ext.EntityFieldMap', COUNT(*) FROM ext.EntityFieldMap WHERE TargetColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'ext.LegacyColumnMapping', COUNT(*) FROM ext.LegacyColumnMapping WHERE ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'ext.RowWindowValue', COUNT(*) FROM ext.RowWindowValue WHERE ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out SELECT N'ext.SourceEventFieldMap', COUNT(*) FROM ext.SourceEventFieldMap WHERE TargetColumnDefId IN (SELECT Id FROM @C);
        -- Структура ІНШИХ версій, що дивиться на цю: залежність чужої формули, зв'язок з однією стороною тут,
        -- каскад чужої колонки від нашої. Каскад їх не чіпає — це відмова.
        INSERT @out SELECT N'cfg.FormulaDependency', COUNT(*) FROM cfg.FormulaDependency
            WHERE (ColumnDefId IN (SELECT Id FROM @C) OR TableDefId IN (SELECT Id FROM @T))
              AND FormulaDefId NOT IN (SELECT Id FROM @F);
        INSERT @out SELECT N'cfg.TableRelationDef', COUNT(*) FROM cfg.TableRelationDef
            WHERE (CASE WHEN SourceTableDefId IN (SELECT Id FROM @T) THEN 1 ELSE 0 END)
                + (CASE WHEN TargetTableDefId IN (SELECT Id FROM @T) THEN 1 ELSE 0 END) = 1;
        INSERT @out SELECT N'cfg.ColumnDef', COUNT(*) FROM cfg.ColumnDef
            WHERE CascadeFromColumnId IN (SELECT Id FROM @C) AND Id NOT IN (SELECT Id FROM @C);
        SELECT Tbl, N FROM @out WHERE N > 0;
        """;

    private const string DeleteSql = ScopeSql + """

        DECLARE @B TABLE (Id int PRIMARY KEY);
        INSERT @B SELECT Id FROM cfg.PermitSheetBinding
            WHERE TemplateVersionId = @v OR SheetTableDefId IN (SELECT Id FROM @T) OR HeaderFieldDefId IN (SELECT Id FROM @H);

        -- ⛔ TRY/THROW: помилка FK (547) перериває лише ОПЕРАТОР, і без цього решта пакета йшла б далі.
        BEGIN TRY
        DELETE FROM cfg.FormulaDependency WHERE FormulaDefId IN (SELECT Id FROM @F);
        INSERT @out VALUES (N'cfg.FormulaDependency', @@ROWCOUNT);
        DELETE FROM cfg.FormulaDef WHERE Id IN (SELECT Id FROM @F);
        INSERT @out VALUES (N'cfg.FormulaDef', @@ROWCOUNT);
        DELETE FROM cfg.ValidationRule WHERE TableDefId IN (SELECT Id FROM @T);
        INSERT @out VALUES (N'cfg.ValidationRule', @@ROWCOUNT);
        DELETE FROM cfg.TableRelationDef
            WHERE SourceTableDefId IN (SELECT Id FROM @T) AND TargetTableDefId IN (SELECT Id FROM @T);
        INSERT @out VALUES (N'cfg.TableRelationDef', @@ROWCOUNT);
        DELETE FROM cfg.PeriodAccessRuleDef
            WHERE TemplateVersionId = @v OR SheetDefId IN (SELECT Id FROM @S)
               OR TableDefId IN (SELECT Id FROM @T) OR SourceColumnDefId IN (SELECT Id FROM @C);
        INSERT @out VALUES (N'cfg.PeriodAccessRuleDef', @@ROWCOUNT);
        DELETE FROM cfg.MonthInputAlias WHERE TableDefId IN (SELECT Id FROM @T) OR ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out VALUES (N'cfg.MonthInputAlias', @@ROWCOUNT);
        DELETE FROM cfg.CalculationBinding WHERE TableDefId IN (SELECT Id FROM @T) OR ColumnDefId IN (SELECT Id FROM @C);
        INSERT @out VALUES (N'cfg.CalculationBinding', @@ROWCOUNT);
        DELETE FROM cfg.PermitSheetBindingColumn WHERE BindingId IN (SELECT Id FROM @B) OR SheetColumnDefId IN (SELECT Id FROM @C);
        INSERT @out VALUES (N'cfg.PermitSheetBindingColumn', @@ROWCOUNT);
        DELETE FROM cfg.PermitSheetBinding WHERE Id IN (SELECT Id FROM @B);
        INSERT @out VALUES (N'cfg.PermitSheetBinding', @@ROWCOUNT);
        DELETE FROM cfg.ConditionalFormatRule WHERE TemplateVersionId = @v;
        INSERT @out VALUES (N'cfg.ConditionalFormatRule', @@ROWCOUNT);
        DELETE FROM cfg.SheetGroupRule WHERE TemplateVersionId = @v;
        INSERT @out VALUES (N'cfg.SheetGroupRule', @@ROWCOUNT);
        -- Ресурсні гранти (Sheet = 1, Table = 2, Column = 3) не мають FK: без цього лишилися б гранти на Id, яких немає.
        DELETE FROM sec.ResourceGrant
            WHERE (ResourceKind = 1 AND ResourceId IN (SELECT Id FROM @S))
               OR (ResourceKind = 2 AND ResourceId IN (SELECT Id FROM @T))
               OR (ResourceKind = 3 AND ResourceId IN (SELECT Id FROM @C));
        INSERT @out VALUES (N'sec.ResourceGrant', @@ROWCOUNT);
        DELETE FROM cfg.RowDef WHERE Id IN (SELECT Id FROM @R);
        INSERT @out VALUES (N'cfg.RowDef', @@ROWCOUNT);
        DELETE FROM cfg.ColumnDef WHERE Id IN (SELECT Id FROM @C);
        INSERT @out VALUES (N'cfg.ColumnDef', @@ROWCOUNT);
        DELETE FROM cfg.ColumnHeaderGroupDef WHERE TableDefId IN (SELECT Id FROM @T);
        INSERT @out VALUES (N'cfg.ColumnHeaderGroupDef', @@ROWCOUNT);
        DELETE FROM cfg.TableDef WHERE Id IN (SELECT Id FROM @T);
        INSERT @out VALUES (N'cfg.TableDef', @@ROWCOUNT);
        DELETE FROM cfg.SheetDef WHERE Id IN (SELECT Id FROM @S);
        INSERT @out VALUES (N'cfg.SheetDef', @@ROWCOUNT);
        DELETE FROM cfg.HeaderFieldDef WHERE Id IN (SELECT Id FROM @H);
        INSERT @out VALUES (N'cfg.HeaderFieldDef', @@ROWCOUNT);
        DELETE FROM cfg.StyleDef WHERE TemplateVersionId = @v;
        INSERT @out VALUES (N'cfg.StyleDef', @@ROWCOUNT);
        -- Status = 0 (Draft): навіть якщо перевірку домену колись обійдуть, опубліковану версію пакет не видалить.
        DELETE FROM cfg.TemplateVersion WHERE Id = @v AND Status = 0;
        INSERT @out VALUES (N'cfg.TemplateVersion', @@ROWCOUNT);
        END TRY
        BEGIN CATCH
            THROW;
        END CATCH;
        SELECT Tbl, N FROM @out WHERE N > 0;
        """;

    /// <inheritdoc />
    public async Task<TemplateVersion?> LockAsync(int templateVersionId, CancellationToken ct)
        // UPDLOCK, HOLDLOCK: паралельні публікація, клон і правка чернетки чекають, доки видалення завершиться
        // (вони блокують той самий рядок версії першим, `DraftVersionLock`).
        => await db.TemplateVersions
            .FromSql($"SELECT * FROM cfg.TemplateVersion WITH (UPDLOCK, HOLDLOCK) WHERE Id = {templateVersionId}")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> FindUsagesAsync(int templateVersionId, CancellationToken ct)
        => RunAsync(UsageSql, templateVersionId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> DeleteAsync(int templateVersionId, CancellationToken ct)
    {
        try
        {
            return await RunAsync(DeleteSql, templateVersionId, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            // Посилання поза обома переліками: FK зупинив видалення. Це «версію використовують», не 500.
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Чернетку не можна відкинути: на її структуру посилається запис поза конфігурацією ({ex.Message}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0409.discardVersionInUse",
                    ["version"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["usages"] = ConstraintOf(ex.Message),
                });
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> RunAsync(string sql, int templateVersionId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 600;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@v";
            parameter.Value = templateVersionId;
            command.Parameters.Add(parameter);

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = reader.GetInt32(1);
            }

            return result;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string ConstraintOf(string message)
    {
        const string marker = "constraint \"";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "FK";
        }

        start += marker.Length;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : "FK";
    }
}
