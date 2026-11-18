namespace PaymentGateway.Api.Models;

/// <summary>
/// The status of a stored payment (DESIGN 5.1).
/// </summary>
public enum PaymentStatus
{
    Authorized,
    Declined
}
