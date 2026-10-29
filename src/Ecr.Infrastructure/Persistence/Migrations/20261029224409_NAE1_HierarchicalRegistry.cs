using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NAE1_HierarchicalRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LevelId",
                schema: "dic",
                table: "RegistryEntry",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHierarchical",
                schema: "cfg",
                table: "RegistryDef",
                type: "bit",
                nullable: false,
                defaultValue: false)
                .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Hier");

            migrationBuilder.CreateTable(
                name: "RegistryLevelDef",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistryDefId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_RegLevel_Opt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryLevelDef", x => x.Id);
                    table.CheckConstraint("CK_RegLevel_Ordinal", "Ordinal >= 1");
                    table.ForeignKey(
                        name: "FK_RegLevel_Registry",
                        column: x => x.RegistryDefId,
                        principalSchema: "cfg",
                        principalTable: "RegistryDef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistryEntry_Level",
                schema: "dic",
                table: "RegistryEntry",
                column: "LevelId",
                filter: "[LevelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistryEntry_Parent",
                schema: "dic",
                table: "RegistryEntry",
                columns: new[] { "RegistryDefId", "ParentEntryId" },
                filter: "[ParentEntryId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "Ordinal", "Code", "IsActive", "IsDeleted", "LevelId" });

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryLevelDef_Code",
                schema: "cfg",
                table: "RegistryLevelDef",
                columns: new[] { "RegistryDefId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RegistryLevelDef_Ordinal",
                schema: "cfg",
                table: "RegistryLevelDef",
                columns: new[] { "RegistryDefId", "Ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RegEntry_Level",
                schema: "dic",
                table: "RegistryEntry",
                column: "LevelId",
                principalSchema: "cfg",
                principalTable: "RegistryLevelDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RegEntry_Level",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropTable(
                name: "RegistryLevelDef",
                schema: "cfg");

            migrationBuilder.DropIndex(
                name: "IX_RegistryEntry_Level",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropIndex(
                name: "IX_RegistryEntry_Parent",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropColumn(
                name: "LevelId",
                schema: "dic",
                table: "RegistryEntry");

            migrationBuilder.DropColumn(
                name: "IsHierarchical",
                schema: "cfg",
                table: "RegistryDef")
                .Annotation("Relational:DefaultConstraintName", "DF_RegDef_Hier");
        }
    }
}
