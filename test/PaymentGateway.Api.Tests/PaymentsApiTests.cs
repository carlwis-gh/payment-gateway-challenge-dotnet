using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Tests.TestDoubles;

namespace PaymentGateway.Api.Tests;

/// <summary>End-to-end behaviour of the HTTP API with a fake bank (DESIGN 7.1, 7.2).</summary>
public sealed class PaymentsApiTests : IDisposable
{
    private const string KeyA = GatewayFactory.MerchantAKey;
    private const string KeyB = GatewayFactory.MerchantBKey;

    private readonly GatewayFactory _factory = new();
    private readonly HttpClient _client;

    public PaymentsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string ValidBody(string card = TestData.AuthorizedCard, long amount = 1250) => $$"""
        {
          "cardNumber": "{{card}}",
          "expiryMonth": 12,
          "expiryYear": 2030,
          "currency": "GBP",
          "amount": {{amount}},
          "cvv": "123"
        }
        """;

    private Task<HttpResponseMessage> Post(string? body, string? apiKey = KeyA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        Authenticate(request, apiKey);
        return Send(request);
    }

    private static void Authenticate(HttpRequestMessage request, string? apiKey)
    {
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
    {
        var response = await _client.SendAsync(request);
        // Buffer so a test can read the body more than once.
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private Task<HttpResponseMessage> Get(string id, string? apiKey = KeyA)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/payments/{id}");
        Authenticate(request, apiKey);
        return Send(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertPaymentDetails(JsonElement json, string status, string lastFour)
    {
        Assert.True(Guid.TryParse(json.GetProperty("id").GetString(), out _));
        Assert.Equal(status, json.GetProperty("status").GetString());
        Assert.Equal(lastFour, json.GetProperty("cardNumberLastFour").GetString());
        Assert.Equal(12, json.GetProperty("expiryMonth").GetInt32());
        Assert.Equal(2030, json.GetProperty("expiryYear").GetInt32());
        Assert.Equal("GBP", json.GetProperty("currency").GetString());
        Assert.Equal(1250, json.GetProperty("amount").GetInt64());
        Assert.Equal(TestData.Now, json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.False(json.TryGetProperty("merchantId", out _));
        Assert.False(json.TryGetProperty("authorizationCode", out _)); // stored, not returned
        Assert.Equal(8, json.EnumerateObject().Count());
    }

    private static async Task AssertRejected(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
        Assert.NotEmpty(json.GetProperty("errors").EnumerateArray());
    }

    private static async Task<string> AssertError(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var json = await Json(response);
        Assert.Single(json.EnumerateObject());
        var error = json.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    [Fact]
    public async Task Post_OddCard_IsAuthorized() // AC-1
    {
        var response = await Post(ValidBody(TestData.AuthorizedCard));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPaymentDetails(await Json(response), "Authorized", "8877");
    }

    [Fact]
    public async Task Post_EvenCard_IsDeclined() // AC-2
    {
        var response = await Post(ValidBody(TestData.DeclinedCard));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPaymentDetails(await Json(response), "Declined", "8112");
    }

    [Fact]
    public async Task Post_CardWithLeadingZeroesInLastFour_KeepsThem()
    {
        var response = await Post(ValidBody("22224053432400" + "07"));

        Assert.Equal("0007", (await Json(response)).GetProperty("cardNumberLastFour").GetString());
    }

    [Fact]
    public async Task Post_InvalidField_IsRejected_AndBankNotCalled() // AC-3
    {
        var response = await Post(ValidBody().Replace("\"GBP\"", "\"JPY\""));

        await AssertRejected(response);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    public static TheoryData<string?> MalformedBodies => new()
    {
        null,
        "",
        "null",
        "{",
        "not json",
        "[]",
        "{}"
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Post_MissingOrMalformedBody_IsRejected(string? body) // AC-3
    {
        await AssertRejected(await Post(body));
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Post_NonJsonContentType_IsRejected_AndBankNotCalled(string contentType) // AC-3, DESIGN 5.4
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(ValidBody(), Encoding.UTF8, contentType)
        };
        Authenticate(request, KeyA);

        var response = await Send(request);

        await AssertRejected(response);
        var errors = (await Json(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Content-Type must be application/json" }, errors);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("\"1250\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1e3")]
    [InlineData("true")]
    public async Task Post_AmountNotA64BitInteger_IsRejected(string amount) // DESIGN 2.2
    {
        var body = ValidBody().Replace("\"amount\": 1250", $"\"amount\": {amount}");

        var response = await Post(body);

        await AssertRejected(response);
        var errors = (await Json(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("amount is invalid", errors);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    [Fact]
    public async Task Post_CardNumberAsJsonNumber_IsRejected()
    {
        var body = ValidBody().Replace($"\"{TestData.AuthorizedCard}\"", TestData.AuthorizedCard);

        await AssertRejected(await Post(body));
    }

    public static TheoryData<string?> BadCredentials => new()
    {
        null, // no Authorization header
        "", // empty key
        "not-a-real-key",
        GatewayFactory.MerchantA // a merchant id is not a key
    };

    [Theory]
    [MemberData(nameof(BadCredentials))]
    public async Task Post_WithoutAValidApiKey_Is401_AndBankNotCalled(string? apiKey) // AC-10, DESIGN D3
    {
        await AssertError(await Post(ValidBody(), apiKey), HttpStatusCode.Unauthorized);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    [Fact]
    public async Task Post_WithNonBearerScheme_Is401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(ValidBody(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {KeyA}");

        await AssertError(await Send(request), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_InvalidKeyAndMalformedBody_Is401_NotRejected() // DESIGN D3
    {
        await AssertError(await Post("{", apiKey: "not-a-real-key"), HttpStatusCode.Unauthorized);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    [Fact]
    public async Task Post_SpoofedMerchantIdHeader_IsIgnored() // DESIGN D3
    {
        // Merchant B sends its own key but claims to be merchant A.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments")
        {
            Content = new StringContent(ValidBody(), Encoding.UTF8, "application/json")
        };
        Authenticate(request, KeyB);
        request.Headers.TryAddWithoutValidation("Merchant-Id", GatewayFactory.MerchantA);
        var id = (await Json(await Send(request))).GetProperty("id").GetString()!;

        // The payment belongs to B, the merchant the key proves, not to A.
        Assert.Equal(HttpStatusCode.OK, (await Get(id, KeyB)).StatusCode);
        await AssertError(await Get(id, KeyA), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_RejectedBody_DoesNotEchoSubmittedValues()
    {
        const string body = """
            { "cardNumber": "98765432109X", "expiryMonth": 77, "expiryYear": 31,
              "currency": "QQQ", "amount": -4242, "cvv": "9z" }
            """;

        var response = await Post(body);

        await AssertRejected(response);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var value in new[] { "98765432109X", "77", "31", "QQQ", "4242", "9z" })
        {
            Assert.DoesNotContain(value, text);
        }
    }

    [Fact]
    public async Task Post_TypeErrorBody_DoesNotEchoSubmittedValues()
    {
        var response = await Post(ValidBody().Replace("\"amount\": 1250", "\"amount\": \"secret-9911\""));

        Assert.DoesNotContain("secret-9911", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(BankResult.NotProcessed, HttpStatusCode.BadGateway, StringResources.BankNotProcessed)] // AC-4: bank 503 or 400
    [InlineData(BankResult.Unknown, HttpStatusCode.BadGateway, StringResources.BankOutcomeUnknown)] // AC-4: unexpected error
    [InlineData(BankResult.TimedOut, HttpStatusCode.GatewayTimeout, StringResources.BankTimeout)] // AC-5
    public async Task Post_BankFailure_ReturnsErrorBody_ThatSaysWhetherTheOutcomeIsKnown(
        BankResult bankResult, HttpStatusCode expected, string message) // DESIGN D7
    {
        _factory.Bank.NextResult = bankResult;

        Assert.Equal(message, await AssertError(await Post(ValidBody()), expected));
    }

    [Fact]
    public async Task Post_UnexpectedGatewayError_Is500_WithTheErrorShape_AndNoDetails() // DESIGN D9, 2.3
    {
        _factory.Bank.ThrowOnCall = new InvalidOperationException("secret internal detail");

        var response = await Post(ValidBody());

        Assert.Equal(StringResources.UnexpectedError, await AssertError(response, HttpStatusCode.InternalServerError));
        Assert.DoesNotContain("secret internal detail", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_CardEndingInZero_Is502() // AC-4 via simulator rules in the fake
    {
        await AssertError(await Post(ValidBody(TestData.UnavailableCard)), HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Get_ReturnsTheSameDetailsAsThePost() // AC-6
    {
        var posted = await Json(await Post(ValidBody()));
        var id = posted.GetProperty("id").GetString()!;

        var response = await Get(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(posted.GetRawText(), (await Json(response)).GetRawText());
    }

    [Fact]
    public async Task Get_StillWorksAfterTheCardExpires() // DESIGN 1.3.2
    {
        var id = (await Json(await Post(ValidBody()))).GetProperty("id").GetString()!;
        _factory.Clock.Advance(TimeSpan.FromDays(365 * 10));

        Assert.Equal(HttpStatusCode.OK, (await Get(id)).StatusCode);
    }

    [Theory]
    [InlineData("3f2b8c1e-5d4a-4e7b-9a10-2c6f8d0b1e77")]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    public async Task Get_UnknownOrMalformedId_Is404(string id) // AC-7
    {
        await AssertError(await Get(id), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_AnotherMerchantsPayment_Is404_WithTheSameBodyAsUnknown() // AC-8
    {
        var id = (await Json(await Post(ValidBody()))).GetProperty("id").GetString()!;

        var otherMerchant = await AssertError(await Get(id, KeyB), HttpStatusCode.NotFound);
        var unknown = await AssertError(await Get(Guid.NewGuid().ToString()), HttpStatusCode.NotFound);

        Assert.Equal(unknown, otherMerchant);
    }

    [Theory]
    [MemberData(nameof(BadCredentials))]
    public async Task Get_WithoutAValidApiKey_Is401(string? apiKey) // AC-10
    {
        var id = (await Json(await Post(ValidBody()))).GetProperty("id").GetString()!;

        var response = await Get(id, apiKey);

        await AssertError(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task NoResponse_ContainsTheFullCardNumberOrCvv() // AC-9
    {
        const string cvv = "9876";
        var bodies = new List<string>();
        async Task Capture(HttpResponseMessage r) => bodies.Add(await r.Content.ReadAsStringAsync());
        string Body(string card) => ValidBody(card).Replace("\"cvv\": \"123\"", $"\"cvv\": \"{cvv}\"");

        await Capture(await Post(Body(TestData.AuthorizedCard)));
        await Capture(await Post(Body(TestData.DeclinedCard)));
        await Capture(await Post(Body(TestData.UnavailableCard)));
        await Capture(await Post(Body(TestData.AuthorizedCard).Replace("GBP", "JPY")));
        var id = JsonDocument.Parse(bodies[0]).RootElement.GetProperty("id").GetString();
        await Capture(await Get(id!));

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(TestData.AuthorizedCard, body);
            Assert.DoesNotContain(TestData.DeclinedCard, body);
            Assert.DoesNotContain(TestData.UnavailableCard, body);
            Assert.DoesNotContain(cvv, body);
        }
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreHealthy_WithoutAnApiKey(string path) // DESIGN D18
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthEndpoints_DoNotCallTheBank() // DESIGN D18: a bank outage must not fail liveness
    {
        _factory.Bank.ThrowOnCall = new HttpRequestException("bank down");

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(0, _factory.Bank.CallCount);
    }

    // DESIGN D11, 2.3: The full card number, CVV and API keys are never logged. Responses are
    // covered by AC-9; this covers the other place they could leak. Every path that handles card
    // data or a key is exercised, and every log line, at every level and category, is checked.
    [Fact]
    public async Task NoLogLine_ContainsTheFullCardNumber_Cvv_OrApiKey()
    {
        const string cvv = "4831";
        const string wrongKey = "sk_wrong_key_77123";
        string Body(string card) => ValidBody(card).Replace("\"cvv\": \"123\"", $"\"cvv\": \"{cvv}\"");

        await Post(Body(TestData.AuthorizedCard));
        await Post(Body(TestData.DeclinedCard));
        await Post(Body(TestData.UnavailableCard));
        await Post(Body(TestData.AuthorizedCard).Replace("GBP", "JPY")); // Rejected by validation
        await Post(Body(TestData.AuthorizedCard).Replace("\"amount\": 1250", "\"amount\": \"x\"")); // Rejected by binding
        await Post(Body(TestData.AuthorizedCard), apiKey: wrongKey); // 401
        _factory.Bank.NextResult = BankResult.TimedOut;
        await Post(Body(TestData.AuthorizedCard));
        _factory.Bank.NextResult = null;
        _factory.Bank.ThrowOnCall = new InvalidOperationException("boom");
        await Post(Body(TestData.AuthorizedCard)); // 500

        var entries = _factory.Logs.Entries.ToList();
        Assert.NotEmpty(entries);
        foreach (var text in entries.Select(e => e.AllText))
        {
            Assert.DoesNotContain(TestData.AuthorizedCard, text);
            Assert.DoesNotContain(TestData.DeclinedCard, text);
            Assert.DoesNotContain(TestData.UnavailableCard, text);
            Assert.DoesNotContain(KeyA, text);
            Assert.DoesNotContain(wrongKey, text);
            // Trace, span and request ids are random hex and can contain any four digits, so they
            // are removed before looking for the CVV as a standalone number.
            var withoutIds = Regex.Replace(text, "[0-9A-Za-z-]{8,}", "#");
            Assert.DoesNotMatch($"(?<![0-9.]){cvv}(?![0-9])", withoutIds);
        }
    }

    [Fact]
    public async Task PaymentLogLines_CarryTheMerchantId_AndTheRequestTraceId() // DESIGN D16
    {
        await Post(ValidBody(), KeyB);

        var outcome = Assert.Single(_factory.Logs.Entries, e => e.Message.StartsWith("Payment ") && e.Message.Contains("Authorized"));
        Assert.Equal(GatewayFactory.MerchantB, outcome.Property("MerchantId"));
        Assert.False(string.IsNullOrEmpty(outcome.Property("TraceId")?.ToString()));
        Assert.False(string.IsNullOrEmpty(outcome.Property("RequestId")?.ToString()));
    }

    [Fact]
    public async Task EveryGatewayLogLineOfAnAuthenticatedRequest_CarriesTheMerchantId() // DESIGN D16
    {
        await Post(ValidBody().Replace("GBP", "JPY"), KeyA); // Rejected
        await Post(ValidBody(), KeyA); // Authorized
        _factory.Bank.ThrowOnCall = new InvalidOperationException("boom");
        await Post(ValidBody(), KeyA); // 500

        // The gateway's own lines after authentication: the processor, the bank client and the
        // unhandled-error handler. The authentication handler's own lines come before the merchant
        // is known, so they cannot carry it.
        var gatewayLines = _factory.Logs.Entries
            .Where(e => e.Category.StartsWith("PaymentGateway.", StringComparison.Ordinal) || e.Category == "Program")
            .Where(e => !e.Category.EndsWith(nameof(PaymentGateway.Api.Services.ApiKeyAuthenticationHandler), StringComparison.Ordinal))
            .ToList();
        Assert.Contains(gatewayLines, e => e.Message.StartsWith("Payment request rejected"));
        Assert.Contains(gatewayLines, e => e.Message.StartsWith("Payment "));
        Assert.Contains(gatewayLines, e => e.Message.StartsWith("Unhandled exception"));
        Assert.All(gatewayLines, e => Assert.Equal(GatewayFactory.MerchantA, e.Property("MerchantId")));
    }

    [Fact]
    public void Startup_FailsWithoutAnyMerchant() // DESIGN D3, D18: never report ready while every request would be 401
    {
        using var factory = new GatewayFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<MerchantApiKeyOptions>(MerchantAuth.Scheme, options => options.Merchants = new())));

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task OpenApiDocument_IncludesTheXmlDocComments()
    {
        // Guards against the XML documentation file dropping out of the build, which would
        // silently strip every description from Swagger.
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        var document = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Process a card payment.", document);
        Assert.Contains("14 to 19 digits that pass the Luhn check", document);
    }
}
