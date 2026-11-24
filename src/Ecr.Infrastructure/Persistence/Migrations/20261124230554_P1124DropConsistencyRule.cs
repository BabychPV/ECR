using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ✎ 24.11 (аудит «Consistency issues», lane integ-consistency-checks): прибирає мертву таблицю <c>ext.ConsistencyRule</c>.
    /// </summary>
    /// <remarks>
    /// Правила нічної перевірки узгодженості — КОД (<c>ConsistencyCheckJob.KnownRules</c>), а не дані: у таблиці не було
    /// жодного читача, записувача, ендпоінта, сіду чи екрана — лише <c>DbSet</c>; на live 0 рядків (24.11, лише читанням).
    /// Порядок токена міграцій: calc-issue-orphans → <c>P1124RemoveReportSnapshots</c> → ця. <c>Down</c> відтворює порожню
    /// таблицю (даних у ній не було).
    /// </remarks>
    public partial class P1124DropConsistencyRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsistencyRule",
                schema: "ext");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsistencyRule",
                schema: "ext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceEntityId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsistencyRule", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_ConsistencyRule",
                schema: "ext",
                table: "ConsistencyRule",
                column: "Code",
                unique: true);
        }
    }
}
