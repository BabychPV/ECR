using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EA1EditAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EditorSet",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OwnerUserId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorSet", x => x.Id);
                    table.CheckConstraint("CK_EditorSet_Name", "LEN(LTRIM(RTRIM(Name))) > 0");
                    table.ForeignKey(
                        name: "FK_EditorSet_Owner",
                        column: x => x.OwnerUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EditAssignment",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SheetCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TableCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MonthNumber = table.Column<byte>(type: "tinyint", nullable: true),
                    PeriodKey = table.Column<int>(type: "int", nullable: true),
                    SliceKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsExclusive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_EditAssignment_Excl"),
                    ValidationStatus = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0)
                        .Annotation("Relational:DefaultConstraintName", "DF_EditAssignment_Valid"),
                    ValidatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ValidatedByUserId = table.Column<int>(type: "int", nullable: true),
                    DelegatedByUserId = table.Column<int>(type: "int", nullable: false),
                    HandoffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AppliedSetId = table.Column<int>(type: "int", nullable: true),
                    AppliedSetName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedByUserId = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditAssignment", x => x.Id);
                    table.CheckConstraint("CK_EditAssignment_Month", "MonthNumber IS NULL OR MonthNumber BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_EditAssignment_Note", "Note IS NULL OR LEN(Note) > 0");
                    table.CheckConstraint("CK_EditAssignment_Period", "PeriodKey IS NULL OR (PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99)");
                    table.CheckConstraint("CK_EditAssignment_SliceKey", "LEN(SliceKey) > 0");
                    table.CheckConstraint("CK_EditAssignment_Table", "TableCode IS NULL OR SheetCode IS NOT NULL");
                    table.CheckConstraint("CK_EditAssignment_Validation", "(ValidationStatus = 0 AND ValidatedAt IS NULL AND ValidatedByUserId IS NULL) OR (ValidationStatus = 1 AND ValidatedAt IS NOT NULL AND ValidatedByUserId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_EditAssignment_DelegatedBy",
                        column: x => x.DelegatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignment_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignment_Set",
                        column: x => x.AppliedSetId,
                        principalSchema: "doc",
                        principalTable: "EditorSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignment_ValidatedBy",
                        column: x => x.ValidatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EditorSetMember",
                schema: "doc",
                columns: table => new
                {
                    SetId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorSetMember", x => new { x.SetId, x.UserId });
                    table.ForeignKey(
                        name: "FK_EditorSetMember_Set",
                        column: x => x.SetId,
                        principalSchema: "doc",
                        principalTable: "EditorSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditorSetMember_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EditAssignmentRecipient",
                schema: "doc",
                columns: table => new
                {
                    AssignmentId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedByUserId = table.Column<int>(type: "int", nullable: false),
                    FilledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReturnedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReturnedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReturnReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReturnCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                        .Annotation("Relational:DefaultConstraintName", "DF_EditAssignmentRecipient_RetCnt"),
                    StatusChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditAssignmentRecipient", x => new { x.AssignmentId, x.UserId });
                    table.CheckConstraint("CK_EditAssignmentRecipient_Reason", "ReturnReason IS NULL OR LEN(LTRIM(RTRIM(ReturnReason))) > 0");
                    table.CheckConstraint("CK_EditAssignmentRecipient_State", "(Status = 0 AND FilledAt IS NULL AND ReturnedAt IS NULL AND ReturnedByUserId IS NULL AND ReturnReason IS NULL AND ReturnCount = 0) OR (Status = 1 AND FilledAt IS NOT NULL) OR (Status = 2 AND ReturnedAt IS NOT NULL AND ReturnedByUserId IS NOT NULL AND ReturnReason IS NOT NULL AND ReturnCount >= 1)");
                    table.ForeignKey(
                        name: "FK_EditAssignmentRecipient_AssignedBy",
                        column: x => x.AssignedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignmentRecipient_Assignment",
                        column: x => x.AssignmentId,
                        principalSchema: "doc",
                        principalTable: "EditAssignment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignmentRecipient_ReturnedBy",
                        column: x => x.ReturnedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EditAssignmentRecipient_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EditAssignment_AppliedSet",
                schema: "doc",
                table: "EditAssignment",
                column: "AppliedSetId",
                filter: "[AppliedSetId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EditAssignment_Handoff",
                schema: "doc",
                table: "EditAssignment",
                column: "HandoffId");

            migrationBuilder.CreateIndex(
                name: "UQ_EditAssignment",
                schema: "doc",
                table: "EditAssignment",
                columns: new[] { "DocumentId", "SliceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EditAssignmentRecipient_User",
                schema: "doc",
                table: "EditAssignmentRecipient",
                columns: new[] { "UserId", "Status" })
                .Annotation("SqlServer:Include", new[] { "AssignmentId" });

            migrationBuilder.CreateIndex(
                name: "UQ_EditorSet_Owner",
                schema: "doc",
                table: "EditorSet",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EditAssignmentRecipient",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "EditorSetMember",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "EditAssignment",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "EditorSet",
                schema: "doc");
        }
    }
}
