using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Q222MissingForeignKeysAndConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DocSheet_Document",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropForeignKey(
                name: "FK_Unit_Dimension",
                schema: "uom",
                table: "Unit");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignment_Sid",
                schema: "sec",
                table: "RoleAssignment");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignment_User",
                schema: "sec",
                table: "RoleAssignment");

            migrationBuilder.AddColumn<string>(
                name: "CategoryNorm",
                schema: "calc",
                table: "MethodologyConstant",
                type: "nvarchar(450)",
                nullable: false,
                computedColumnSql: "ISNULL([Category], N'')",
                stored: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidFromNorm",
                schema: "calc",
                table: "MethodologyConstant",
                type: "date",
                nullable: false,
                computedColumnSql: "ISNULL([ValidFrom], CONVERT(date, '19000101', 112))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RoleAssignment_Sid",
                schema: "sec",
                table: "RoleAssignment",
                columns: new[] { "PrincipalSid", "RoleId" },
                unique: true,
                filter: "[PrincipalSid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_RoleAssignment_User",
                schema: "sec",
                table: "RoleAssignment",
                columns: new[] { "UserId", "RoleId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UQ_MethodologyConstant",
                schema: "calc",
                table: "MethodologyConstant",
                columns: new[] { "MethodologyVersionId", "Code", "CategoryNorm", "ValidFromNorm" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ApprState_Doc",
                schema: "wf",
                table: "ApprovalState",
                column: "DocumentId",
                principalSchema: "doc",
                principalTable: "Document",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApprState_Sheet",
                schema: "wf",
                table: "ApprovalState",
                column: "SheetDefId",
                principalSchema: "cfg",
                principalTable: "SheetDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CalcBind_Column",
                schema: "cfg",
                table: "CalculationBinding",
                column: "ColumnDefId",
                principalSchema: "cfg",
                principalTable: "ColumnDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CalcBind_Table",
                schema: "cfg",
                table: "CalculationBinding",
                column: "TableDefId",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CellValue_Unit",
                schema: "doc",
                table: "CellValue",
                column: "ValueUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ColumnDef_Cascade",
                schema: "cfg",
                table: "ColumnDef",
                column: "CascadeFromColumnId",
                principalSchema: "cfg",
                principalTable: "ColumnDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conv_From",
                schema: "uom",
                table: "Conversion",
                column: "FromUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conv_To",
                schema: "uom",
                table: "Conversion",
                column: "ToUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dim_BaseUnit",
                schema: "uom",
                table: "Dimension",
                column: "BaseUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dim_Den",
                schema: "uom",
                table: "Dimension",
                column: "DenominatorDimensionId",
                principalSchema: "uom",
                principalTable: "Dimension",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dim_Num",
                schema: "uom",
                table: "Dimension",
                column: "NumeratorDimensionId",
                principalSchema: "uom",
                principalTable: "Dimension",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocSheet_Doc",
                schema: "doc",
                table: "DocumentSheet",
                column: "DocumentId",
                principalSchema: "doc",
                principalTable: "Document",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocSheet_Sheet",
                schema: "doc",
                table: "DocumentSheet",
                column: "SheetDefId",
                principalSchema: "cfg",
                principalTable: "SheetDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PAR_Sheet",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                column: "SheetDefId",
                principalSchema: "cfg",
                principalTable: "SheetDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PAR_TV",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                column: "TemplateVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PAR_Table",
                schema: "cfg",
                table: "PeriodAccessRuleDef",
                column: "TableDefId",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Project_CurrentPeriod",
                schema: "doc",
                table: "Project",
                column: "CurrentPeriodId",
                principalSchema: "doc",
                principalTable: "Period",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Project_Policy",
                schema: "doc",
                table: "Project",
                column: "PeriodPolicyId",
                principalSchema: "doc",
                principalTable: "PeriodPolicy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Project_TV",
                schema: "doc",
                table: "Project",
                column: "TemplateVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RowDef_Parent",
                schema: "cfg",
                table: "RowDef",
                column: "ParentRowDefId",
                principalSchema: "cfg",
                principalTable: "RowDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SGR_TV",
                schema: "cfg",
                table: "SheetGroupRule",
                column: "TemplateVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StyleDef_TV",
                schema: "cfg",
                table: "StyleDef",
                column: "TemplateVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TableInstance_Doc",
                schema: "doc",
                table: "TableInstance",
                column: "DocumentId",
                principalSchema: "doc",
                principalTable: "Document",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TableInstance_Table",
                schema: "doc",
                table: "TableInstance",
                column: "TableDefId",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Rel_Source",
                schema: "cfg",
                table: "TableRelationDef",
                column: "SourceTableDefId",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Rel_Target",
                schema: "cfg",
                table: "TableRelationDef",
                column: "TargetTableDefId",
                principalSchema: "cfg",
                principalTable: "TableDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TableRow_RowDef",
                schema: "doc",
                table: "TableRow",
                column: "RowDefId",
                principalSchema: "cfg",
                principalTable: "RowDef",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TV_ClonedFrom",
                schema: "cfg",
                table: "TemplateVersion",
                column: "ClonedFromVersionId",
                principalSchema: "cfg",
                principalTable: "TemplateVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Unit_Den",
                schema: "uom",
                table: "Unit",
                column: "DenominatorUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Unit_Dim",
                schema: "uom",
                table: "Unit",
                column: "DimensionId",
                principalSchema: "uom",
                principalTable: "Dimension",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Unit_Num",
                schema: "uom",
                table: "Unit",
                column: "NumeratorUnitId",
                principalSchema: "uom",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_User_Policy",
                schema: "sec",
                table: "User",
                column: "PasswordPolicyId",
                principalSchema: "sec",
                principalTable: "PasswordPolicy",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApprState_Doc",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.DropForeignKey(
                name: "FK_ApprState_Sheet",
                schema: "wf",
                table: "ApprovalState");

            migrationBuilder.DropForeignKey(
                name: "FK_CalcBind_Column",
                schema: "cfg",
                table: "CalculationBinding");

            migrationBuilder.DropForeignKey(
                name: "FK_CalcBind_Table",
                schema: "cfg",
                table: "CalculationBinding");

            migrationBuilder.DropForeignKey(
                name: "FK_CellValue_Unit",
                schema: "doc",
                table: "CellValue");

            migrationBuilder.DropForeignKey(
                name: "FK_ColumnDef_Cascade",
                schema: "cfg",
                table: "ColumnDef");

            migrationBuilder.DropForeignKey(
                name: "FK_Conv_From",
                schema: "uom",
                table: "Conversion");

            migrationBuilder.DropForeignKey(
                name: "FK_Conv_To",
                schema: "uom",
                table: "Conversion");

            migrationBuilder.DropForeignKey(
                name: "FK_Dim_BaseUnit",
                schema: "uom",
                table: "Dimension");

            migrationBuilder.DropForeignKey(
                name: "FK_Dim_Den",
                schema: "uom",
                table: "Dimension");

            migrationBuilder.DropForeignKey(
                name: "FK_Dim_Num",
                schema: "uom",
                table: "Dimension");

            migrationBuilder.DropForeignKey(
                name: "FK_DocSheet_Doc",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropForeignKey(
                name: "FK_DocSheet_Sheet",
                schema: "doc",
                table: "DocumentSheet");

            migrationBuilder.DropForeignKey(
                name: "FK_PAR_Sheet",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropForeignKey(
                name: "FK_PAR_TV",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropForeignKey(
                name: "FK_PAR_Table",
                schema: "cfg",
                table: "PeriodAccessRuleDef");

            migrationBuilder.DropForeignKey(
                name: "FK_Project_CurrentPeriod",
                schema: "doc",
                table: "Project");

            migrationBuilder.DropForeignKey(
                name: "FK_Project_Policy",
                schema: "doc",
                table: "Project");

            migrationBuilder.DropForeignKey(
                name: "FK_Project_TV",
                schema: "doc",
                table: "Project");

            migrationBuilder.DropForeignKey(
                name: "FK_RowDef_Parent",
                schema: "cfg",
                table: "RowDef");

            migrationBuilder.DropForeignKey(
                name: "FK_SGR_TV",
                schema: "cfg",
                table: "SheetGroupRule");

            migrationBuilder.DropForeignKey(
                name: "FK_StyleDef_TV",
                schema: "cfg",
                table: "StyleDef");

            migrationBuilder.DropForeignKey(
                name: "FK_TableInstance_Doc",
                schema: "doc",
                table: "TableInstance");

            migrationBuilder.DropForeignKey(
                name: "FK_TableInstance_Table",
                schema: "doc",
                table: "TableInstance");

            migrationBuilder.DropForeignKey(
                name: "FK_Rel_Source",
                schema: "cfg",
                table: "TableRelationDef");

            migrationBuilder.DropForeignKey(
                name: "FK_Rel_Target",
                schema: "cfg",
                table: "TableRelationDef");

            migrationBuilder.DropForeignKey(
                name: "FK_TableRow_RowDef",
                schema: "doc",
                table: "TableRow");

            migrationBuilder.DropForeignKey(
                name: "FK_TV_ClonedFrom",
                schema: "cfg",
                table: "TemplateVersion");

            migrationBuilder.DropForeignKey(
                name: "FK_Unit_Den",
                schema: "uom",
                table: "Unit");

            migrationBuilder.DropForeignKey(
                name: "FK_Unit_Dim",
                schema: "uom",
                table: "Unit");

            migrationBuilder.DropForeignKey(
                name: "FK_Unit_Num",
                schema: "uom",
                table: "Unit");

            migrationBuilder.DropForeignKey(
                name: "FK_User_Policy",
                schema: "sec",
                table: "User");

            migrationBuilder.DropIndex(
                name: "UQ_RoleAssignment_Sid",
                schema: "sec",
                table: "RoleAssignment");

            migrationBuilder.DropIndex(
                name: "UQ_RoleAssignment_User",
                schema: "sec",
                table: "RoleAssignment");

            migrationBuilder.DropIndex(
                name: "UQ_MethodologyConstant",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.DropColumn(
                name: "CategoryNorm",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.DropColumn(
                name: "ValidFromNorm",
                schema: "calc",
                table: "MethodologyConstant");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_Sid",
                schema: "sec",
                table: "RoleAssignment",
                column: "PrincipalSid");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_User",
                schema: "sec",
                table: "RoleAssignment",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DocSheet_Document",
                schema: "doc",
                table: "DocumentSheet",
                column: "DocumentId",
                principalSchema: "doc",
                principalTable: "Document",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Unit_Dimension",
                schema: "uom",
                table: "Unit",
                column: "DimensionId",
                principalSchema: "uom",
                principalTable: "Dimension",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
