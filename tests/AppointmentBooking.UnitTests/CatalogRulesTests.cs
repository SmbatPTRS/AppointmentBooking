using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Application.Catalog;

namespace AppointmentBooking.UnitTests;

public sealed class CatalogRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void InvalidNamesFailWhenCalledWithoutHttpValidation(string? name)
    {
        Assert.Throws<ValidationException>(() => CatalogRules.Validate(new CreateServiceRequest(name!)));
        Assert.Throws<ValidationException>(() => CatalogRules.Validate(new UpdateServiceRequest(name!, false)));
    }

    [Fact]
    public void NegativeFractionalPriceIsRejectedBeforePersistence() =>
        Assert.Throws<ValidationException>(() => CatalogRules.Validate(new ConfigureStaffServiceRequest(-0.01m, 40, true)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveDurationFailsWithoutHttpValidation(int duration) =>
        Assert.Throws<ValidationException>(() => CatalogRules.Validate(new ConfigureStaffServiceRequest(5000m, duration, true)));

    [Fact]
    public void FreeAndFractionalPricesAreValidAndActivationDoesNotChangeValidation()
    {
        CatalogRules.Validate(new ConfigureStaffServiceRequest(0m, 1, true));
        CatalogRules.Validate(new ConfigureStaffServiceRequest(5000.25m, 40, false));
        CatalogRules.Validate(new CreateServiceRequest("Սանրվածք"));
        CatalogRules.Validate(new UpdateServiceRequest("Стрижка", false));
    }

    [Fact]
    public void MissingRequestsFailClearly()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogRules.Validate((CreateServiceRequest)null!));
        Assert.Throws<ArgumentNullException>(() => CatalogRules.Validate((UpdateServiceRequest)null!));
        Assert.Throws<ArgumentNullException>(() => CatalogRules.Validate((ConfigureStaffServiceRequest)null!));
    }
}
