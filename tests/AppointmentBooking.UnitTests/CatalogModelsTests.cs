using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Application.Catalog;

namespace AppointmentBooking.UnitTests;

public sealed class CatalogModelsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ServiceRequests_RejectMissingName(string? name)
    {
        Assert.Contains(Validate(new CreateServiceRequest(name!)),
            error => error.MemberNames.Contains(nameof(CreateServiceRequest.Name)));
        Assert.Contains(Validate(new UpdateServiceRequest(name!, true)),
            error => error.MemberNames.Contains(nameof(UpdateServiceRequest.Name)));
    }

    [Fact]
    public void ServiceRequests_AcceptNonEmptyName()
    {
        Assert.Empty(Validate(new CreateServiceRequest("Haircut")));
        Assert.Empty(Validate(new UpdateServiceRequest("Haircut", false)));
    }

    [Fact]
    public void Offering_RejectsNegativePrice()
    {
        var errors = Validate(new ConfigureStaffServiceRequest(-0.01m, 40, true));
        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(ConfigureStaffServiceRequest.Price)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Offering_RejectsNonPositiveDuration(int duration)
    {
        var errors = Validate(new ConfigureStaffServiceRequest(5000m, duration, true));
        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(ConfigureStaffServiceRequest.DurationMinutes)));
    }

    [Fact]
    public void Offering_AcceptsFreeServiceAndOneMinuteDuration()
    {
        Assert.Empty(Validate(new ConfigureStaffServiceRequest(0m, 1, true)));
    }

    [Fact]
    public void Offering_AcceptsDifferentEmployeePricesAndDurations()
    {
        Assert.Empty(Validate(new ConfigureStaffServiceRequest(5000m, 40, true)));
        Assert.Empty(Validate(new ConfigureStaffServiceRequest(6000m, 30, false)));
    }

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results,
            validateAllProperties: true);
        return results;
    }
}
