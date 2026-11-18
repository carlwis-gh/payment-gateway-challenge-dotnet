using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Interfaces;

public interface IAcquiringBankClient
{
    /// <summary>
    /// Sends one authorisation request. Never throws for bank or network failures; they are
    /// translated into a <see cref="BankResult"/>. Never retries (DESIGN D7).
    /// </summary>
    /// <returns>The outcome, with the bank's authorization code when it sent one.</returns>
    Task<BankAuthorizationResult> AuthorizeAsync(BankAuthorizationRequest request, CancellationToken cancellationToken = default);
}
