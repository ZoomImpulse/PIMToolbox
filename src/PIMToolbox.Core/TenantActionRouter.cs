namespace PIMToolbox.Core;

public sealed class TenantActionRouter(
    IEnumerable<TenantConfiguration> tenants,
    Func<TenantConfiguration, CancellationToken, Task<string>> tokenProvider,
    GraphPimClient graphClient)
{
    private readonly IReadOnlyDictionary<string, TenantConfiguration> _tenants =
        tenants.ToDictionary(tenant => tenant.TenantId, StringComparer.OrdinalIgnoreCase);

    public async Task<PimActionResult> ActivateAsync(
        PimAssignment assignment,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken = default)
    {
        var tenant = ResolveTenant(assignment);
        var token = await tokenProvider(tenant, cancellationToken).ConfigureAwait(false);
        return await graphClient.ActivateAsync(tenant, assignment, token, duration, justification, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PimActionResult> DeactivateAsync(PimAssignment assignment, CancellationToken cancellationToken = default)
    {
        var tenant = ResolveTenant(assignment);
        var token = await tokenProvider(tenant, cancellationToken).ConfigureAwait(false);
        return await graphClient.DeactivateAsync(tenant, assignment, token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TimeSpan?> GetMaximumDurationAsync(
        PimAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        var tenant = ResolveTenant(assignment);
        var token = await tokenProvider(tenant, cancellationToken).ConfigureAwait(false);
        return await graphClient.GetMaximumActivationDurationAsync(tenant, assignment, token, cancellationToken)
            .ConfigureAwait(false);
    }

    private TenantConfiguration ResolveTenant(PimAssignment assignment)
    {
        if (!_tenants.TryGetValue(assignment.TenantId, out var tenant))
            throw new InvalidOperationException(
                $"No tenant configuration exists for assignment tenant '{assignment.TenantId}'.");
        return tenant;
    }
}
