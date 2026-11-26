using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Brightline.LegacyOrders.Client;

/// <summary>Typed client for the existing Orders & Returns API. The only way the app reaches that system.</summary>
public interface ILegacyOrdersClient
{
    /// <summary>Returns the customer if the order number and email match the same order; otherwise null.</summary>
    Task<VerifiedCustomer?> VerifyCustomerAsync(string orderNumber, string email, CancellationToken ct = default);

    Task<IReadOnlyList<OrderSummary>> ListOrdersAsync(int customerId, CancellationToken ct = default);

    /// <summary>Returns null when the order does not exist or does not belong to the customer.</summary>
    Task<OrderDetail?> GetOrderAsync(int customerId, string orderNumber, CancellationToken ct = default);

    Task<CreateReturnResult> CreateReturnAsync(int customerId, NewReturn request, string idempotencyKey, CancellationToken ct = default);

    Task<IReadOnlyList<ReturnInfo>> ListReturnsAsync(int customerId, CancellationToken ct = default);
}

public sealed class LegacyOrdersClient(HttpClient http, ILogger<LegacyOrdersClient> logger) : ILegacyOrdersClient
{
    public const string IdempotencyHeader = "Idempotency-Key";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<VerifiedCustomer?> VerifyCustomerAsync(string orderNumber, string email, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await http.PostAsJsonAsync(
                "api/verifications", new { orderNumber, email }, Json, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            await EnsureSuccessAsync(response, ct);
            return await response.Content.ReadFromJsonAsync<VerifiedCustomer>(Json, ct);
        });

    public Task<IReadOnlyList<OrderSummary>> ListOrdersAsync(int customerId, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await http.GetAsync($"api/customers/{customerId}/orders", ct);
            await EnsureSuccessAsync(response, ct);
            return (IReadOnlyList<OrderSummary>)(await response.Content.ReadFromJsonAsync<List<OrderSummary>>(Json, ct) ?? []);
        });

    public Task<OrderDetail?> GetOrderAsync(int customerId, string orderNumber, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await http.GetAsync(
                $"api/customers/{customerId}/orders/{Uri.EscapeDataString(orderNumber)}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, ct);
            return await response.Content.ReadFromJsonAsync<OrderDetail>(Json, ct);
        });

    public Task<CreateReturnResult> CreateReturnAsync(int customerId, NewReturn request, string idempotencyKey, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"api/customers/{customerId}/returns")
            {
                Content = JsonContent.Create(request, options: Json),
            };
            message.Headers.Add(IdempotencyHeader, idempotencyKey);

            using var response = await http.SendAsync(message, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            {
                var problem = await ReadProblemTitleAsync(response, ct);
                throw new LegacyOrdersRejectedException(problem ?? "The order system rejected the return request.");
            }

            await EnsureSuccessAsync(response, ct);
            var created = await response.Content.ReadFromJsonAsync<ReturnInfo>(Json, ct)
                ?? throw new LegacyOrdersUnavailableException("Empty response from the order system.");
            return new CreateReturnResult(created, response.StatusCode == HttpStatusCode.Created);
        });

    public Task<IReadOnlyList<ReturnInfo>> ListReturnsAsync(int customerId, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await http.GetAsync($"api/customers/{customerId}/returns", ct);
            await EnsureSuccessAsync(response, ct);
            return (IReadOnlyList<ReturnInfo>)(await response.Content.ReadFromJsonAsync<List<ReturnInfo>>(Json, ct) ?? []);
        });

    private async Task<T> SendAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (BrokenCircuitException ex)
        {
            logger.LogWarning("Legacy Orders API circuit is open");
            throw new LegacyOrdersUnavailableException("The order system is temporarily unavailable (circuit open).", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            logger.LogWarning("Legacy Orders API timed out");
            throw new LegacyOrdersUnavailableException("The order system did not respond in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Legacy Orders API request failed: {StatusCode}", ex.StatusCode);
            throw new LegacyOrdersUnavailableException("The order system could not be reached.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var title = await ReadProblemTitleAsync(response, ct);
        throw new HttpRequestException(
            $"Legacy Orders API returned {(int)response.StatusCode}: {title}", inner: null, response.StatusCode);
    }

    private static async Task<string?> ReadProblemTitleAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The legacy system could not be reached (after retries) or the circuit is open.</summary>
public sealed class LegacyOrdersUnavailableException : Exception
{
    public LegacyOrdersUnavailableException()
    {
    }

    public LegacyOrdersUnavailableException(string message)
        : base(message)
    {
    }

    public LegacyOrdersUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The legacy system refused a write because of its own business rules (e.g. quantity already returned).</summary>
public sealed class LegacyOrdersRejectedException : Exception
{
    public LegacyOrdersRejectedException()
    {
    }

    public LegacyOrdersRejectedException(string message)
        : base(message)
    {
    }

    public LegacyOrdersRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
