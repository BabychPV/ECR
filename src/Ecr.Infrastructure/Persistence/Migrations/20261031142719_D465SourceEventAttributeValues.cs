using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D465SourceEventAttributeValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceEventAttributeValue",
                schema: "ext",
                columns: table => new
                {
                    LinkId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<byte>(type: "tinyint", nullable: false),
                    Attribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true),
                    ValueText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsTruncated = table.Column<bool>(type: "bit", nullable: false),
                    SourceUnit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ValueHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SourceModifiedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RunId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEventAttributeValue", x => new { x.LinkId, x.Scope, x.Attribute });
                    table.CheckConstraint("CK_SEAV_Scope", "Scope BETWEEN 0 AND 1");
                });

            // ✎ Аудит 31.10 (F4): на наявній таблиці зв'язків — сирим SQL, ONLINE де є, з перевіркою наявності
            // (той самий прийом, що `D463RawDataReadIndexes`/`AN34CollectionCoverageDedupIndex`).
            migrationBuilder.Sql(CreateMapStartIndexSql(D463RawDataReadIndexes.EngineEditionExpression));
        }

        /// <summary>Ім'я індексу keyset-сторінки зв'язків мапінгу.</summary>
        public const string MapStartIndexName = "IX_SEL_MapStart";

        /// <summary>T-SQL, що створює <c>IX_SEL_MapStart</c>, якщо його ще немає, для заданої редакції сервера.</summary>
        /// <param name="engineEdition">Вираз T-SQL типу <c>int</c>.</param>
        /// <returns>Текст T-SQL.</returns>
        public static string CreateMapStartIndexSql(string engineEdition)
            => $"""
                DECLARE @d465Edition int = {engineEdition};
                DECLARE @d465Online nvarchar(40) =
                    CASE WHEN @d465Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
                DECLARE @d465Create nvarchar(max) =
                      N'CREATE NONCLUSTERED INDEX {MapStartIndexName} ON ext.SourceEventLink (SourceEventMapId, StartUtc, Id) '
                    + N'INCLUDE (Status, PrimaryElement, EventName)'
                    + @d465Online
                    + N';';

                IF INDEXPROPERTY(OBJECT_ID(N'ext.SourceEventLink'), N'{MapStartIndexName}', 'IndexID') IS NULL
                    EXEC sys.sp_executesql @d465Create;
                """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceEventAttributeValue",
                schema: "ext");

            migrationBuilder.DropIndex(
                name: "IX_SEL_MapStart",
                schema: "ext",
                table: "SourceEventLink");
        }
    }
}
