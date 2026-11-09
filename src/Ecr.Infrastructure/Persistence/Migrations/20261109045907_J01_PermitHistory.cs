using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class J01_PermitHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CellPermitPart",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefId = table.Column<int>(type: "int", nullable: false),
                    SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColumnDefId = table.Column<int>(type: "int", nullable: false),
                    PartNo = table.Column<byte>(type: "tinyint", nullable: false),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false),
                    DayFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DayTo = table.Column<DateOnly>(type: "date", nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(34,16)", precision: 34, scale: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellPermitPart", x => new { x.PeriodKey, x.Id });
                    table.CheckConstraint("CK_CellPermitPart_Days", "DayTo >= DayFrom");
                    table.CheckConstraint("CK_CellPermitPart_PartNo", "PartNo >= 1");
                    table.ForeignKey(
                        name: "FK_CellPermitPart_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CellPermitPart_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CellPermitPart_Table",
                        column: x => x.TableDefId,
                        principalSchema: "cfg",
                        principalTable: "TableDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitSegment",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    HeaderFieldDefId = table.Column<int>(type: "int", nullable: true),
                    TableDefId = table.Column<int>(type: "int", nullable: true),
                    SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ColumnDefId = table.Column<int>(type: "int", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitSegment", x => x.Id);
                    table.CheckConstraint("CK_PermitSegment_Target", "(HeaderFieldDefId IS NOT NULL AND TableDefId IS NULL AND RowKey IS NULL AND ColumnDefId IS NULL AND SheetCopyId IS NULL) OR (HeaderFieldDefId IS NULL AND TableDefId IS NOT NULL AND RowKey IS NOT NULL AND ColumnDefId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PermitSegment_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitSegment_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitSegment_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_CellPermitPart_Cell",
                schema: "doc",
                table: "CellPermitPart",
                columns: new[] { "PeriodKey", "DocumentId", "TableDefId", "SheetCopyId", "RowKey", "ColumnDefId", "PartNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PermitSegment_Header",
                schema: "doc",
                table: "PermitSegment",
                columns: new[] { "DocumentId", "HeaderFieldDefId", "ValidFrom" },
                unique: true,
                filter: "[HeaderFieldDefId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_PermitSegment_Row",
                schema: "doc",
                table: "PermitSegment",
                columns: new[] { "DocumentId", "TableDefId", "SheetCopyId", "RowKey", "ColumnDefId", "ValidFrom" },
                unique: true,
                filter: "[TableDefId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CellPermitPart",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "PermitSegment",
                schema: "doc");
        }
    }
}
