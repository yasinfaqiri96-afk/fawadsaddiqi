using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodySalesAndFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustodyCommissionRevenueAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustodyFundsPayableAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustodyPendingCollectionAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustodyServiceRevenueAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustodySales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustodyLotId = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyType = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    SaleDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnitPriceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CommissionMethod = table.Column<int>(type: "integer", nullable: false),
                    CommissionRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    CommissionUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CommissionRecognition = table.Column<int>(type: "integer", nullable: false),
                    CommissionRecognizedAtSaleUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
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
                    table.PrimaryKey("PK_CustodySales", x => x.Id);
                    table.CheckConstraint("CK_CustodySales_Amounts", "\"AmountUsd\" > 0 AND \"CommissionUsd\" >= 0 AND \"CommissionUsd\" <= \"AmountUsd\" AND \"CommissionRecognizedAtSaleUsd\" >= 0 AND \"CommissionRecognizedAtSaleUsd\" <= \"CommissionUsd\"");
                    table.CheckConstraint("CK_CustodySales_Quantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodySales_CustodyLots_CustodyLotId",
                        column: x => x.CustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodySales_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodyFinanceEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CustodySaleId = table.Column<int>(type: "integer", nullable: true),
                    CustodyLotId = table.Column<int>(type: "integer", nullable: true),
                    OwnerPartyType = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    EntryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CommissionUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CashAccountId = table.Column<int>(type: "integer", nullable: true),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    ReversalPaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    ExpenseTypeId = table.Column<int>(type: "integer", nullable: true),
                    PayeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                    table.PrimaryKey("PK_CustodyFinanceEntries", x => x.Id);
                    table.CheckConstraint("CK_CustodyFinanceEntries_Amounts", "\"AmountUsd\" > 0 AND \"CommissionUsd\" >= 0 AND \"CommissionUsd\" <= \"AmountUsd\"");
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_CustodyLots_CustodyLotId",
                        column: x => x.CustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_CustodySales_CustodySaleId",
                        column: x => x.CustodySaleId,
                        principalTable: "CustodySales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_ExpenseTypes_ExpenseTypeId",
                        column: x => x.ExpenseTypeId,
                        principalTable: "ExpenseTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_PaymentTransactions_PaymentTransactio~",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyFinanceEntries_PaymentTransactions_ReversalPaymentTr~",
                        column: x => x.ReversalPaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CustodyCommissionRevenueAccountId",
                table: "AccountingSettings",
                column: "CustodyCommissionRevenueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CustodyFundsPayableAccountId",
                table: "AccountingSettings",
                column: "CustodyFundsPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CustodyPendingCollectionAccountId",
                table: "AccountingSettings",
                column: "CustodyPendingCollectionAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_CustodyServiceRevenueAccountId",
                table: "AccountingSettings",
                column: "CustodyServiceRevenueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_CashAccountId",
                table: "CustodyFinanceEntries",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_CustodyLotId",
                table: "CustodyFinanceEntries",
                column: "CustodyLotId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_CustodySaleId",
                table: "CustodyFinanceEntries",
                column: "CustodySaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_ExpenseTypeId",
                table: "CustodyFinanceEntries",
                column: "ExpenseTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_OwnerPartyType_OwnerPartyId",
                table: "CustodyFinanceEntries",
                columns: new[] { "OwnerPartyType", "OwnerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_PaymentTransactionId",
                table: "CustodyFinanceEntries",
                column: "PaymentTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyFinanceEntries_ReversalPaymentTransactionId",
                table: "CustodyFinanceEntries",
                column: "ReversalPaymentTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySales_CustodyLotId",
                table: "CustodySales",
                column: "CustodyLotId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodySales_CustomerId",
                table: "CustodySales",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodySales_OwnerPartyType_OwnerPartyId",
                table: "CustodySales",
                columns: new[] { "OwnerPartyType", "OwnerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodySales_SaleNumber",
                table: "CustodySales",
                column: "SaleNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySales_StockOperationKey",
                table: "CustodySales",
                column: "StockOperationKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyCommissionRevenueAccount~",
                table: "AccountingSettings",
                column: "CustodyCommissionRevenueAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyFundsPayableAccountId",
                table: "AccountingSettings",
                column: "CustodyFundsPayableAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyPendingCollectionAccount~",
                table: "AccountingSettings",
                column: "CustodyPendingCollectionAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyServiceRevenueAccountId",
                table: "AccountingSettings",
                column: "CustodyServiceRevenueAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyCommissionRevenueAccount~",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyFundsPayableAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyPendingCollectionAccount~",
                table: "AccountingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_CustodyServiceRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropTable(
                name: "CustodyFinanceEntries");

            migrationBuilder.DropTable(
                name: "CustodySales");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_CustodyCommissionRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_CustodyFundsPayableAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_CustodyPendingCollectionAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_CustodyServiceRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "CustodyCommissionRevenueAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "CustodyFundsPayableAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "CustodyPendingCollectionAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "CustodyServiceRevenueAccountId",
                table: "AccountingSettings");
        }
    }
}
