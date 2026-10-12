using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyMultiCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CommissionRecognizedAtSaleOwner",
                table: "CustodySales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrencyPerUsd",
                table: "CustodySales",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FxAgreementReference",
                table: "CustodySales",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxOwnerSharePercent",
                table: "CustodySales",
                type: "numeric(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FxResponsibility",
                table: "CustodySales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerAgreedPerUsd",
                table: "CustodySales",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerCurrency",
                table: "CustodySales",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerRateSource",
                table: "CustodySales",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerRateToUsd",
                table: "CustodySales",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RateSource",
                table: "CustodySales",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RateToUsd",
                table: "CustodySales",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaleAmount",
                table: "CustodySales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaleCommission",
                table: "CustodySales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "CustodySales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "CustodySaleReturns",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "CustodySaleReturns",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerCommissionReversed",
                table: "CustodySaleReturns",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerRateToUsd",
                table: "CustodySaleReturns",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RecognizedCommissionReversed",
                table: "CustodySaleReturns",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrencyPerUsd",
                table: "CustodyFinanceEntries",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxCompanyUsd",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxDifference",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxOwnerShare",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerAmount",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerCommission",
                table: "CustodyFinanceEntries",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerCurrency",
                table: "CustodyFinanceEntries",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OwnerRateToUsd",
                table: "CustodyFinanceEntries",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RateSource",
                table: "CustodyFinanceEntries",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RateToUsd",
                table: "CustodyFinanceEntries",
                type: "numeric(24,12)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodySales_Fx",
                table: "CustodySales",
                sql: "(\"OwnerCurrency\" IS NULL OR \"OwnerCurrency\" = \"Currency\" OR \"OwnerAgreedPerUsd\" > 0) AND (\"FxResponsibility\" IS NULL OR \"FxAgreementReference\" IS NOT NULL) AND (\"FxResponsibility\" IS NULL OR \"FxResponsibility\" <> 3 OR (\"FxOwnerSharePercent\" > 0 AND \"FxOwnerSharePercent\" < 100))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodySales_Fx",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "CommissionRecognizedAtSaleOwner",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "CurrencyPerUsd",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "FxAgreementReference",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "FxOwnerSharePercent",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "FxResponsibility",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "OwnerAgreedPerUsd",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "OwnerCurrency",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "OwnerRateSource",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "OwnerRateToUsd",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "RateSource",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "RateToUsd",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "SaleAmount",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "SaleCommission",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "CustodySales");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "CustodySaleReturns");

            migrationBuilder.DropColumn(
                name: "Commission",
                table: "CustodySaleReturns");

            migrationBuilder.DropColumn(
                name: "OwnerCommissionReversed",
                table: "CustodySaleReturns");

            migrationBuilder.DropColumn(
                name: "OwnerRateToUsd",
                table: "CustodySaleReturns");

            migrationBuilder.DropColumn(
                name: "RecognizedCommissionReversed",
                table: "CustodySaleReturns");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "Commission",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "CurrencyPerUsd",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "FxCompanyUsd",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "FxDifference",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "FxOwnerShare",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "OwnerAmount",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "OwnerCommission",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "OwnerCurrency",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "OwnerRateToUsd",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "RateSource",
                table: "CustodyFinanceEntries");

            migrationBuilder.DropColumn(
                name: "RateToUsd",
                table: "CustodyFinanceEntries");
        }
    }
}
