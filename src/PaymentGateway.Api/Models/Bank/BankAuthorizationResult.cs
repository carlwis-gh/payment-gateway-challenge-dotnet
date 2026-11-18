namespace PaymentGateway.Api.Models.Bank;

/// <summary>
/// What one bank call produced: the gateway's reading of it, plus the bank's authorization code
/// when the bank returned one (DESIGN 5.2.3).
/// </summary>
/// <param name="Outcome">How the gateway interprets the bank call.</param>
/// <param name="AuthorizationCode">
/// The bank's reference for the authorization, kept for tracing and reconciliation. Null when the
/// bank sent none (declines send an empty string) or the call failed.
/// </param>
public sealed record BankAuthorizationResult(BankResult Outcome, string? AuthorizationCode = null);
