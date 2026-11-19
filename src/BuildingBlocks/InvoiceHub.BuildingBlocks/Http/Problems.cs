using Microsoft.AspNetCore.Http;

namespace InvoiceHub.BuildingBlocks.Http;

/// <summary>RFC 9457 problem responses with a stable machine-readable <c>code</c> the portal can switch on.</summary>
public static class Problems
{
    public static IResult Create(int status, string code, string title, string? detail = null) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            type: $"https://invoicehub.dev/problems/{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Validation(string code, string detail) => Create(StatusCodes.Status400BadRequest, code, "Validation failed", detail);

    public static IResult Conflict(string code, string detail) => Create(StatusCodes.Status409Conflict, code, "Conflict", detail);

    public static IResult Forbidden(string code, string detail) => Create(StatusCodes.Status403Forbidden, code, "Forbidden", detail);

    public static IResult NotFound(string code, string detail) => Create(StatusCodes.Status404NotFound, code, "Not found", detail);

    /// <summary>Writes a problem to the response from middleware (where an IResult cannot simply be returned).</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string title, string? detail = null) =>
        Create(status, code, title, detail).ExecuteAsync(context);
}
