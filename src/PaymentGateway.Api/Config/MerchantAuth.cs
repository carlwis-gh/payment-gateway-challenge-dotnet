namespace PaymentGateway.Api.Config;

/// <summary>Names shared by the API key scheme, the controller and the tests (DESIGN D3).</summary>
public static class MerchantAuth
{
    public const string Scheme = "ApiKey";
    public const string MerchantIdClaim = "merchant_id";
    public const string ConfigSection = "MerchantAuth";
}
