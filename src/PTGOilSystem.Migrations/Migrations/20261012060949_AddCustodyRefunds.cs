using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustodySaleReturnId",
                table: "CustodyFinanceEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_CustodySaleReturnId",
                table: "CustodyFinanceEntries",
                column: "CustodySaleReturnId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustodyFinanceEntries_CustodySaleReturns_CustodySaleReturnId",
                table: "CustodyFinanceEntries",
                column: "CustodySaleReturnId",
                principalTable: "CustodySaleReturns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustodyFinanceEntries_CustodySaleReturns_CustodySaleReturnId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropIndex(
                name: "IX_CustodyFinanceEntries_CustodySaleReturnId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "CustodySaleReturnId",
                table: "CustodyFinanceEntries");
        }
    }
}
