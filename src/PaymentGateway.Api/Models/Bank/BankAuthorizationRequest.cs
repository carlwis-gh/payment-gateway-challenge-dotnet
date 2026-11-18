namespace PaymentGateway.Api.Models.Bank;

/// <summary>Card details for one bank call. Lives only for the duration of the call (DESIGN D11).</summary>
public sealed record BankAuthorizationRequest(
    string CardNumber,
    int ExpiryMonth,
    int ExpiryYear,
    string Currency,
    long Amount,
    string Cvv);
