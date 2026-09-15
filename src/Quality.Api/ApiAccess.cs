using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
namespace Quality.Api;

public sealed class ApiAccess
{
    private readonly byte[]? keyHash;

    public ApiAccess(IConfiguration configuration)
    {
        var key = configuration["Quality:Api:Key"];
        var anonymous = configuration.GetValue<bool>("Quality:Api:AllowAnonymous");
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length is < 32 or > 256 || key.Any(c => c < 33 || c > 126))
                throw new ArgumentException("Quality__Api__Key must contain 32–256 printable ASCII characters without spaces");
            keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }
        else if (!anonymous)
            throw new ArgumentException("Set Quality__Api__Key, or explicitly enable Quality__Api__AllowAnonymous for local development");
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (keyHash is null || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        var headers = context.Request.Headers.Authorization;
        var value = headers.Count == 1 ? headers[0] : null;
        if (value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = value[7..];
            if (token.Length is >= 32 and <= 256 && token.All(c => c >= 33 && c <= 126) &&
                CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(token))))
            {
                await next(context);
                return;
            }
        }
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, context.RequestAborted);
    }
}
