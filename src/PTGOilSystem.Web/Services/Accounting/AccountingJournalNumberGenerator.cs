namespace PTGOilSystem.Web.Services.Accounting;

public interface IAccountingJournalNumberGenerator
{
    string ForContractBalanceTransfer(int companyId, int transferId);
    string ForSupplierPaymentAllocation(int companyId, int allocationId);
    string ForSupplierPaymentAllocationReversal(int companyId, int allocationId);
    string ForPayment(int companyId, int paymentId);
    string ForPaymentRevision(int companyId, int paymentId, int revision);
    string ForPaymentReversal(int companyId, int paymentId, int revision);
    string ForViaSarrafSupplierPayment(int companyId, int supplierLedgerEntryId);
    string ForExpense(int companyId, int expenseId);
    string ForExpenseReversal(int companyId, int expenseId);
    string ForInventoryReceiptReversal(int companyId, int loadingReceiptId);
    string ForPurchase(int companyId, int loadingRegisterId, int revision);
    string ForPurchaseReversal(int companyId, int loadingRegisterId, int revision);
    string ForInventoryReceipt(int companyId, int loadingReceiptId);
    string ForSale(int companyId, int salesTransactionId);
    string ForSaleReversal(int companyId, int salesTransactionId);
    string ForCogs(int companyId, int salesTransactionId);
    string ForCogsReversal(int companyId, int salesTransactionId);
    /// <summary>بهای کالای تحویل‌شده با قیمت باز: موجودی → کالای تحویل‌شده در انتظار قیمت.</summary>
    string ForSaleDeliveredCost(int companyId, int salesTransactionId);
    string ForSaleDeliveredCostReversal(int companyId, int salesTransactionId);
    string ForCustomerAdvanceApplication(int companyId, int applicationId);
    string ForCustomerAdvanceApplicationReversal(int companyId, int applicationId);
    string ForInventoryLoss(int companyId, int lossEventId);
    string ForInventoryLossReversal(int companyId, int lossEventId);
    string ForShortageCharge(int companyId, int transportReceiptId);

    /// <summary>تخصیص سود قرارداد شراکتی به حساب جاری شرکا.</summary>
    string ForProfitAllocation(int companyId, int contractId);
    string ForSarrafSettlement(int companyId, int settlementId, int revision);
    string ForSarrafSettlementReversal(int companyId, int settlementId, int revision);
    string ForThreeWaySettlement(int companyId, int settlementId);
    string ForThreeWaySettlementReversal(int companyId, int settlementId);
    string ForTransportLegLoad(int companyId, int transportLegId);
    string ForTransportLegLoadReversal(int companyId, int transportLegId);
    string ForTransportReceipt(int companyId, int transportReceiptId);
    string ForTransportInventoryReceipt(int companyId, int transportReceiptId);
    string ForAssetRent(int companyId, int assetRentTransactionId);
    string ForAssetRentReversal(int companyId, int assetRentTransactionId);
    string ForEmployeeSalary(int companyId, int salaryTransactionId);
    string ForEmployeeSalaryReversal(int companyId, int salaryTransactionId);
}

public sealed class AccountingJournalNumberGenerator : IAccountingJournalNumberGenerator
{
    public string ForContractBalanceTransfer(int companyId, int transferId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (transferId <= 0)
            throw new ArgumentOutOfRangeException(nameof(transferId));

        // Transfer ids are database-generated and globally unique. Including the
        // company keeps the number readable while remaining deterministic on retry.
        return $"CBT-{companyId:D6}-{transferId:D10}";
    }

    public string ForSupplierPaymentAllocation(int companyId, int allocationId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (allocationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationId));

