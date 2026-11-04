using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TC01_TableDiscussion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TableDiscussion",
                schema: "doc",
                columns: table => new
                {
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    MessageCount = table.Column<int>(type: "int", nullable: false),
                    LastMessageId = table.Column<long>(type: "bigint", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableDiscussion", x => new { x.DocumentId, x.TableDefCode });
                    table.ForeignKey(
                        name: "FK_TableDiscussion_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableDiscussionMessage",
                schema: "doc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TableDefCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PeriodKey = table.Column<int>(type: "int", nullable: false),
                    AuthorUserId = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableDiscussionMessage", x => x.Id);
                    table.CheckConstraint("CK_TableDiscussionMessage_Body", "LEN(Body) BETWEEN 1 AND 4000");
                    table.CheckConstraint("CK_TableDiscussionMessage_Deleted", "(DeletedAt IS NULL AND DeletedByUserId IS NULL) OR (DeletedAt IS NOT NULL AND DeletedByUserId IS NOT NULL)");
                    table.CheckConstraint("CK_TableDiscussionMessage_Period", "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99");
                    table.ForeignKey(
                        name: "FK_TableDiscussionMessage_Author",
                        column: x => x.AuthorUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableDiscussionMessage_DeletedBy",
                        column: x => x.DeletedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableDiscussionMessage_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableDiscussionRead",
                schema: "doc",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastReadMessageId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableDiscussionRead", x => new { x.UserId, x.DocumentId, x.TableDefCode });
                    table.ForeignKey(
                        name: "FK_TableDiscussionRead_Document",
                        column: x => x.DocumentId,
                        principalSchema: "doc",
                        principalTable: "Document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableDiscussionRead_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TableDiscussionMention",
                schema: "doc",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    TableDefCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TableDiscussionMention", x => new { x.MessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TableDiscussionMention_Message",
                        column: x => x.MessageId,
                        principalSchema: "doc",
                        principalTable: "TableDiscussionMessage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TableDiscussionMention_User",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TableDiscussionMention_User",
                schema: "doc",
                table: "TableDiscussionMention",
                columns: new[] { "UserId", "DocumentId", "TableDefCode" });

            migrationBuilder.CreateIndex(
                name: "IX_TableDiscussionMessage_Chat",
                schema: "doc",
                table: "TableDiscussionMessage",
                columns: new[] { "DocumentId", "TableDefCode", "Id" },
                descending: new[] { false, false, true })
                .Annotation("SqlServer:Include", new[] { "PeriodKey", "AuthorUserId", "CreatedAt", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_TableDiscussionMessage_ClientId",
                schema: "doc",
                table: "TableDiscussionMessage",
                columns: new[] { "DocumentId", "TableDefCode", "AuthorUserId", "ClientMessageId" },
                unique: true,
                filter: "[ClientMessageId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TableDiscussion",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "TableDiscussionMention",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "TableDiscussionRead",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "TableDiscussionMessage",
                schema: "doc");
        }
    }
}
