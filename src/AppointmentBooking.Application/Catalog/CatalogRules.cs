using System.ComponentModel.DataAnnotations;

namespace AppointmentBooking.Application.Catalog;

/// <summary>
/// Entry-point-independent input validation. Services must call these rules even
/// when DTO validation has already run in an HTTP or Telegram adapter.
/// Shared application error mapping and authorization are added during integration.
/// </summary>
public static class CatalogRules
{
    public static void Validate(CreateServiceRequest request)
    {
        ValidateRequest(request);
        ValidateName(request.Name);
    }

    public static void Validate(UpdateServiceRequest request)
    {
        ValidateRequest(request);
        ValidateName(request.Name);
    }

    public static void Validate(ConfigureStaffServiceRequest request)
    {
        ValidateRequest(request);
        if (decimal.Round(request.Price, 2) != request.Price)
            throw new ValidationException("Price cannot have more than two decimal places.");
    }

    private static void ValidateName(string name)
    {
        if (name.Trim().Length > 200)
            throw new ValidationException("Service name cannot exceed 200 characters.");
    }

    private static void ValidateRequest(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validator.ValidateObject(request, new ValidationContext(request), validateAllProperties: true);
    }
}
