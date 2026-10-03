using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Service;

/// <summary>
/// Guards mutating endpoints with a shared secret. When no key is configured the endpoints are
/// refused outright rather than left open, so a default deployment cannot be rewritten anonymously.
/// </summary>
public sealed class ApiKeyEndpointFilter(IOptions<SchedulerServiceOptions> options) : IEndpointFilter
{
    private const string HeaderName = "X-Api-Key";

    private readonly SchedulerServiceOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Results.Problem(
                "This endpoint is disabled because Scheduler:ApiKey is not configured.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        string? provided = context.HttpContext.Request.Headers[HeaderName];
        if (provided is null || !FixedTimeEquals(provided, _options.ApiKey))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
