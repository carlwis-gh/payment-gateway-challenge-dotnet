namespace PaymentGateway.Api.Models.Requests;

/// <summary>
/// Merchant-facing payment request.
/// Every property is nullable so that a missing field is detected by validation instead of silently defaulting to 0 (DESIGN 5.4).
/// </summary>
public class PostPaymentRequest
{
    /// <summary>14 to 19 digits that pass the Luhn check. A string, to keep leading zeroes.</summary>
    /// <example>2222405343248877</example>
    public string? CardNumber { get; set; }

    /// <summary>1 to 12.</summary>
    /// <example>12</example>
    public int? ExpiryMonth { get; set; }

    /// <summary>Four digits. Month and year together must be in the future; the current month counts.</summary>
    /// <example>2030</example>
    public int? ExpiryYear { get; set; }

    /// <summary>GBP, EUR or USD, upper case.</summary>
    /// <example>GBP</example>
    public string? Currency { get; set; }

    /// <summary>Minor currency units, greater than zero (1250 is 12.50).</summary>
    /// <example>1250</example>
    public long? Amount { get; set; }

    /// <summary>3 or 4 digits. A string, to keep leading zeroes.</summary>
    /// <example>123</example>
    public string? Cvv { get; set; }
}
