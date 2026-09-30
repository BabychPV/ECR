using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RK01RegistryKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistryKeyDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegKey_Pri"),
                    IgnoreCase = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegKey_Case"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegKey_Act"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryKeyDef", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegKey_Def",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryEntryKey",
                schema: "dic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryEntryId = table.Column<int>(type: "int", nullable: false),
                    RegistryKeyDefId = table.Column<int>(type: "int", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    KeyText = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    ValidFromKey = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsLive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryEntryKey", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegEntryKey_Entry",
                        column: x => x.RegistryEntryId,
                        principalSchema: "dic",
                        principalTable: "RegistryEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegEntryKey_Key",
                        column: x => x.RegistryKeyDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryKeyDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistryKeyField",
                schema: "cfg",
                columns: table => new
                {
                    RegistryKeyDefId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<byte>(type: "tinyint", nullable: false),
                    RegistryFieldDefId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryKeyField", x => new { x.RegistryKeyDefId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_RegKeyField_Field",
                        column: x => x.RegistryFieldDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryFieldDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegKeyField_Key",
                        column: x => x.RegistryKeyDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryKeyDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistryEntryKey_Hash",
                schema: "dic",
                table: "RegistryEntryKey",
                columns: new[] { "RegistryKeyDefId", "KeyHash" })
                .Annotation("SqlServer:Include", new[] { "RegistryEntryId", "ValidFromKey", "ValidTo", "IsLive" });

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryEntryKey_Entry",
                schema: "dic",
                table: "RegistryEntryKey",
                columns: new[] { "RegistryKeyDefId", "RegistryEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RegistryEntryKey_Live",
                schema: "dic",
                table: "RegistryEntryKey",
                columns: new[] { "RegistryKeyDefId", "KeyHash", "ValidFromKey" },
                unique: true,
                filter: "[IsLive] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryKeyDef",
                schema: "cfg",
                table: "RegistryKeyDef",
                columns: new[] { "RegistryDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RegistryKeyDef_Primary",
                schema: "cfg",
                table: "RegistryKeyDef",
                column: "RegistryDefId",
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryKeyField_Field",
                schema: "cfg",
                table: "RegistryKeyField",
                columns: new[] { "RegistryKeyDefId", "RegistryFieldDefId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistryEntryKey",
                schema: "dic");

            migrationBuilder.DropTable(
                name: "RegistryKeyField",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "RegistryKeyDef",
                schema: "cfg");
        }
    }
}
