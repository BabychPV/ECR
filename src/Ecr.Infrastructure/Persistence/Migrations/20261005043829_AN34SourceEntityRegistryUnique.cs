using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// AN-34 L4-01 (аудит 1D): фільтрований унікальний <c>UQ_SourceEntity_Registry</c> на
    /// <c>ext.SourceEntity (DataSourceId, RegistryDefId) WHERE RegistryDefId IS NOT NULL</c> -
    /// одна сутність збору на довідник у з'єднанні.
    /// </summary>
    /// <remarks>
    /// ⛔ Передперевірка йде ПЕРШОЮ командою: база, де дві сутності одного з'єднання вже
    /// прив'язані до одного довідника, зупиняється <c>THROW 50401</c> з переліком (з'єднання,
    /// довідник, сутності), а не SQL-помилкою 1505 без імен. Нічого не видаляється й не
    /// відв'язується автоматично: яку з сутностей лишити, вирішує людина (runbook §8.4).
    /// ⚠ Механізм той самий, що в U1 (<c>U1UnitForeignKeys</c>) і Q222: <c>THROW</c> власного
    /// номера з переліком до зміни схеми; <c>FOR XML PATH</c>, а не <c>STRING_AGG</c> (підлога
    /// сервера - 2016 SP1, <c>D-101</c>). Перевірка лежить у тілі міграції, тож обидва шляхи
    /// застосування - <c>MigrateAsync</c> і ідемпотентний <c>migration.sql</c> - виконують її.
    /// </remarks>
    public partial class AN34SourceEntityRegistryUnique : Migration
    {
        /// <summary>Номер помилки передперевірки: «дві сутності на один довідник» (AN-34 L4-01).</summary>
        public const int PrecheckErrorNumber = 50401;

        /// <summary>Скільки груп-порушників показувати.</summary>
        private const int SampleSize = 10;

        /// <summary>T-SQL передперевірки: <c>THROW 50401</c> з переліком, якщо є дублі.</summary>
        public static string PrecheckSql { get; } = BuildPrecheckSql();

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrecheckSql);

            migrationBuilder.CreateIndex(
                name: "UQ_SourceEntity_Registry",
                schema: "ext",
                table: "SourceEntity",
                columns: new[] { "DataSourceId", "RegistryDefId" },
                unique: true,
                filter: "[RegistryDefId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_SourceEntity_Registry",
                schema: "ext",
                table: "SourceEntity");
        }

        private static string BuildPrecheckSql()
            => $"""
                DECLARE @an34Count int, @an34Rows nvarchar(max);

                SELECT @an34Count = COUNT(*)
                FROM (
                    SELECT 1 AS One
                    FROM ext.SourceEntity
                    WHERE RegistryDefId IS NOT NULL
                    GROUP BY DataSourceId, RegistryDefId
                    HAVING COUNT(*) > 1
                ) AS g;

                IF @an34Count > 0
                BEGIN
                    SET @an34Rows = STUFF((
                        SELECT TOP ({SampleSize})
                            N'; (з''єднання ' + CONVERT(nvarchar(20), s.DataSourceId)
                            + N', довідник ' + CONVERT(nvarchar(20), s.RegistryDefId)
                            + N', сутності: '
                            + STUFF((
                                SELECT N', ' + CONVERT(nvarchar(20), t.Id) + N' ' + t.Code
                                       + CASE WHEN t.IsActive = 1 THEN N'' ELSE N' (вимкнена)' END
                                FROM ext.SourceEntity AS t
                                WHERE t.DataSourceId = s.DataSourceId AND t.RegistryDefId = s.RegistryDefId
                                ORDER BY t.Id
                                FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'')
                            + N')'
                        FROM ext.SourceEntity AS s
                        WHERE s.RegistryDefId IS NOT NULL
                        GROUP BY s.DataSourceId, s.RegistryDefId
                        HAVING COUNT(*) > 1
                        ORDER BY s.DataSourceId, s.RegistryDefId
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

                    DECLARE @an34Message nvarchar(2048) = LEFT(
                          N'Передперевірка AN-34 L4-01: оновлення зупинено ДО зміни схеми. '
                        + N'Міграція AN34SourceEntityRegistryUnique додає унікальний індекс UQ_SourceEntity_Registry '
                        + N'(одна сутність збору на довідник у з''єднанні), а в ext.SourceEntity є сутності одного '
                        + N'з''єднання, прив''язані до одного довідника; SQL Server дав би 1505 без переліку. '
                        + N'Груп (з''єднання, довідник): ' + CONVERT(nvarchar(20), @an34Count) + N'; перші: ' + ISNULL(@an34Rows, N'')
                        + NCHAR(10) + N'Схему й дані не змінено, автоматичного відв''язування немає. '
                        + N'Що робити: відв''яжіть зайву сутність від довідника (PUT /sources/ID/registry з registryDefId = null) '
                        + N'і повторіть оновлення, див. docs/admin/operations-runbook.md, п. 8.4.',
                        2048);
                    THROW {PrecheckErrorNumber}, @an34Message, 1;
                END
                """;
    }
}
