using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Config;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests;

public class AcquiringBankClientTests
{
    private static readonly BankAuthorizationRequest Request = new("2222405343248877", 4, 2025, "GBP", 100, "123");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(request, cancellationToken);
        }
    }

    private static (AcquiringBankClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new BankClientOptions
        {
            BaseUrl = new Uri("http://bank.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        });
        return (new AcquiringBankClient(new HttpClient(handler), options, NullLogger<AcquiringBankClient>.Instance), handler);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string? json = null) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json")
        });

    [Fact]
    public async Task SendsTheBankFormat()
    {
        var (client, handler) = Create(Respond(HttpStatusCode.OK, """{"authorized":true,"authorization_code":"abc"}"""));

        await client.AuthorizeAsync(Request);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://bank.test/payments", handler.LastRequest.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("2222405343248877", root.GetProperty("card_number").GetString());
        Assert.Equal("04/2025", root.GetProperty("expiry_date").GetString());
        Assert.Equal("GBP", root.GetProperty("currency").GetString());
        Assert.Equal(100, root.GetProperty("amount").GetInt64());
        Assert.Equal("123", root.GetProperty("cvv").GetString());
    }

    [Theory]
    [InlineData(1, 2030, "01/2030")]
    [InlineData(9, 2030, "09/2030")]
    [InlineData(10, 2030, "10/2030")]
    [InlineData(12, 2030, "12/2030")]
    public void ExpiryDate_IsZeroPaddedMonthSlashYear(int month, int year, string expected)
    {
        var mapped = AcquiringBankClient.ToBankRequest(Request with { ExpiryMonth = month, ExpiryYear = year });

        Assert.Equal(expected, mapped.ExpiryDate);
    }

    [Fact]
    public async Task AuthorizedTrue_IsAuthorized()
    {
        var (client, _) = Create(Respond(HttpStatusCode.OK, """{"authorized":true,"authorization_code":"abc"}"""));

        Assert.Equal(new BankAuthorizationResult(BankResult.Authorized, "abc"), await client.AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData("""{"authorized":false,"authorization_code":""}""")] // what the simulator sends
    [InlineData("""{"authorized":false}""")]
    [InlineData("""{"authorized":true,"authorization_code":"   "}""")]
    public async Task MissingOrBlankAuthorizationCode_IsNull(string json)
    {
        var (client, _) = Create(Respond(HttpStatusCode.OK, json));

        Assert.Null((await client.AuthorizeAsync(Request)).AuthorizationCode);
    }

    [Fact]
    public async Task AuthorizationCode_IsDropped_WhenTheOutcomeIsUnknown()
    {
        var (client, _) = Create(Respond(HttpStatusCode.OK, """{"authorization_code":"abc"}"""));

        Assert.Equal(new BankAuthorizationResult(BankResult.Unknown), await client.AuthorizeAsync(Request));
    }

    [Fact]
    public async Task AuthorizedFalse_IsDeclined()
    {
        var (client, _) = Create(Respond(HttpStatusCode.OK, """{"authorized":false,"authorization_code":""}"""));

        Assert.Equal(BankResult.Declined, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ServiceUnavailableOrBadRequest_IsNotProcessed(HttpStatusCode status)
    {
        var (client, _) = Create(Respond(status, "{}"));

        Assert.Equal(BankResult.NotProcessed, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task OtherStatus_IsUnknown(HttpStatusCode status)
    {
        var (client, _) = Create(Respond(status, "{}"));

        Assert.Equal(BankResult.Unknown, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"authorized":"yes"}""")]
    public async Task UnreadableOkBody_IsUnknown(string json)
    {
        var (client, _) = Create(Respond(HttpStatusCode.OK, json));

        Assert.Equal(BankResult.Unknown, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Fact]
    public async Task NetworkFailure_IsUnknown()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("connection reset"));

        Assert.Equal(BankResult.Unknown, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Fact]
    public async Task Timeout_IsTimedOut()
    {
        var (client, _) = Create(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(BankResult.TimedOut, (await client.AuthorizeAsync(Request)).Outcome);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
    {
        var (client, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AuthorizeAsync(Request, cts.Token));
    }
}
