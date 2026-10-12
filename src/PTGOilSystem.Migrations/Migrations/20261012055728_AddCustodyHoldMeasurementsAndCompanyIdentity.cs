using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyHoldMeasurementsAndCompanyIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyIdentity_OneRole",
                table: "CustodySupplierIdentityLinks");

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "CustodySupplierIdentityLinks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DensityKgPerM3",
                table: "CustodyHolds",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DensityTemperatureC",
                table: "CustodyHolds",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeasurementReference",
                table: "CustodyHolds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeasurementTemperatureC",
                table: "CustodyHolds",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceLiters",
                table: "CustodyHolds",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodySupplierIdentityLinks_CompanyId",
                table: "CustodySupplierIdentityLinks",
                column: "CompanyId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyIdentity_OneRole",
                table: "CustodySupplierIdentityLinks",
                sql: "(\"CustomerId\" IS NOT NULL AND \"PartnerId\" IS NULL AND \"CompanyId\" IS NULL) OR (\"PartnerId\" IS NOT NULL AND \"CustomerId\" IS NULL AND \"CompanyId\" IS NULL) OR (\"CompanyId\" IS NOT NULL AND \"CustomerId\" IS NULL AND \"PartnerId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyHolds_Measurement",
                table: "CustodyHolds",
                sql: "(\"SourceLiters\" IS NULL AND \"DensityKgPerM3\" IS NULL AND \"MeasurementTemperatureC\" IS NULL AND \"DensityTemperatureC\" IS NULL AND \"MeasurementReference\" IS NULL) OR (\"SourceLiters\" IS NOT NULL AND \"SourceLiters\" > 0 AND \"DensityKgPerM3\" IS NOT NULL AND \"DensityKgPerM3\" > 0 AND \"MeasurementTemperatureC\" IS NOT NULL AND \"DensityTemperatureC\" IS NOT NULL AND \"MeasurementTemperatureC\" = \"DensityTemperatureC\" AND \"MeasurementReference\" IS NOT NULL AND length(trim(\"MeasurementReference\")) > 0 AND round(\"SourceLiters\" * \"DensityKgPerM3\" / 1000000, 4) = \"QuantityMt\")");

            migrationBuilder.AddForeignKey(
                name: "FK_CustodySupplierIdentityLinks_Companies_CompanyId",
                table: "CustodySupplierIdentityLinks",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustodySupplierIdentityLinks_Companies_CompanyId",
                table: "CustodySupplierIdentityLinks");

            migrationBuilder.DropIndex(
                name: "IX_CustodySupplierIdentityLinks_CompanyId",
                table: "CustodySupplierIdentityLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyIdentity_OneRole",
                table: "CustodySupplierIdentityLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyHolds_Measurement",
                table: "CustodyHolds");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "CustodySupplierIdentityLinks");

            migrationBuilder.DropColumn(
                name: "DensityKgPerM3",
                table: "CustodyHolds");

            migrationBuilder.DropColumn(
                name: "DensityTemperatureC",
                table: "CustodyHolds");

            migrationBuilder.DropColumn(
                name: "MeasurementReference",
                table: "CustodyHolds");

            migrationBuilder.DropColumn(
                name: "MeasurementTemperatureC",
                table: "CustodyHolds");

            migrationBuilder.DropColumn(
                name: "SourceLiters",
                table: "CustodyHolds");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyIdentity_OneRole",
                table: "CustodySupplierIdentityLinks",
                sql: "(\"CustomerId\" IS NOT NULL AND \"PartnerId\" IS NULL) OR (\"PartnerId\" IS NOT NULL AND \"CustomerId\" IS NULL)");
        }
    }
}
