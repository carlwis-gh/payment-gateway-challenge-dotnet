using PaymentGateway.Api.Models.Domain;

namespace PaymentGateway.Api.Models;

/// <summary>Every outcome of a POST /api/payments (DESIGN D9). The controller maps each to HTTP.</summary>
public abstract record PaymentResult
{
    private PaymentResult() { }

    /// <summary>Authorized or Declined: 200.</summary>
    public sealed record Processed(Payment Payment) : PaymentResult;

    /// <summary>Validation failed: 400 with status Rejected.</summary>
    public sealed record Rejected(IReadOnlyList<string> Errors) : PaymentResult;

    /// <summary>Bank unavailable, unexpected error (502) or timeout (504).</summary>
    public sealed record BankFailure(int StatusCode, string Message) : PaymentResult;
}
