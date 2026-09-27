using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RK02RegistryComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "RegistryEntryCodeSeq",
                schema: "dic");

            migrationBuilder.AddColumn<byte>(
                name: "OnParentDelete",
                schema: "cfg",
                table: "RegistryFieldDef",
                type: "tinyint",
                nullable: false,
                defaultValueSql: "0")
                .Annotation("Relational:DefaultConstraintName", "DF_RegField_OnDel");

            migrationBuilder.AddColumn<byte>(
                name: "RelationKind",
                schema: "cfg",
                table: "RegistryFieldDef",
                type: "tinyint",
                nullable: false,
                defaultValueSql: "0")
                .Annotation("Relational:DefaultConstraintName", "DF_RegField_Rel");

            migrationBuilder.AddColumn<byte>(
                name: "CodeMode",
                schema: "cfg",
                table: "RegistryDef",
                type: "tinyint",
                nullable: false,
                defaultValueSql: "0")
                .Annotation("Relational:DefaultConstraintName", "DF_RegDef_CodeMode");

            migrationBuilder.AddColumn<DateTime>(
                name: "DataChangedAt",
                schema: "cfg",
                table: "RegistryDef",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RegField_Composition",
                schema: "cfg",
                table: "RegistryFieldDef",
                sql: "RelationKind = 0 OR DataType = 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RegField_Rel",
                schema: "cfg",
                table: "RegistryFieldDef",
                sql: "RelationKind BETWEEN 0 AND 1 AND OnParentDelete BETWEEN 0 AND 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RegField_Composition",
                schema: "cfg",
                table: "RegistryFieldDef");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RegField_Rel",
                schema: "cfg",
                table: "RegistryFieldDef");

            migrationBuilder.DropColumn(
                name: "OnParentDelete",
                schema: "cfg",
                table: "RegistryFieldDef")
                .Annotation("Relational:DefaultConstraintName", "DF_RegField_OnDel");

            migrationBuilder.DropColumn(
                name: "RelationKind",
                schema: "cfg",
                table: "RegistryFieldDef")
                .Annotation("Relational:DefaultConstraintName", "DF_RegField_Rel");

            migrationBuilder.DropColumn(
                name: "CodeMode",
                schema: "cfg",
                table: "RegistryDef")
                .Annotation("Relational:DefaultConstraintName", "DF_RegDef_CodeMode");

            migrationBuilder.DropColumn(
                name: "DataChangedAt",
                schema: "cfg",
                table: "RegistryDef");

            migrationBuilder.DropSequence(
                name: "RegistryEntryCodeSeq",
                schema: "dic");
        }
    }
}
