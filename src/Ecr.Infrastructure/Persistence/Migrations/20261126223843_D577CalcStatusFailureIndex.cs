﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ D-577 (огляд M7): <c>itg.JobProgress (DocumentId, State, UpdatedAt) INCLUDE (JobCode) WHERE DocumentId IS NOT NULL</c> —
    /// чип «Стан розрахунку» читає останній провал перерахунку документа після його актуального прогону
    /// (<c>DocumentCalcStatusStore.LastFailureAsync</c>) пошуком, а не сканом історії черги (30 днів).
    /// </summary>
    /// <remarks>
    /// <para>⚠ Дані міграція не змінює, індекс не унікальний — передперевірки немає. Відкат (<c>Down</c>) — <c>DROP INDEX</c>.</para>
    /// <para>
    /// ⛔ Сирим SQL, як <c>PSS04JobQueueDocumentIndex</c>: <c>ONLINE = ON</c> лише там, де він є (<c>EngineEdition</c> 3, 5, 8;
    /// на Standard і Express — офлайн), наявний індекс (зібраний DBA чи повторний запуск після збою) нічого не ламає.
    /// </para>
    /// <para>⚠ Фільтрований індекс: сеанс, що пише в таблицю, має <c>QUOTED_IDENTIFIER ON</c> (SqlClient — типово; <c>sqlcmd -I</c>).</para>
    /// <para>Модель (<c>JobProgressConfiguration</c>) оголошує той самий індекс — знімок збігається з базою.</para>
    /// </remarks>
    public partial class D577CalcStatusFailureIndex : Migration
    {
        /// <summary>Ім'я індексу.</summary>
        public const string IndexName = "IX_JobProgress_Document_State_UpdatedAt";

        /// <summary>T-SQL, що створює індекс, якщо його ще немає (ONLINE, де він є).</summary>
        public const string CreateIndexSql = $"""
            DECLARE @d577Edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
            DECLARE @d577Online nvarchar(40) =
                CASE WHEN @d577Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
            DECLARE @d577Create nvarchar(max) =
                  N'CREATE NONCLUSTERED INDEX {IndexName} ON itg.JobProgress (DocumentId, [State], UpdatedAt) '
                + N'INCLUDE (JobCode) WHERE [DocumentId] IS NOT NULL'
                + @d577Online
                + N';';

            IF INDEXPROPERTY(OBJECT_ID(N'itg.JobProgress'), N'{IndexName}', 'IndexID') IS NULL
                EXEC sys.sp_executesql @d577Create;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: IndexName,
                schema: "itg",
                table: "JobProgress");
        }
    }
}
