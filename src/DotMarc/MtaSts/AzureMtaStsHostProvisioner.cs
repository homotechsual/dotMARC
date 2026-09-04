using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotMarc.MtaSts;

/// <summary>Azure Container Apps: binds mta-sts.&lt;domain&gt; to this same Container App with a
/// free managed certificate, via the Resource Manager API rather than anything baked into the
/// Bicep template at deploy time — these bindings come and go per-domain as customers opt in and
/// out, long after deployment. Authenticates as the Container App's own system-assigned managed
/// identity (DefaultAzureCredential resolves that automatically when running in Azure); see
/// infra/main.bicep for the custom RBAC role this needs.
///
/// CNAME-based managed certificate validation requires the customer's CNAME to point directly at
/// this Container App's own generated *.azurecontainerapps.io hostname — not at any intermediate
/// hostname — so MtaSts:HostingHostname must be set to that exact value for this provisioner (see
/// deploy-to-azure.mdx).</summary>
public sealed class AzureMtaStsHostProvisioner : IMtaStsHostProvisioner
{
    private readonly MtaStsOptions _options;
    private readonly ArmClient _armClient;
    private readonly ILogger<AzureMtaStsHostProvisioner> _logger;

    public AzureMtaStsHostProvisioner(IOptions<MtaStsOptions> options, ILogger<AzureMtaStsHostProvisioner> logger)
    {
        _options = options.Value;
        _armClient = new ArmClient(new DefaultAzureCredential());
        _logger = logger;
    }

