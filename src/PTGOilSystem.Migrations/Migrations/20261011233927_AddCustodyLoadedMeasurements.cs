using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyLoadedMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LoadedDensityKgPerM3",
                table: "CustodyLots",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LoadedDensityTemperatureC",
                table: "CustodyLots",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoadedMeasurementReference",
                table: "CustodyLots",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LoadedMeasurementTemperatureC",
                table: "CustodyLots",
                type: "numeric(8,3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LoadedSourceLiters",
                table: "CustodyLots",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyLots_LoadedMeasurement",
                table: "CustodyLots",
                sql: "(\"LoadedSourceLiters\" IS NULL AND \"LoadedDensityKgPerM3\" IS NULL AND \"LoadedMeasurementTemperatureC\" IS NULL AND \"LoadedDensityTemperatureC\" IS NULL AND \"LoadedMeasurementReference\" IS NULL) OR (\"LoadedSourceLiters\" IS NOT NULL AND \"LoadedSourceLiters\" > 0 AND \"LoadedDensityKgPerM3\" IS NOT NULL AND \"LoadedDensityKgPerM3\" > 0 AND \"LoadedMeasurementTemperatureC\" IS NOT NULL AND \"LoadedDensityTemperatureC\" IS NOT NULL AND \"LoadedMeasurementTemperatureC\" = \"LoadedDensityTemperatureC\" AND \"LoadedMeasurementReference\" IS NOT NULL AND length(trim(\"LoadedMeasurementReference\")) > 0 AND round(\"LoadedSourceLiters\" * \"LoadedDensityKgPerM3\" / 1000000, 4) = \"LoadedQuantityMt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyLots_LoadedMeasurement",
                table: "CustodyLots");

            migrationBuilder.DropColumn(
                name: "LoadedDensityKgPerM3",
                table: "CustodyLots");

            migrationBuilder.DropColumn(
                name: "LoadedDensityTemperatureC",
                table: "CustodyLots");

            migrationBuilder.DropColumn(
                name: "LoadedMeasurementReference",
                table: "CustodyLots");

            migrationBuilder.DropColumn(
                name: "LoadedMeasurementTemperatureC",
                table: "CustodyLots");

            migrationBuilder.DropColumn(
                name: "LoadedSourceLiters",
                table: "CustodyLots");
        }
    }
}
