namespace PaymentGateway.Api.Config;

public sealed class BankClientOptions
{
    public const string SectionName = "Bank";

    public Uri BaseUrl { get; set; } = new("http://localhost:8080");

    /// <summary>
    /// How long to wait for the bank (DESIGN D7); a call that takes longer is a 504. Bound from "Bank:Timeout" (e.g. "00:00:10"), so
    /// it can be tuned per environment with the <c>Bank__Timeout</c> variable (DESIGN D17); this default applies
    /// only when it is not set.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
