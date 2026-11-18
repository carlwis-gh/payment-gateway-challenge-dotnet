using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Interfaces;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Validation;

namespace PaymentGateway.Api.Controllers;

/// <summary>Merchant-facing payment API (DESIGN 5.2).</summary>
[Route("api/payments")]
[ApiController]
[Produces("application/json")]
// DESIGN D3: Every action needs a valid API key. Unauthenticated calls get 401 from the
// middleware before model binding, so they never reach the repository or the bank.
[Authorize]
// rate-limiting: (DESIGN 8.6) Add [EnableRateLimiting("per-merchant")] here.
public class PaymentsController : ControllerBase
{
    private readonly IPaymentProcessor _processor;
    private readonly IPaymentsRepository _repository;

    public PaymentsController(IPaymentProcessor processor, IPaymentsRepository repository)
    {
        _processor = processor;
        _repository = repository;
    }

    /// <summary>The merchant proven by the API key. Nothing the caller sends can change it.</summary>
    private string MerchantId => User.FindFirstValue(MerchantAuth.MerchantIdClaim)
        ?? throw new InvalidOperationException("Authenticated principal has no merchant id.");

    /// <summary>Process a card payment.</summary>
    /// <remarks>
    /// The request is validated first; only a valid request is sent to the acquiring bank.
    /// Every call is a new payment attempt: there is no duplicate prevention (DESIGN D4), so a
    /// retry after a 504, or after a 502 whose outcome is unknown, may charge the card twice.
    /// </remarks>
    /// <response code="200">The bank answered: status is Authorized or Declined.</response>
    /// <response code="400">The request was Rejected (invalid fields, malformed JSON or a non-JSON Content-Type); the bank was not called and nothing was stored.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="502">The bank did not process the payment (safe to retry), or gave an unexpected answer (outcome unknown); the error message says which. Nothing was stored.</response>
    /// <response code="504">The bank did not answer in time; the outcome is unknown and nothing was stored.</response>
    /// <response code="500">An unexpected gateway error.</response>
    [HttpPost]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<RejectedResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status504GatewayTimeout)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PostPaymentAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PostPaymentRequest? request)
    {
        // AC-3: Malformed JSON, wrong types, non-integer or overflowing amounts are Rejected here.
        // The framework's messages are not returned, so submitted values are never echoed.
        if (!ModelState.IsValid)
        {
            var errors = ModelBindingErrors(ModelState).ToList();
            return BadRequest(new RejectedResponse { Errors = errors });
        }

        // The merchant id is already on the log scope (Program.cs, DESIGN D16).
        // telemetry: (DESIGN 8.5) Add it to the current trace span too, once traces are exported.
        // idempotency: (DESIGN 8.3) Read an Idempotency-Key header here and pass it to the processor.
        var result = await _processor.ProcessAsync(MerchantId, request);

        return result switch
        {
            // AC-1, AC-2: Authorized or Declined.
            PaymentResult.Processed processed => Ok(PaymentResponse.From(processed.Payment)),
            // AC-3: Validation failed; nothing was stored.
            PaymentResult.Rejected rejected => BadRequest(new RejectedResponse { Errors = rejected.Errors }),
            // AC-4, AC-5: Bank did not process it or gave an unexpected answer (502), or timed out (504).
            PaymentResult.BankFailure failure => StatusCode(failure.StatusCode, new ErrorResponse(failure.Message)),
            _ => throw new InvalidOperationException("Unhandled payment result.")
        };
    }

    /// <summary>Retrieve a previously processed payment.</summary>
    /// <param name="id">The payment id returned by the POST.</param>
    /// <response code="200">The stored payment details, as returned by the original POST.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="404">No such payment, a malformed id, or a payment owned by another merchant.</response>
    /// <response code="500">An unexpected gateway error.</response>
    [HttpGet("{id}")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status500InternalServerError)]
    public IActionResult GetPayment(string id)
    {
        // AC-10: A GET without a valid API key never gets here; the middleware returns 401.
        // AC-7: A malformed id is simply a payment that does not exist.
        if (!Guid.TryParse(id, out var paymentId))
        {
            return NotFound(new ErrorResponse(StringResources.PaymentNotFound));
        }

        // AC-8: Another merchant's payment is indistinguishable from an unknown one.
        // AC-6: Otherwise the stored details are returned as they were on the POST.
        var payment = _repository.Get(paymentId, MerchantId);
        return payment is null
            ? NotFound(new ErrorResponse(StringResources.PaymentNotFound))
            : Ok(PaymentResponse.From(payment));
    }

    /// <summary>
    /// Turns model-binding failures into Rejected error messages (DESIGN 5.3.1, 5.4).
    /// </summary>
    /// <remarks>
    /// Binding happens before the action runs, so a body that is not valid JSON, or has a field
    /// of the wrong type (<c>"amount": "abc"</c>, <c>"amount": 12.5</c>, a value too large for a
    /// long), never reaches <see cref="PaymentRequestValidator"/>. The automatic 400 is switched off
    /// in Program.cs so these cases still get the Rejected body rather than the framework's
    /// ProblemDetails. The framework's own messages are discarded because they can quote the
    /// submitted value (D11); only the field name is kept.
    /// </remarks>
    private static IEnumerable<string> ModelBindingErrors(ModelStateDictionary modelState)
    {
        foreach (var (key, entry) in modelState)
        {
            if (entry.ValidationState != ModelValidationState.Invalid)
            {
                continue;
            }

            // A body sent with a non-JSON Content-Type has no formatter to read it (DESIGN 5.4).
            if (entry.Errors.Any(e => e.Exception is UnsupportedContentTypeException))
            {
                yield return StringResources.UnsupportedContentType;
                continue;
            }

            // Each ModelState entry is keyed by where the error happened. System.Text.Json uses a
            // JSON path for that: "$.amount" means the "amount" property of the root object. When
            // the key has that shape we strip the "$." and name the field ("amount is invalid").
            // Any other key ("$" for the whole document, "request" for the parameter, or "" when
            // the body is empty) means the body as a whole could not be read, so one generic
            // message is returned instead.
            if (key.StartsWith("$.", StringComparison.Ordinal) && key.Length > 2)
            {
                yield return StringResources.FieldIsInvalid(key[2..]);
            }
            else
            {
                yield return StringResources.InvalidJsonBody;
            }
        }
    }
}
