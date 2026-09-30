using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RK05RegistryImportProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistryImportProfile",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SpecJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryImportProfile", x => x.Id);
                    table.CheckConstraint("CK_RegImpProfile_Json", "ISJSON([SpecJson]) = 1");
                    table.ForeignKey(
                        name: "FK_RegImpProfile_Def",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryImportProfile",
                schema: "cfg",
                table: "RegistryImportProfile",
                columns: new[] { "RegistryDefId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistryImportProfile",
                schema: "cfg");
        }
    }
}
