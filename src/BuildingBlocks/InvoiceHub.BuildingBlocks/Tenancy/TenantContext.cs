namespace InvoiceHub.BuildingBlocks.Tenancy;

/// <summary>
/// The tenant the current unit of work runs for (principle P6: from the token in the API, from the delivery row in workers).
/// It is copied into SQL <c>SESSION_CONTEXT</c> on every connection open, where row-level security enforces it a second time.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    /// <summary>Cross-tenant system mode. Only honoured by SQL for logins in the worker role; the API login can never use it.</summary>
    bool IsSystem { get; }
}

public interface ITenantScope
{
    void SetTenant(Guid tenantId);

    void SetSystem();

    /// <summary>Runs for another tenant until disposed, then restores the previous context.</summary>
    IDisposable Use(Guid tenantId);
}

public sealed class TenantContext : ITenantContext, ITenantScope
{
    public Guid? TenantId { get; private set; }

    public bool IsSystem { get; private set; }

    public void SetTenant(Guid tenantId)
    {
        TenantId = tenantId;
        IsSystem = false;
    }

    public void SetSystem()
    {
        TenantId = null;
        IsSystem = true;
    }

    public IDisposable Use(Guid tenantId)
    {
        var (previousTenant, previousSystem) = (TenantId, IsSystem);
        SetTenant(tenantId);
        return new Restore(this, previousTenant, previousSystem);
    }

    private sealed class Restore(TenantContext owner, Guid? tenant, bool system) : IDisposable
    {
        public void Dispose()
        {
            owner.TenantId = tenant;
            owner.IsSystem = system;
        }
    }
}
