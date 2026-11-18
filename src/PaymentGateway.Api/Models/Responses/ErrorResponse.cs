namespace PaymentGateway.Api.Models.Responses;

/// <summary>
/// The single error shape used by every failure other than a rejected payment (DESIGN 5.2.2).
/// </summary>
public class ErrorResponse
{
    public ErrorResponse(string error)
    {
        Error = error;
    }

    public string Error { get; }
}
