using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Authenticates a merchant from <c>Authorization: Bearer &lt;api key&gt;</c> (DESIGN D3).
/// The merchant id comes only from the matched key, never from anything else the caller sends.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<MerchantApiKeyOptions>
{
    private const string BearerPrefix = "Bearer ";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<MerchantApiKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unsupported authorization scheme"));
        }

        var key = header[BearerPrefix.Length..].Trim();
        var merchantId = key.Length == 0 ? null : FindMerchant(key);
        if (merchantId is null)
        {
            // The key itself is never logged.
            return Task.FromResult(AuthenticateResult.Fail("Unknown API key"));
        }

        var identity = new ClaimsIdentity(new[] { new Claim(MerchantAuth.MerchantIdClaim, merchantId) }, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>401 with the documented error body (DESIGN 2.3) instead of an empty response.</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new ErrorResponse(StringResources.Unauthorized));
    }

    private string? FindMerchant(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string? match = null;
        // Every entry is compared in constant time, with no early exit, so response timing does not
        // reveal how close a guess was.
        foreach (var merchant in Options.Merchants)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(merchant.KeyHash)))
            {
                match = merchant.MerchantId;
            }
        }

        return match;
    }
}
