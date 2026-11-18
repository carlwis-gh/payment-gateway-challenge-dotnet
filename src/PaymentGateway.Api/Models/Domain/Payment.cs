namespace PaymentGateway.Api.Models.Domain;

/// <summary>
/// A processed payment (Authorized or Declined) as persisted by the repository (DESIGN 5.1).
/// </summary>
public sealed record Payment
{
    public required Guid Id { get; init; }
    public required PaymentStatus Status { get; init; }
    public required string CardNumberLastFour { get; init; }
    public required int ExpiryMonth { get; init; }
    public required int ExpiryYear { get; init; }
    public required string Currency { get; init; }
    public required long Amount { get; init; }
    public required string MerchantId { get; init; }

    /// <summary>When the gateway recorded the bank's outcome (UTC), from the injected clock (DESIGN D10, D15).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The acquiring bank's reference for this authorization, for tracing, debugging and
    /// reconciliation with the bank. Null when the bank sent none (declines). Not returned by the API.
    /// </summary>
    public string? AuthorizationCode { get; init; }
}
