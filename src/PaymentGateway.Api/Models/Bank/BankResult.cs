namespace PaymentGateway.Api.Models.Bank;

/// <summary>How the gateway interprets a bank call (DESIGN 5.2.3 table).</summary>
public enum BankResult
{
    Authorized,
    Declined,

    /// <summary>503 or 400: the bank did not process the payment; safe to retry.</summary>
    NotProcessed,

    /// <summary>Any other failure; the bank may have processed the payment.</summary>
    Unknown,

    TimedOut
}
