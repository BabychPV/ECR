using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Stage3Security : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRoute",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NameL10n = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_AR_Act"),
                    TemplateVersionId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRoute", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginAttempt",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<byte>(type: "tinyint", nullable: false),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    FailReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginAttempt", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignment",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    PrincipalSid = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ScopeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignment", x => x.Id);
                    table.CheckConstraint("CK_RoleAssign_Principal", "(UserId IS NOT NULL AND PrincipalSid IS NULL) OR (UserId IS NULL AND PrincipalSid IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_RoleAssign_Role",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleAssign_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermission",
                schema: "sec",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermission", x => new { x.RoleId, x.PermissionCode });
                    table.ForeignKey(
                        name: "FK_RolePerm_Perm",
                        column: x => x.PermissionCode,
                        principalSchema: "sec",
                        principalTable: "Permission",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePerm_Role",
                        column: x => x.RoleId,
                        principalSchema: "sec",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ValidationResult",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    RunAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    WarningCount = table.Column<int>(type: "int", nullable: false),
                    InfoCount = table.Column<int>(type: "int", nullable: false),
                    MessagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationResult", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VRes_Doc",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalStep",
                schema: "wf",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApprovalRouteId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_AS_Opt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalStep", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AS_Route",
                        column: x => x.ApprovalRouteId,
                        principalSchema: "wf",
                        principalTable: "ApprovalRoute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_ApprovalRoute",
                schema: "wf",
                table: "ApprovalRoute",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_ApprovalStep",
                schema: "wf",
                table: "ApprovalStep",
                columns: new[] { "ApprovalRouteId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginAttempt_User",
                schema: "sec",
                table: "LoginAttempt",
                columns: new[] { "UserName", "AttemptedAt" },
                descending: new[] { false, true });

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

            migrationBuilder.CreateIndex(
                name: "IX_ValidationResult_Doc",
                schema: "wf",
                table: "ValidationResult",
                columns: new[] { "DocumentId", "PeriodKey", "RunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalStep",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "LoginAttempt",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "RoleAssignment",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "RolePermission",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "ValidationResult",
                schema: "wf");

            migrationBuilder.DropTable(
                name: "ApprovalRoute",
                schema: "wf");
        }
    }
}
