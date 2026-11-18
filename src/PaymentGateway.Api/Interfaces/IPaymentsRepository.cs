using PaymentGateway.Api.Models.Domain;

namespace PaymentGateway.Api.Interfaces;

public interface IPaymentsRepository
{
    void Add(Payment payment);

    /// <summary>
    /// Returns the payment only if it exists and belongs to <paramref name="merchantId"/>
    /// (DESIGN D3); otherwise null, so another merchant's payment is indistinguishable from
    /// an unknown one.
    /// </summary>
    Payment? Get(Guid id, string merchantId);
}
