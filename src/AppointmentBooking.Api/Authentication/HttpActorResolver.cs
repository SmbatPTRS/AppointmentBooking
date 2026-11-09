using System.Security.Claims;
using AppointmentBooking.Application.Common;
using Microsoft.AspNetCore.Authentication;

namespace AppointmentBooking.Api.Authentication;

public sealed class HttpActorResolver
{
    public async Task<Actor> ResolveAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        string? userId = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!result.Succeeded || !Guid.TryParse(userId, out Guid id) || id == Guid.Empty)
        {
            throw new AppException(ErrorCodes.Unauthorized, "Authentication is required.", AppErrorKind.Unauthorized);
        }

        return new Actor(id);
    }
}