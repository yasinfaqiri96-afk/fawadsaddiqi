using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustodianPartyId",
                table: "Terminals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustodianPartyType",
                table: "Terminals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustodyLots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LotNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OwnerPartyType = table.Column<int>(type: "integer", nullable: false),
                    OwnerPartyId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ParentCustodyLotId = table.Column<int>(type: "integer", nullable: true),
                    LoadedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LoadedQuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    OriginDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TransportReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TransitLossResponsibility = table.Column<int>(type: "integer", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyLots", x => x.Id);
                    table.CheckConstraint("CK_CustodyLots_LoadedQuantity", "\"LoadedQuantityMt\" IS NULL OR \"LoadedQuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodyLots_CustodyLots_ParentCustodyLotId",
                        column: x => x.ParentCustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyLots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodyHolds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustodyLotId = table.Column<int>(type: "integer", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedByUserId = table.Column<int>(type: "integer", nullable: true),
                    ReleaseNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyHolds", x => x.Id);
                    table.CheckConstraint("CK_CustodyHolds_PositiveQuantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodyHolds_CustodyLots_CustodyLotId",
                        column: x => x.CustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodyMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustodyLotId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    StorageTankId = table.Column<int>(type: "integer", nullable: true),
                    MovementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QuantityMt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OperationKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversalOfCustodyMovementId = table.Column<int>(type: "integer", nullable: true),
                    IsReversed = table.Column<bool>(type: "boolean", nullable: false),
                    LossResponsibility = table.Column<int>(type: "integer", nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CounterpartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VehicleReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyMovements", x => x.Id);
                    table.CheckConstraint("CK_CustodyMovements_PositiveQuantity", "\"QuantityMt\" > 0");
                    table.ForeignKey(
                        name: "FK_CustodyMovements_CustodyLots_CustodyLotId",
                        column: x => x.CustodyLotId,
                        principalTable: "CustodyLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyMovements_CustodyMovements_ReversalOfCustodyMovement~",
                        column: x => x.ReversalOfCustodyMovementId,
                        principalTable: "CustodyMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyMovements_StorageTanks_StorageTankId",
                        column: x => x.StorageTankId,
                        principalTable: "StorageTanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodyMovements_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_CustodianPartyType_CustodianPartyId",
                table: "Terminals",
                columns: new[] { "CustodianPartyType", "CustodianPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyHolds_CustodyLotId",
                table: "CustodyHolds",
                column: "CustodyLotId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyLots_LotNumber",
                table: "CustodyLots",
                column: "LotNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyLots_OwnerPartyType_OwnerPartyId",
                table: "CustodyLots",
                columns: new[] { "OwnerPartyType", "OwnerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyLots_ParentCustodyLotId",
                table: "CustodyLots",
                column: "ParentCustodyLotId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyLots_ProductId",
                table: "CustodyLots",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovements_CustodyLotId_TerminalId_StorageTankId",
                table: "CustodyMovements",
                columns: new[] { "CustodyLotId", "TerminalId", "StorageTankId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovements_OperationKey",
                table: "CustodyMovements",
                column: "OperationKey");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovements_ReversalOfCustodyMovementId",
                table: "CustodyMovements",
                column: "ReversalOfCustodyMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovements_StorageTankId",
                table: "CustodyMovements",
                column: "StorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovements_TerminalId_StorageTankId",
                table: "CustodyMovements",
                columns: new[] { "TerminalId", "StorageTankId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustodyHolds");

            migrationBuilder.DropTable(
                name: "CustodyMovements");

            migrationBuilder.DropTable(
                name: "CustodyLots");

            migrationBuilder.DropIndex(
                name: "IX_Terminals_CustodianPartyType_CustodianPartyId",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "CustodianPartyId",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "CustodianPartyType",
                table: "Terminals");
        }
    }
}
