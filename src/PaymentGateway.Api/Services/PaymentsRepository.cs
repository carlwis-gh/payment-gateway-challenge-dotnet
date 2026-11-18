using System.Collections.Concurrent;

using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models.Domain;

namespace PaymentGateway.Api.Services;

/// <summary>Thread-safe in-memory store.</summary>
public sealed class PaymentsRepository : IPaymentsRepository
{
    // Assumption-10: An in-memory store is acceptable; its contents are lost on restart.
    // persistence: (DESIGN 8.1) Replace with a database-backed implementation of IPaymentsRepository,
    // with a primary key on Id and an index on (MerchantId, Id) for the GET lookup.
    // idempotency: (DESIGN 8.3) The payments table would also hold the idempotency key and request
    // fingerprint, with a unique constraint on (MerchantId, IdempotencyKey). No separate store: the
    // repository would insert a Pending payment to reserve the key, then update that row.
    private readonly ConcurrentDictionary<Guid, Payment> _payments = new();

    public void Add(Payment payment)
    {
        if (!_payments.TryAdd(payment.Id, payment))
        {
            throw new InvalidOperationException("A payment with this id already exists.");
        }
    }

    public Payment? Get(Guid id, string merchantId)
    {
        return _payments.TryGetValue(id, out var payment)
               && string.Equals(payment.MerchantId, merchantId, StringComparison.Ordinal)
            ? payment
            : null;
    }
}
