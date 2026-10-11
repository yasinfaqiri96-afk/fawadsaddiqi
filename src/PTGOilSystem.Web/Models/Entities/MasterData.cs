using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

public class Product : BaseEntity
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public int? SecondaryUnitId { get; set; }
    public Unit? SecondaryUnit { get; set; }
    [MaxLength(20)] public string UnitOfMeasure { get; set; } = "MT";
    [MaxLength(150)] public string? Category { get; set; }
    [MaxLength(1000)] public string? SecondaryUnitConversionNote { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Currency : BaseEntity
{
    [Required, MaxLength(10)] public string Code { get; set; } = "USD";
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Symbol { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Unit : BaseEntity
{
    [Required, MaxLength(20)] public string Code { get; set; } = "MT";
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Symbol { get; set; }
    [MaxLength(50)] public string? UnitType { get; set; }
    [MaxLength(50)] public string? BaseUnitCode { get; set; }
    public decimal? ConversionFactorToBase { get; set; }
    public bool IsBaseUnit { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Partner : BaseEntity, ICanonicalSearchable
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Country { get; set; }
    [MaxLength(100)] public string? ContactPerson { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(150)] public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { Code, Name, NamePersian });
}

public class Company : BaseEntity, ICanonicalSearchable
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string Country { get; set; } = "";
    [MaxLength(300)] public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>
    /// شرکتِ مالکِ سیستم و صاحبِ دفاترِ مالی. دقیقاً یک شرکت باید true باشد؛ یک ایندکسِ یکتای
    /// جزئی (partial unique) روی همین فیلد وجودِ بیش از یک مالک را در سطح دیتابیس هم می‌بندد.
    /// مالک‌بودن فقط از این فیلد خوانده می‌شود — نه از IsActive و نه از شناسهٔ هاردکد.
    /// </summary>
    public bool IsSystemOwner { get; set; }

    /// <summary>
    /// شریکی که دفترِ این شرکت، دفترِ شخصیِ اوست — یعنی وقتی این شرکت پولِ یک قراردادِ
    /// شراکتی را می‌پردازد، آن پول سرمایه‌گذاریِ همین شریک است، نه پولِ یک طرفِ سوم.
    ///
    /// چرا این ستون لازم شد: پیش از این تنها نشانهٔ «کدام شریک پرداخت کرد»
    /// <see cref="PaymentTransaction.FundingSource"/> = <see cref="PaymentFundingSource.Partner"/>
    /// به‌همراه <see cref="PaymentTransaction.PaidByPartnerId"/> بود. مالکِ شرکت که هم‌زمان
    /// شریکِ قرارداد است پول را از صندوقِ شرکتِ خودش می‌دهد، پس آن پرداخت
    /// <see cref="PaymentFundingSource.Company"/> ثبت می‌شود و در صورت‌حساب شراکت صفر
    /// می‌شد. این ستون همان رابطه را صریح و قطعی می‌کند؛ هیچ‌جا از روی نام حدس زده نمی‌شود.
    ///
    /// خالی یعنی شرکتِ مستقل — رفتار دقیقاً همان چیزی است که تا امروز بوده.
    /// </summary>
    public int? OwnerPartnerId { get; set; }
    public Partner? OwnerPartner { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { Code, Name, NamePersian });
}

public enum ServiceProviderType
{
    RailwayService = 1,
    WagonRent = 2,
    StorageProvider = 3,
    TerminalOperator = 4,
    TransportCompany = 5,
    CustomsBroker = 6,
    LoadingUnloadingService = 7,
    InspectionService = 8,
    DocumentationService = 9,
    Other = 10
}

public class ServiceProvider : BaseEntity
{
    [MaxLength(50)] public string? Code { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public ServiceProviderType ProviderType { get; set; } = ServiceProviderType.Other;
    [MaxLength(80)] public string? Country { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(150)] public string? Email { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(100)] public string? TaxNumber { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Supplier : BaseEntity, ICanonicalSearchable
{
    [MaxLength(50)] public string? Code { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Country { get; set; }
    [MaxLength(100)] public string? ContactPerson { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { Code, Name, NamePersian });
}

public class Customer : BaseEntity, ICanonicalSearchable
{
    [MaxLength(50)] public string? Code { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Country { get; set; }
    [MaxLength(100)] public string? ContactPerson { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { Code, Name, NamePersian });
}

public class Terminal : BaseEntity
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>
    /// خالی = تأسیساتِ خودِ ما. پر = مخزن/انبارِ طرفِ دیگر؛ تیلِ ملکیِ ما اینجا «نزد دیگران»
    /// است و در موجودی فزیکی مخازن ما شمرده نمی‌شود. تیل امانیِ دیگران اینجا ثبت نمی‌شود.
    /// </summary>
    public AccountingPartyType? CustodianPartyType { get; set; }
    public int? CustodianPartyId { get; set; }

    public bool IsThirdPartyFacility => CustodianPartyType.HasValue && CustodianPartyId.HasValue;
}

public class StorageTank : BaseEntity
{
    public int TerminalId { get; set; }
    public Terminal? Terminal { get; set; }
    [Required, MaxLength(50)] public string TankCode { get; set; } = "";
    [MaxLength(150)] public string? DisplayName { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal CapacityMt { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Vessel : BaseEntity
{
    [MaxLength(50)] public string? Code { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(50)] public string? Imo { get; set; }
    [MaxLength(50)] public string? Flag { get; set; }
    [MaxLength(150)] public string? OwnerOrOperator { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Truck : BaseEntity, ICanonicalSearchable
{
    [Required, MaxLength(50)] public string PlateNumber { get; set; } = "";
    [MaxLength(100)] public string? Owner { get; set; }
    public decimal? MaxLoadMt { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { PlateNumber, Owner });
}

public class Wagon : BaseEntity, ICanonicalSearchable
{
    [Required, MaxLength(50)] public string WagonNumber { get; set; } = "";
    [MaxLength(50)] public string? WagonType { get; set; }
    [MaxLength(100)] public string? Owner { get; set; }
    public decimal? CapacityMt { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    /// <summary>شکلِ canonical برای جستجو. متنِ نمایشی دست‌نخورده می‌ماند.</summary>
    [MaxLength(600)] public string? SearchKey { get; set; }

    public string BuildSearchSource() => string.Join(' ', new[] { WagonNumber, Owner });
}

public class Driver : BaseEntity
{
    [Required, MaxLength(200)] public string FullName { get; set; } = "";
    [MaxLength(50)] public string? LicenseNumber { get; set; }
    [MaxLength(50)] public string? NationalId { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

public class Location : BaseEntity
{
    [MaxLength(50)] public string? Code { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(20)] public string? Country { get; set; }
    [MaxLength(50)] public string Kind { get; set; } = "Destination"; // Origin / Transit / Destination
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
}

// مرحله ۵ — حسابِ بدهیِ متناظر با یک نوع مصرف در دفتر کل جدید.
// عمداً یک enum صریح است و نه استنتاج از Category: چون Category متن آزاد و قابل‌ویرایش کاربر
// است و نباید انتخاب حساب حسابداری به آن وابسته شود.
public enum ExpensePayableKind
{
    [System.ComponentModel.DataAnnotations.Display(Name = "حساب‌های پرداختنی")] AccountsPayable = 1,
    [System.ComponentModel.DataAnnotations.Display(Name = "کرایه پرداختنی")] FreightPayable = 2,
    [System.ComponentModel.DataAnnotations.Display(Name = "کمیسیون پرداختنی")] CommissionPayable = 3,
    [System.ComponentModel.DataAnnotations.Display(Name = "مصارف معوق")] AccruedExpense = 4
}

public class ExpenseType : BaseEntity
{
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? NamePersian { get; set; }
    [MaxLength(50)] public string Category { get; set; } = "Other"; // Storage, Trucking, Commission, ...
    public bool IsActive { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }

    // مرحله ۵ — حساب بدهیِ این نوع مصرف در دفتر کل جدید. توسط مدیر مالی تعیین می‌شود.
    // تا وقتی null باشد، مصارف این نوع به دفتر کل جدید پست نمی‌شوند (Skip) و حدس زده نمی‌شود.
    // منطق Ledger قدیمی و مانده‌ها به این فیلد وابسته نیستند.
    public ExpensePayableKind? PayableAccountKind { get; set; }
}

public class Role : BaseEntity
{
    [Required, MaxLength(50)] public string Name { get; set; } = "";
    [MaxLength(200)] public string? Description { get; set; }
    public bool CanManageData { get; set; }
    public bool CanManageUsers { get; set; }
    [MaxLength(1000)] public string? AllowedNavigationItems { get; set; }

    /// <summary>Permissionهای صریحِ اعطاشده (جداشده با کامه)، مثل HR.ViewSalary.</summary>
    [MaxLength(1000)] public string? GrantedPermissions { get; set; }
}

public class User : BaseEntity
{
    [Required, MaxLength(100)] public string Username { get; set; } = "";
    [Required, MaxLength(200)] public string FullName { get; set; } = "";
    [MaxLength(200)] public string? Email { get; set; }
    [Required, MaxLength(256)] public string PasswordHash { get; set; } = "";
    public int? RoleId { get; set; }
    public Role? Role { get; set; }
    public bool IsActive { get; set; } = true;
}
