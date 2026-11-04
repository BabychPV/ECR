using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TC02_SheetCopyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TableDiscussionRead",
                schema: "doc",
                table: "TableDiscussionRead");

            migrationBuilder.DropIndex(
                name: "IX_TableDiscussionMessage_Chat",
                schema: "doc",
                table: "TableDiscussionMessage");

            migrationBuilder.DropIndex(
                name: "UX_TableDiscussionMessage_ClientId",
                schema: "doc",
                table: "TableDiscussionMessage");

            migrationBuilder.DropIndex(
                name: "IX_TableDiscussionMention_User",
                schema: "doc",
                table: "TableDiscussionMention");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TableDiscussion",
                schema: "doc",
                table: "TableDiscussion");

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionRead",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionMessage",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionMention",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussion",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TableDiscussionRead",
                schema: "doc",
                table: "TableDiscussionRead",
                columns: new[] { "UserId", "DocumentId", "TableDefCode", "SheetCopyKey" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_TableDiscussion",
                schema: "doc",
                table: "TableDiscussion",
                columns: new[] { "DocumentId", "TableDefCode", "SheetCopyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_TableDiscussionMessage_Chat",
                schema: "doc",
                table: "TableDiscussionMessage",
                columns: new[] { "DocumentId", "TableDefCode", "SheetCopyKey", "Id" },
                descending: new[] { false, false, false, true })
                .Annotation("SqlServer:Include", new[] { "PeriodKey", "AuthorUserId", "CreatedAt", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_TableDiscussionMessage_ClientId",
                schema: "doc",
                table: "TableDiscussionMessage",
                columns: new[] { "DocumentId", "TableDefCode", "SheetCopyKey", "AuthorUserId", "ClientMessageId" },
                unique: true,
                filter: "[ClientMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TableDiscussionMention_User",
                schema: "doc",
                table: "TableDiscussionMention",
                columns: new[] { "UserId", "DocumentId", "TableDefCode", "SheetCopyKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TableDiscussionRead",
                schema: "doc",
                table: "TableDiscussionRead");

            migrationBuilder.DropIndex(
                name: "IX_TableDiscussionMessage_Chat",
                schema: "doc",
                table: "TableDiscussionMessage");

            migrationBuilder.DropIndex(
                name: "UX_TableDiscussionMessage_ClientId",
                schema: "doc",
                table: "TableDiscussionMessage");

            migrationBuilder.DropIndex(
                name: "IX_TableDiscussionMention_User",
                schema: "doc",
                table: "TableDiscussionMention");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TableDiscussion",
                schema: "doc",
                table: "TableDiscussion");

            migrationBuilder.DropColumn(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionRead");

            migrationBuilder.DropColumn(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionMessage");

            migrationBuilder.DropColumn(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussionMention");

            migrationBuilder.DropColumn(
                name: "SheetCopyKey",
                schema: "doc",
                table: "TableDiscussion");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TableDiscussionRead",
                schema: "doc",
                table: "TableDiscussionRead",
                columns: new[] { "UserId", "DocumentId", "TableDefCode" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_TableDiscussion",
                schema: "doc",
                table: "TableDiscussion",
                columns: new[] { "DocumentId", "TableDefCode" });

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

            migrationBuilder.CreateIndex(
                name: "IX_TableDiscussionMention_User",
                schema: "doc",
                table: "TableDiscussionMention",
                columns: new[] { "UserId", "DocumentId", "TableDefCode" });
        }
    }
}
