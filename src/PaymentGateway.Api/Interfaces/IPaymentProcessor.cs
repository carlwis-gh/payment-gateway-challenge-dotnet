using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Interfaces;

public interface IPaymentProcessor
{
    Task<PaymentResult> ProcessAsync(string merchantId, PostPaymentRequest? request);
}
