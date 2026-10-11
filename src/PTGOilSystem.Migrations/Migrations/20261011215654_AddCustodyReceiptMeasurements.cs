using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyReceiptMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DensityKgPerM3",
                table: "CustodyMovements",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DensityTemperatureC",
                table: "CustodyMovements",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeasurementReference",
                table: "CustodyMovements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeasurementTemperatureC",
                table: "CustodyMovements",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceLiters",
                table: "CustodyMovements",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyMovements_Measurement",
                table: "CustodyMovements",
                sql: "(\"SourceLiters\" IS NULL AND \"DensityKgPerM3\" IS NULL AND \"MeasurementTemperatureC\" IS NULL AND \"DensityTemperatureC\" IS NULL AND \"MeasurementReference\" IS NULL) OR (\"SourceLiters\" IS NOT NULL AND \"SourceLiters\" > 0 AND \"DensityKgPerM3\" IS NOT NULL AND \"DensityKgPerM3\" > 0 AND \"MeasurementTemperatureC\" IS NOT NULL AND \"DensityTemperatureC\" IS NOT NULL AND \"MeasurementTemperatureC\" = \"DensityTemperatureC\" AND \"MeasurementReference\" IS NOT NULL AND length(trim(\"MeasurementReference\")) > 0 AND round(\"SourceLiters\" * \"DensityKgPerM3\" / 1000000, 4) = \"QuantityMt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyMovements_Measurement",
                table: "CustodyMovements");

            migrationBuilder.DropColumn(
                name: "DensityKgPerM3",
                table: "CustodyMovements");

            migrationBuilder.DropColumn(
                name: "DensityTemperatureC",
                table: "CustodyMovements");

            migrationBuilder.DropColumn(
                name: "MeasurementReference",
                table: "CustodyMovements");

            migrationBuilder.DropColumn(
                name: "MeasurementTemperatureC",
                table: "CustodyMovements");

            migrationBuilder.DropColumn(
                name: "SourceLiters",
                table: "CustodyMovements");
        }
    }
}
