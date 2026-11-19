namespace InvoiceHub.BuildingBlocks.Http;

public static class QueryTimes
{
    /// <summary>
    /// A time from a query string, as UTC. ASP.NET turns "...Z" into LOCAL time when it binds a DateTime; the database holds UTC, so comparing
    /// it as it is would shift every range by the server's offset. A time without a zone is taken as UTC.
    /// </summary>
    public static DateTime? AsUtc(this DateTime? value) =>
        value is null ? null : value.Value.Kind == DateTimeKind.Local ? value.Value.ToUniversalTime() : value;
}
