using PaymentGateway.Api.Models.Domain;

namespace PaymentGateway.Api.Models.Responses;

/// <summary>Payment details returned by both POST and GET (DESIGN 5.2.2).</summary>
public class PaymentResponse
{
    /// <summary>The gateway's payment id; use it to retrieve the payment.</summary>
    public Guid Id { get; init; }

    /// <summary>Authorized or Declined.</summary>
    public PaymentStatus Status { get; init; }

    /// <summary>The last four digits of the card. The full number is never returned.</summary>
    /// <example>8877</example>
    public string CardNumberLastFour { get; init; } = string.Empty;

    /// <example>12</example>
    public int ExpiryMonth { get; init; }

    /// <example>2030</example>
    public int ExpiryYear { get; init; }

    /// <example>GBP</example>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Minor currency units.</summary>
    /// <example>1250</example>
    public long Amount { get; init; }

    /// <summary>When the gateway recorded the bank's outcome, in UTC (ISO 8601).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    public static PaymentResponse From(Payment payment) => new()
    {
        Id = payment.Id,
        Status = payment.Status,
        CardNumberLastFour = payment.CardNumberLastFour,
        ExpiryMonth = payment.ExpiryMonth,
        ExpiryYear = payment.ExpiryYear,
        Currency = payment.Currency,
        Amount = payment.Amount,
        CreatedAt = payment.CreatedAt
    };
}
