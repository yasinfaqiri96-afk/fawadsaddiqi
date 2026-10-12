using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSalePricePending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSalePricePending",
                table: "SalesTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SalePriceAccountingDate",
                table: "SalesTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SalePriceFixedAtUtc",
                table: "SalesTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalePriceFixedByUserId",
                table: "SalesTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveredPendingPricingAccountId",
                table: "AccountingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTransactions_PendingSalePrice",
                table: "SalesTransactions",
                column: "IsSalePricePending",
                filter: "\"IsSalePricePending\"");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingSettings_DeliveredPendingPricingAccountId",
                table: "AccountingSettings",
                column: "DeliveredPendingPricingAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingSettings_Accounts_DeliveredPendingPricingAccountId",
                table: "AccountingSettings",
                column: "DeliveredPendingPricingAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // فروش با قیمت باز مبلغ صفر ذخیره دارد؛ حذف پرچم آن را به «فروش قطعی با قیمت صفر» تبدیل
            // می‌کند. پس برگشت فقط وقتی مجاز است که هیچ فروش در انتظار قیمت وجود نداشته باشد.
            // حساب ۱۳۲۰ و ژورنال‌هایش حذف نمی‌شوند؛ فقط ارجاعِ تنظیمات برداشته می‌شود.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "SalesTransactions" WHERE "IsSalePricePending") THEN
                        RAISE EXCEPTION 'Cannot roll back AddSalePricePending: sales awaiting a sale price exist. Price or cancel them first.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingSettings_Accounts_DeliveredPendingPricingAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropIndex(
                name: "IX_SalesTransactions_PendingSalePrice",
                table: "SalesTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountingSettings_DeliveredPendingPricingAccountId",
                table: "AccountingSettings");

            migrationBuilder.DropColumn(
                name: "IsSalePricePending",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SalePriceAccountingDate",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SalePriceFixedAtUtc",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "SalePriceFixedByUserId",
                table: "SalesTransactions");

            migrationBuilder.DropColumn(
                name: "DeliveredPendingPricingAccountId",
                table: "AccountingSettings");
        }
    }
}