        return $"SPA-{companyId:D6}-{allocationId:D10}";
    }

    public string ForSupplierPaymentAllocationReversal(int companyId, int allocationId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (allocationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationId));

        return $"SPAR-{companyId:D6}-{allocationId:D10}";
    }

    public string ForPayment(int companyId, int paymentId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (paymentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(paymentId));

        return $"PAY-{companyId:D6}-{paymentId:D10}";
    }

    // A payment can be corrected after posting, so revisions past the first carry a suffix.
    // Revision 0 keeps the legacy PAY- number so already-posted journals stay addressable.
    public string ForPaymentRevision(int companyId, int paymentId, int revision)
    {
        if (revision == 0)
            return ForPayment(companyId, paymentId);
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (paymentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(paymentId));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return $"PAY-{companyId:D6}-{paymentId:D10}-R{revision:D3}";
    }

    public string ForPaymentReversal(int companyId, int paymentId, int revision)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (paymentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(paymentId));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return $"PAYR-{companyId:D6}-{paymentId:D10}-R{revision:D3}";
    }

    public string ForViaSarrafSupplierPayment(int companyId, int supplierLedgerEntryId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (supplierLedgerEntryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(supplierLedgerEntryId));

        // The via-sarraf flow writes no PaymentTransaction, so the supplier ledger row is the
        // only stable, database-generated identity for the event.
        return $"VSS-{companyId:D6}-{supplierLedgerEntryId:D10}";
    }

    public string ForExpense(int companyId, int expenseId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (expenseId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expenseId));

        return $"EXP-{companyId:D6}-{expenseId:D10}";
    }

    public string ForExpenseReversal(int companyId, int expenseId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (expenseId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expenseId));

        return $"EXPR-{companyId:D6}-{expenseId:D10}";
    }

    public string ForInventoryReceiptReversal(int companyId, int loadingReceiptId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (loadingReceiptId <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadingReceiptId));

        return $"INVR-{companyId:D6}-{loadingReceiptId:D10}";
    }

    // A loading can be repriced, so the purchase number carries a revision. Revision 0 is the
    // first posting; each reprice reverses the previous revision and posts the next one.
    public string ForPurchase(int companyId, int loadingRegisterId, int revision)
        => $"PUR-{ValidatePurchaseKey(companyId, loadingRegisterId, revision)}";

    public string ForPurchaseReversal(int companyId, int loadingRegisterId, int revision)
        => $"PURR-{ValidatePurchaseKey(companyId, loadingRegisterId, revision)}";

    public string ForInventoryReceipt(int companyId, int loadingReceiptId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (loadingReceiptId <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadingReceiptId));

        return $"INV-{companyId:D6}-{loadingReceiptId:D10}";
    }

    public string ForSale(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"SAL-{companyId:D6}-{salesTransactionId:D10}";
    }

    // برگشت رسمی فروش. شمارهٔ جدا نگه داشته می‌شود تا ژورنال اصلی همچنان با شمارهٔ خودش قابل ارجاع بماند.
    public string ForSaleReversal(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"SALR-{companyId:D6}-{salesTransactionId:D10}";
    }

    public string ForSaleDeliveredCost(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"SDC-{companyId:D6}-{salesTransactionId:D10}";
    }

    public string ForSaleDeliveredCostReversal(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"SDCR-{companyId:D6}-{salesTransactionId:D10}";
    }

    public string ForCogs(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"COGS-{companyId:D6}-{salesTransactionId:D10}";
    }

    public string ForCogsReversal(int companyId, int salesTransactionId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (salesTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(salesTransactionId));

        return $"COGSR-{companyId:D6}-{salesTransactionId:D10}";
    }

    // انتقالِ پیش‌دریافت به مطالبات وقتی تخصیصِ دریافت بعد از یک تحویلِ دارای طلبِ باز ثبت می‌شود.
    public string ForCustomerAdvanceApplication(int companyId, int applicationId)
        => $"CAA-{ValidateKey(companyId, applicationId, nameof(applicationId))}";

    public string ForCustomerAdvanceApplicationReversal(int companyId, int applicationId)
        => $"CAAR-{ValidateKey(companyId, applicationId, nameof(applicationId))}";

    public string ForInventoryLoss(int companyId, int lossEventId)
        => $"LOSS-{ValidateKey(companyId, lossEventId, nameof(lossEventId))}";

    public string ForInventoryLossReversal(int companyId, int lossEventId)
        => $"LOSSR-{ValidateKey(companyId, lossEventId, nameof(lossEventId))}";

    public string ForShortageCharge(int companyId, int transportReceiptId)
        => $"SHT-{ValidateKey(companyId, transportReceiptId, nameof(transportReceiptId))}";

    public string ForProfitAllocation(int companyId, int contractId)
        => $"PALLOC-{ValidateKey(companyId, contractId, nameof(contractId))}";

    // A sarraf settlement can be edited after posting, so its number carries a revision the same
    // way a repriced purchase does: each edit reverses the previous revision and posts the next.
    public string ForSarrafSettlement(int companyId, int settlementId, int revision)
        => $"SRF-{ValidateRevisionKey(companyId, settlementId, revision, nameof(settlementId))}";

    public string ForSarrafSettlementReversal(int companyId, int settlementId, int revision)
        => $"SRFR-{ValidateRevisionKey(companyId, settlementId, revision, nameof(settlementId))}";

    public string ForThreeWaySettlement(int companyId, int settlementId)
        => $"TWS-{ValidateKey(companyId, settlementId, nameof(settlementId))}";

    public string ForThreeWaySettlementReversal(int companyId, int settlementId)
        => $"TWSR-{ValidateKey(companyId, settlementId, nameof(settlementId))}";

    // A transfer is two events with time between them: the leg load takes the goods out of the
    // source terminal, and each receipt lands part of them at the destination. Both carry their
    // own number because both are genuine, separately dated journals.
    public string ForTransportLegLoad(int companyId, int transportLegId)
        => $"TRL-{ValidateKey(companyId, transportLegId, nameof(transportLegId))}";

    public string ForTransportLegLoadReversal(int companyId, int transportLegId)
        => $"TRLR-{ValidateKey(companyId, transportLegId, nameof(transportLegId))}";

    public string ForTransportReceipt(int companyId, int transportReceiptId)
        => $"TRR-{ValidateKey(companyId, transportReceiptId, nameof(transportReceiptId))}";

    // رسیدِ حملی که کالای خریداری‌شده را وارد موجودی می‌کند. شمارهٔ آن عمداً از
    // ForInventoryReceipt (که کلیدش LoadingReceipt است) جداست: شناسهٔ این دو جدول مستقل‌اند و
    // یک پیشوندِ مشترک می‌توانست دو رویدادِ متفاوت را زیر یک شماره ببرد.
    public string ForTransportInventoryReceipt(int companyId, int transportReceiptId)
        => $"TINV-{ValidateKey(companyId, transportReceiptId, nameof(transportReceiptId))}";

    public string ForAssetRent(int companyId, int assetRentTransactionId)
        => $"ART-{ValidateKey(companyId, assetRentTransactionId, nameof(assetRentTransactionId))}";

    public string ForAssetRentReversal(int companyId, int assetRentTransactionId)
        => $"ARTR-{ValidateKey(companyId, assetRentTransactionId, nameof(assetRentTransactionId))}";

    public string ForEmployeeSalary(int companyId, int salaryTransactionId)
        => $"SAL-{ValidateKey(companyId, salaryTransactionId, nameof(salaryTransactionId))}";

    public string ForEmployeeSalaryReversal(int companyId, int salaryTransactionId)
        => $"SALR-{ValidateKey(companyId, salaryTransactionId, nameof(salaryTransactionId))}";

    private static string ValidateKey(int companyId, int entityId, string entityIdName)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (entityId <= 0)
            throw new ArgumentOutOfRangeException(entityIdName);

        return $"{companyId:D6}-{entityId:D10}";
    }

    private static string ValidateRevisionKey(int companyId, int entityId, int revision, string entityIdName)
    {
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return $"{ValidateKey(companyId, entityId, entityIdName)}-{revision:D3}";
    }

    private static string ValidatePurchaseKey(int companyId, int loadingRegisterId, int revision)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId));
        if (loadingRegisterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(loadingRegisterId));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return $"{companyId:D6}-{loadingRegisterId:D10}-{revision:D3}";
    }
}
