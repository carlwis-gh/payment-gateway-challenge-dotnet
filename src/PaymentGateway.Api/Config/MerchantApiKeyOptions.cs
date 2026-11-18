using Microsoft.AspNetCore.Authentication;

namespace PaymentGateway.Api.Config;

/// <summary>
/// The merchants allowed to call the API. 
/// Only a SHA-256 hash of each key is configured, so a leaked config file does not leak usable keys.
/// </summary>
public sealed class MerchantApiKeyOptions : AuthenticationSchemeOptions
{
    public List<MerchantApiKey> Merchants { get; set; } = new();
}
