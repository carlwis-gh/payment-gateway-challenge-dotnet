namespace PaymentGateway.Api.Models.Responses;

/// <summary>Body for a rejected payment request (400). No payment was created (DESIGN D8).</summary>
public class RejectedResponse
{
    public const string RejectedStatus = "Rejected";

    /// <summary>Always "Rejected".</summary>
    /// <example>Rejected</example>
    public string Status { get; init; } = RejectedStatus;

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
