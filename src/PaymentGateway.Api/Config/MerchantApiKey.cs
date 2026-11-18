namespace PaymentGateway.Api.Config;

public sealed class MerchantApiKey
{
    public string MerchantId { get; set; } = "";

    /// <summary>Hex-encoded SHA-256 of the merchant's API key.</summary>
    public string KeyHash { get; set; } = "";

    internal bool IsValid() =>
        !string.IsNullOrWhiteSpace(MerchantId)
        && KeyHash.Length == 64
        && KeyHash.All(Uri.IsHexDigit);
}
