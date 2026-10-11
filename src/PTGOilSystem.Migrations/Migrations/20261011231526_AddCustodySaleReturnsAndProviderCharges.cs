using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodySaleReturnsAndProviderCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "CustodyFinanceEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SettlesFinanceEntryId",
                table: "CustodyFinanceEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustodySaleReturns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReturnNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustodySaleId = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyType = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    ReturnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CommissionUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RecognizedCommissionReversedUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockOperationKey = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<int>(type: "integer", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodySaleReturns", x => x.Id);
                    table.CheckConstraint("CK_CustodySaleReturns_Amounts", "\"AmountUsd\" > 0 AND \"CommissionUsd\" >= 0 AND \"RecognizedCommissionReversedUsd\" >= 0 AND \"RecognizedCommissionReversedUsd\" <= \"CommissionUsd\"");
                    table.CheckConstraint("CK_CustodySaleReturns_Quantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodySaleReturns_CustodySales_CustodySaleId",
                        column: x => x.CustodySaleId,
                        principalTable: "CustodySales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodySaleReturns_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_ServiceProviderId",
                table: "CustodyFinanceEntries",
                column: "ServiceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_SettlesFinanceEntryId",
                table: "CustodyFinanceEntries",
                column: "SettlesFinanceEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodySaleReturns_CustodySaleId",
                table: "CustodySaleReturns",
                column: "CustodySaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodySaleReturns_CustomerId",
                table: "CustodySaleReturns",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodySaleReturns_OwnerPartyType_OwnerPartyId",
                table: "CustodySaleReturns",
                columns: new[] { "OwnerPartyType", "OwnerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodySaleReturns_ReturnNumber",
                table: "CustodySaleReturns",
                column: "ReturnNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySaleReturns_StockOperationKey",
                table: "CustodySaleReturns",
                column: "StockOperationKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CustodyFinanceEntries_CustodyFinanceEntries_SettlesFinanceE~",
                table: "CustodyFinanceEntries",
                column: "SettlesFinanceEntryId",
                principalTable: "CustodyFinanceEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustodyFinanceEntries_ServiceProviders_ServiceProviderId",
                table: "CustodyFinanceEntries",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustodyFinanceEntries_CustodyFinanceEntries_SettlesFinanceE~",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_CustodyFinanceEntries_ServiceProviders_ServiceProviderId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropTable(
                name: "CustodySaleReturns");

            migrationBuilder.DropIndex(
                name: "IX_CustodyFinanceEntries_ServiceProviderId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropIndex(
                name: "IX_CustodyFinanceEntries_SettlesFinanceEntryId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "SettlesFinanceEntryId",
                table: "CustodyFinanceEntries");
        }
    }
}
