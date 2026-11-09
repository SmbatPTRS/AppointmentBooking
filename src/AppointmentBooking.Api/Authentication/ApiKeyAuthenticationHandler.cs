using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AppointmentBooking.Api.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiAccessOptions> access,
    IUserRepository users) : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        byte[] presented = Encoding.UTF8.GetBytes(values[0]!);
        ApiKeyEntry? entry = access.Value.Keys.FirstOrDefault(candidate =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate.Key), presented));

        User? user = entry is null
            ? null
            : await users.GetByTelegramUserIdAsync(entry.TelegramUserId, Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
