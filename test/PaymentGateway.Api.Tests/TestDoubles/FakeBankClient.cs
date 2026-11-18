using System.Collections.Concurrent;

using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Tests.TestDoubles;

/// <summary>
/// Mimics the simulator by default (odd last digit authorizes, even declines, 0 is not
/// processed) and records every call. A fixed result can be set per test.
/// </summary>
public sealed class FakeBankClient : IAcquiringBankClient
{
    public ConcurrentQueue<BankAuthorizationRequest> Calls { get; } = new();

    /// <summary>When set, every call returns this instead of the simulator rules.</summary>
    public BankResult? NextResult { get; set; }

    /// <summary>When set, every call throws this: a gateway defect, not a bank failure.</summary>
    public Exception? ThrowOnCall { get; set; }

    /// <summary>Returned with every Authorized outcome, as the simulator does.</summary>
    public const string AuthorizationCode = "fake-auth-code-0001";

    public int CallCount => Calls.Count;

    public Task<BankAuthorizationResult> AuthorizeAsync(BankAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(request);
        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        var lastDigit = request.CardNumber[^1] - '0';
        var outcome = NextResult ?? lastDigit switch
        {
            0 => BankResult.NotProcessed,
            _ when lastDigit % 2 == 1 => BankResult.Authorized,
            _ => BankResult.Declined
        };

        return Task.FromResult(new BankAuthorizationResult(
            outcome,
            outcome == BankResult.Authorized ? AuthorizationCode : null));
    }
}
