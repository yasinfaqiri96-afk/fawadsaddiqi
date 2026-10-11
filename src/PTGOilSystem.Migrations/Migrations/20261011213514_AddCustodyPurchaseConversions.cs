using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyPurchaseConversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustodyConversions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustodyLotId = table.Column<int>(type: "integer", nullable: false),
                    ContractId = table.Column<int>(type: "integer", nullable: false),
                    StockOperationKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AgreementReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyConversions", x => x.Id);
                    table.CheckConstraint("CK_CustodyConversion_Quantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodyConversions_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyConversions_CustodyLots_CustodyLotId",
                        column: x => x.CustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodySupplierIdentityLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodySupplierIdentityLinks", x => x.Id);
                    table.CheckConstraint("CK_CustodyIdentity_OneRole", "(\"CustomerId\" IS NOT NULL AND \"PartnerId\" IS NULL) OR (\"PartnerId\" IS NOT NULL AND \"CustomerId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CustodySupplierIdentityLinks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodySupplierIdentityLinks_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodySupplierIdentityLinks_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyConversions_ContractId",
                table: "CustodyConversions",
                column: "ContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyConversions_CustodyLotId",
                table: "CustodyConversions",
                column: "CustodyLotId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyConversions_StockOperationKey",
                table: "CustodyConversions",
                column: "StockOperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySupplierIdentityLinks_CustomerId",
                table: "CustodySupplierIdentityLinks",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySupplierIdentityLinks_PartnerId",
                table: "CustodySupplierIdentityLinks",
                column: "PartnerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySupplierIdentityLinks_SupplierId",
                table: "CustodySupplierIdentityLinks",
                column: "SupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustodyConversions");

            migrationBuilder.DropTable(
                name: "CustodySupplierIdentityLinks");
        }
    }
}