    public async Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken)
    {
        var hostname = $"mta-sts.{domainName}";
        var containerApp = await GetContainerAppAsync(cancellationToken).ConfigureAwait(false);

        var existingBinding = containerApp.Data.Configuration.Ingress.CustomDomains
            .FirstOrDefault(d => string.Equals(d.Name, hostname, StringComparison.OrdinalIgnoreCase));
        if (existingBinding is not null && existingBinding.BindingType == ContainerAppCustomDomainBindingType.SniEnabled)
        {
            // Already fully bound from an earlier cycle — nothing further to do here. Whether the
            // certificate has actually finished issuing is what the serving self-check
            // (IMtaStsServingVerifier) determines, not this provisioner.
            return;
        }

        if (existingBinding is null)
        {
            // Azure requires the hostname already registered as a custom domain on the container
            // app before it will create a managed certificate for it
            // (RequireCustomHostnameInEnvironment) — so this binds it first with no certificate,
            // then creates the certificate below, then rebinds with the certificate attached. A
            // crash between these two steps leaves the binding Disabled with no certificate; the
            // existingBinding check above only short-circuits once it's fully SniEnabled, so the
            // next cycle resumes from certificate creation instead of re-adding the binding or
            // giving up on it.
            containerApp.Data.Configuration.Ingress.CustomDomains.Add(new ContainerAppCustomDomain(hostname) { BindingType = ContainerAppCustomDomainBindingType.Disabled });
            try
            {
                containerApp = (await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false)).Value;
            }
            catch (RequestFailedException ex) when (string.Equals(ex.ErrorCode, "InvalidCustomHostNameValidation", StringComparison.Ordinal))
            {
                // Azure validates hostname ownership at exactly this call — this is the ARM error
                // the user hits when the asuid.<hostname> TXT record isn't in place yet. Replace
                // the raw error with the specific fix, using the same verification ID
                // GetDomainVerificationIdAsync below would return (already loaded on this same
                // containerApp.Data, so no extra ARM call needed).
                var verificationId = containerApp.Data.CustomDomainVerificationId;
                throw new InvalidOperationException(
                    $"Missing ownership verification record — add asuid.{hostname} TXT {verificationId} to DNS; this retries automatically.", ex);
            }
        }

        var certificateId = await EnsureManagedCertificateAsync(hostname, cancellationToken).ConfigureAwait(false);

        var binding = containerApp.Data.Configuration.Ingress.CustomDomains
            .First(d => string.Equals(d.Name, hostname, StringComparison.OrdinalIgnoreCase));
        binding.CertificateId = certificateId;
        binding.BindingType = ContainerAppCustomDomainBindingType.SniEnabled;
        await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false);
    }

    public async Task TeardownAsync(string domainName, CancellationToken cancellationToken)
    {
        var hostname = $"mta-sts.{domainName}";
        var containerApp = await GetContainerAppAsync(cancellationToken).ConfigureAwait(false);

        var binding = containerApp.Data.Configuration.Ingress.CustomDomains
            .FirstOrDefault(d => string.Equals(d.Name, hostname, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
        {
            return;
        }

        containerApp.Data.Configuration.Ingress.CustomDomains.Remove(binding);
        await containerApp.UpdateAsync(WaitUntil.Completed, containerApp.Data, cancellationToken).ConfigureAwait(false);

        // The managed certificate itself is left in place rather than deleted here: Azure ties
        // managed-certificate issuance to DNS validation succeeding at creation time, and
        // recreating it on a later re-enable would mean waiting on that validation again for no
        // benefit — an orphaned, unbound certificate costs nothing to leave behind.
    }

    /// <summary>The Container App's own customDomainVerificationId — fixed for the life of the
    /// resource, so the same value is correct for every domain this deployment hosts. Not cached:
    /// this is only called from a page load or a DNS push callback, both human-triggered and
    /// infrequent, never from PollingService's poll loop.</summary>
    public async Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var containerApp = await GetContainerAppAsync(cancellationToken).ConfigureAwait(false);
            return containerApp.Data.CustomDomainVerificationId;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to fetch this deployment's Azure Container Apps domain verification ID.");
            return null;
        }
    }

    private async Task<ResourceIdentifier> EnsureManagedCertificateAsync(string hostname, CancellationToken cancellationToken)
    {
        var environmentId = ContainerAppManagedEnvironmentResource.CreateResourceIdentifier(
            RequireOption(_options.AzureSubscriptionId, nameof(MtaStsOptions.AzureSubscriptionId)),
            RequireOption(_options.AzureResourceGroupName, nameof(MtaStsOptions.AzureResourceGroupName)),
            RequireOption(_options.AzureManagedEnvironmentName, nameof(MtaStsOptions.AzureManagedEnvironmentName)));
        var environment = await _armClient.GetContainerAppManagedEnvironmentResource(environmentId).GetAsync(cancellationToken).ConfigureAwait(false);

        // Certificate names have their own naming restrictions (alphanumeric and hyphens); the
        // hostname itself contains dots, so it can't be reused directly as the resource name.
        var certificateName = $"mta-sts-{hostname.Replace('.', '-')}";
        var certificates = environment.Value.GetContainerAppManagedCertificates();

        var existing = await TryGetCertificateAsync(certificates, certificateName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var certificateData = new ContainerAppManagedCertificateData(environment.Value.Data.Location)
        {
            Properties = new ManagedCertificateProperties
            {
                SubjectName = hostname,
                DomainControlValidation = ManagedCertificateDomainControlValidation.Cname
            }
        };

        var created = await certificates.CreateOrUpdateAsync(WaitUntil.Completed, certificateName, certificateData, cancellationToken).ConfigureAwait(false);
        return created.Value.Id;
    }

    private static async Task<ContainerAppManagedCertificateResource?> TryGetCertificateAsync(
        ContainerAppManagedCertificateCollection certificates, string certificateName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await certificates.GetAsync(certificateName, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task<ContainerAppResource> GetContainerAppAsync(CancellationToken cancellationToken)
    {
        var containerAppId = ContainerAppResource.CreateResourceIdentifier(
            RequireOption(_options.AzureSubscriptionId, nameof(MtaStsOptions.AzureSubscriptionId)),
            RequireOption(_options.AzureResourceGroupName, nameof(MtaStsOptions.AzureResourceGroupName)),
            RequireOption(_options.AzureContainerAppName, nameof(MtaStsOptions.AzureContainerAppName)));
        var response = await _armClient.GetContainerAppResource(containerAppId).GetAsync(cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    private static string RequireOption(string? value, string optionName) =>
        !string.IsNullOrEmpty(value) ? value : throw new InvalidOperationException($"MtaSts:{optionName} must be set when MtaSts:Provisioner is \"Azure\".");
}
