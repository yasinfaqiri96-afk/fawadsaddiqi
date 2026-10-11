using PTGOilSystem.Web.Services.Custody;
using PTGOilSystem.Web.Services.Exceptions;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public sealed class CustodyMeasurementTests
{
    [Theory]
    [InlineData(100000, 800, 80)]
    [InlineData(100000, 840, 84)]
    [InlineData(1000, 750, 0.75)]
    public void Uses_Documented_Density_At_Matching_Temperature(decimal liters, decimal density, decimal mt)
        => Assert.Equal(mt, new CustodyMeasurement(liters, density, 15m, 15m, "Product certificate").ToMt());

    [Fact]
    public void Rounds_Once_To_Canonical_Stock_Precision()
        => Assert.Equal(0.0001m, new CustodyMeasurement(0.0625m, 800m, 15m, 15m, "Certificate").ToMt());

    [Theory]
    [InlineData(20, 15)]
    [InlineData(15, 20)]
    public void Does_Not_Invent_Thermal_Correction(decimal volumeTemp, decimal densityTemp)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => new CustodyMeasurement(100000m, 800m, volumeTemp, densityTemp, "Certificate").ToMt());
        Assert.Equal("CUSTODY_MEASUREMENT_TEMPERATURE", ex.Code);
    }

    [Theory]
    [InlineData(0, 800, "Certificate", "CUSTODY_LITERS_INVALID")]
    [InlineData(100000, 0, "Certificate", "CUSTODY_DENSITY_INVALID")]
    [InlineData(100000, -800, "Certificate", "CUSTODY_DENSITY_INVALID")]
    [InlineData(100000, 800, "", "CUSTODY_MEASUREMENT_REFERENCE")]
    public void Rejects_Missing_Or_Invalid_Measurement(decimal liters, decimal density, string reference, string code)
        => Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => new CustodyMeasurement(liters, density, 15m, 15m, reference).ToMt()).Code);
}
