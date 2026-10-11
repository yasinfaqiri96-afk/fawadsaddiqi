# DOMAIN MODEL — PTG Oil System

> تنها مرجع مدل دامنه. فهرست زیر دقیقاً ۶۹ `DbSet` موجود در `Data/ApplicationDbContext.cs` است.

## جریان کسب‌وکار

```text
Contract → Platt's / FX → Loading → Receipt → Inventory (Ilinka Stock)
        → Transport (موتر / واگن / کشتی) → Dispatch → Sales
        → Expenses → Payments / Ledger → Balance / Statements / P&L
```

## موجودیت‌ها به تفکیک دامنه

### Master Data
`Products` · `Currencies` · `Units` · `Partners` · `Companies` · `Suppliers` · `Customers` · `ServiceProviders` · `Terminals` · `StorageTanks` · `Vessels` · `Trucks` · `Wagons` · `Drivers` · `Locations` · `ExpenseTypes` · `Employees` · `Roles` · `Users`

### Contracts & Pricing
`Contracts` · `ContractPartners` · `ContractAmendments` · `ContractPricingRules` · `DailyPlattsPrices` · `DailyFxRates` · `PlattsMonthlyManuals`

### Inventory & Logistics
`InventoryBatches` · `InventoryMovements` · `LoadingRegisters` · `LoadingReceipts` · `LoadingReceiptAllocations` · `LoadingExpenseLines` · `InventoryTransportLegs` · `InventoryTransportBatches` · `InventoryTransportLegAllocations` · `InventoryTransportReceipts` · `TruckDispatches` · `Shipments` · `ShipmentContracts` · `DeliveryReceipts` · `LossEvents`

### Inventory Lineage (ردیابی lot)
`InventoryLots` · `InventoryLotMovements` · `SaleLotAllocations` · `LossLotAllocations` · `ExpenseLotAllocations`

### Custody (امانی — جنسِ متعلق به دیگران؛ هرگز موجودی ملکی نیست)
`CustodyLots` · `CustodyMovements` · `CustodyHolds` — جزئیات: `docs/custody-stock.md`

### Customs
`CustomsDeclarations` · `CustomsDeclarationItems` · `CustomsDeclarationDocuments`

### Sales & Expenses
`SalesTransactions` · `SalesBatches` · `ExpenseRules` · `ExpenseTransactions` · `ExpenseBatches`

### Finance
`CashAccounts` · `PaymentTransactions` · `Sarrafs` · `SarrafSettlements` · `ThreeWaySettlements` · `LedgerEntries` · `ContractBalanceTransfers` · `SupplierPaymentAllocations` · `EmployeeSalaryTransactions`

### Assets
`OperationalAssets` · `AssetOwnershipShares` · `AssetRentTransactions` · `AssetRentShares`

### System
`AuditLogs` · `ProcessedFormTokens`

## قواعد اجباری دامنه

این قواعد در کد enforce می‌شوند و بدون درخواست صریح نباید تغییر کنند:

- `LoadingReceipt` بعد از ثبت `InventoryMovement(In)` می‌سازد.
- `Dispatch` بعد از ثبت `InventoryMovement(Out)` می‌سازد.
- `Sales` بعد از ثبت `InventoryMovement(Out)` + `LedgerEntry` می‌سازد.
- `StockService` تنها منبع حقیقت موجودی است — stock از روی movementها محاسبه می‌شود، نه از یک ستون ذخیره‌شده.
- `PricingService` نرخ را بر اساس تاریخ تراکنش پیدا می‌کند، با fallback به آخرین نرخ معتبر.
- `Payments` همیشه از مسیر `LedgerEntry` دیده می‌شوند؛ `Balance` و `AccountStatements` هیچ relation حدسی ندارند.
- حساب صراف بیرون از `Ledger` است؛ پرداخت به صراف از مسیر `ManualPayment` + `SarrafId` انجام می‌شود (`ViaSarraf` یک جریان جداگانه است).
- کمیسیون دو رکورد می‌سازد: `Expense` (P&L) + خروج نقدی (`CommissionPayment`) یا `SarrafPayable` — بدون double-count.
- ثبت گروهی (مصرف/فروش) یک رکورد parent (`ExpenseBatch` / `SalesBatch`) می‌سازد و سهم‌ها رکوردهای عادی با Ledger خودشان هستند.
- سود/زیان پروندهٔ محموله فقط از `ShipmentNetResultUsd` خوانده می‌شود: کل فروش منهای قیمت کامل خرید همهٔ بار و تمام مصارف. بار فروش‌نشده برای منفی‌شدن نتیجه نیاز به ثبت ضایعات ندارد.
- `ContractAmendment` immutable است؛ متمم قرارداد قبلی را بازنویسی نمی‌کند.

## قراردادهای schema

| مورد | قاعده |
|---|---|
| مبلغ پولی | `numeric(18,4)` |
| وزن (MT) | `numeric(18,4)` |
| نرخ ارز | `numeric(18,6)` |
| درصد | `numeric(9,6)` |
| تاریخ | `timestamp with time zone` (UTC) |
| نام جدول | انگلیسی، PascalCase، جمع |
| Foreign key | `{Entity}Id` |
| Timestamp هر رکورد | `CreatedAtUtc`, `UpdatedAtUtc`, `CreatedByUserId`, `UpdatedByUserId` |
| Soft delete | استفاده نمی‌شود؛ ردیابی با `AuditLog` |
| Multi-company | اکثر جدول‌های عملیاتی `CompanyId` دارند |

## Migration

- ۷۴ migration، از `20260423012242_Initial` تا `20260712134845_AddSarrafSettlementDriverEmployee`.
- migrationهای دستی SQL (backfill) در `db/manual-migrations/`.
- ابزار: EF Core Migrations + Npgsql.

```bash
dotnet ef migrations add <Name> --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj --output-dir Migrations --namespace PTGOilSystem.Web.Migrations
dotnet ef database update --project src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj --startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj
```
