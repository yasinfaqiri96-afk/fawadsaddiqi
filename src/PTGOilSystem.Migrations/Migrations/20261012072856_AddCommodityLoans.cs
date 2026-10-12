using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCommodityLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommodityLoans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LoanNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    CounterpartyType = table.Column<int>(type: "integer", nullable: false),
                    CounterpartyId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    QualitySpec = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LoanDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReturnObligation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BorrowBasis = table.Column<int>(type: "integer", nullable: true),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: false),
                    ValuationPolicy = table.Column<int>(type: "integer", nullable: true),
                    AgreedUnitValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    ValuationReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CarryingValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    LoanAccountId = table.Column<int>(type: "integer", nullable: true),
                    DifferenceAccountId = table.Column<int>(type: "integer", nullable: true),
                    QuantityTolerancePercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    CashSettlementAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    CashSettlementUnitPriceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    CashSettlementReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AgreementReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
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
                    table.PrimaryKey("PK_CommodityLoans", x => x.Id);
                    table.CheckConstraint("CK_CommodityLoans_Due", "\"DueDate\" >= \"LoanDate\"");
                    table.CheckConstraint("CK_CommodityLoans_Quantity", "\"QuantityMt\" > 0");
                    table.CheckConstraint("CK_CommodityLoans_Terms", "(\"Direction\" <> 1 OR \"ValuationPolicy\" IS NULL OR \"ValuationPolicy\" = 1) AND (\"Direction\" <> 2 OR \"BorrowBasis\" IS NOT NULL) AND (\"Direction\" <> 2 OR \"ValuationPolicy\" IS NULL OR (\"ValuationPolicy\" = 2 AND \"AgreedUnitValueUsd\" > 0 AND \"ValuationReference\" IS NOT NULL)) AND (NOT \"CashSettlementAllowed\" OR (\"CashSettlementUnitPriceUsd\" > 0 AND \"CashSettlementReference\" IS NOT NULL)) AND (\"QuantityTolerancePercent\" IS NULL OR (\"QuantityTolerancePercent\" >= 0 AND \"QuantityTolerancePercent\" <= 10))");
                    table.ForeignKey(
                        name: "FK_CommodityLoans_Accounts_DifferenceAccountId",
                        column: x => x.DifferenceAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoans_Accounts_LoanAccountId",
                        column: x => x.LoanAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoans_InventoryMovements_InventoryMovementId",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoans_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoans_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoans_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommodityLoanSettlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommodityLoanId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SettlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: true),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    InventoryMovementId = table.Column<int>(type: "integer", nullable: true),
                    CarryingValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    InventoryValueUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    CashAmountUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    CashAccountId = table.Column<int>(type: "integer", nullable: true),
                    PaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    ReversalPaymentTransactionId = table.Column<int>(type: "integer", nullable: true),
                    DifferenceUsd = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
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
                    table.PrimaryKey("PK_CommodityLoanSettlements", x => x.Id);
                    table.CheckConstraint("CK_CommodityLoanSettlements_Kind", "(\"Kind\" <> 1 OR (\"TerminalId\" IS NOT NULL AND \"InventoryMovementId\" IS NOT NULL)) AND (\"Kind\" <> 2 OR (\"CashAmountUsd\" > 0 AND \"CashAccountId\" IS NOT NULL))");
                    table.CheckConstraint("CK_CommodityLoanSettlements_Quantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_CommodityLoans_CommodityLoanId",
                        column: x => x.CommodityLoanId,
                        principalTable: "CommodityLoans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_InventoryMovements_InventoryMoveme~",
                        column: x => x.InventoryMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_PaymentTransactions_PaymentTransac~",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_PaymentTransactions_ReversalPaymen~",
                        column: x => x.ReversalPaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommodityLoanSettlements_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_CounterpartyType_CounterpartyId",
                table: "CommodityLoans",
                columns: new[] { "CounterpartyType", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_DifferenceAccountId",
                table: "CommodityLoans",
                column: "DifferenceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_InventoryMovementId",
                table: "CommodityLoans",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_LoanAccountId",
                table: "CommodityLoans",
                column: "LoanAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_LoanNumber",
                table: "CommodityLoans",
                column: "LoanNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_ProductId",
                table: "CommodityLoans",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_StorageTankId",
                table: "CommodityLoans",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoans_TerminalId",
                table: "CommodityLoans",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_CashAccountId",
                table: "CommodityLoanSettlements",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_CommodityLoanId",
                table: "CommodityLoanSettlements",
                column: "CommodityLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_InventoryMovementId",
                table: "CommodityLoanSettlements",
                column: "InventoryMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_PaymentTransactionId",
                table: "CommodityLoanSettlements",
                column: "PaymentTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_ReversalPaymentTransactionId",
                table: "CommodityLoanSettlements",
                column: "ReversalPaymentTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_StorageTankId",
                table: "CommodityLoanSettlements",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityLoanSettlements_TerminalId",
                table: "CommodityLoanSettlements",
                column: "TerminalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommodityLoanSettlements");

            migrationBuilder.DropTable(
                name: "CommodityLoans");
        }
    }
}
