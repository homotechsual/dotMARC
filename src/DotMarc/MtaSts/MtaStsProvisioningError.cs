using Azure;
using Azure.Identity;

namespace DotMarc.MtaSts;

/// <summary>Turns a provisioning exception into what a person needs: what went wrong in plain words, then what to do about
/// it (or that nothing needs doing because dotMARC retries). Azure's own message for a failed request carries the status,
/// error code, raw JSON body and every response header, so that is never shown; the error code is kept in brackets for
/// anyone who has to ask for help, and the full exception goes to the server log.</summary>
public static class MtaStsProvisioningError
{
    public static string Describe(Exception exception, string hostname)
    {
        switch (exception)
        {
            case CredentialUnavailableException or AuthenticationFailedException:
                return "dotMARC couldn't sign in to Azure. Turn on the Container App's system-assigned managed identity (Settings, Identity in the Azure portal), then recheck.";

            case RequestFailedException request:
                return DescribeAzureRequest(request, hostname);

            case HttpRequestException or TaskCanceledException:
                return "dotMARC couldn't reach Azure. This is usually temporary and dotMARC retries automatically. If it keeps happening, check the deployment's outbound network access to management.azure.com.";

            default:
                return exception.Message;
        }
    }

    private static string DescribeAzureRequest(RequestFailedException request, string hostname)
    {
        var code = request.ErrorCode;
        var tag = string.IsNullOrEmpty(code) ? $"(Azure status {request.Status})" : $"(Azure error {code})";

        if (request.Status is 401 or 403 || code is "AuthorizationFailed" or "AuthorizationPermissionMismatch")
        {
            return "Azure refused the request because dotMARC's managed identity isn't allowed to change this Container App. " +
                $"Assign that identity the two \"dotMARC MTA-STS\" roles the deployment template creates (one on the Container App, one on its environment), then recheck. {tag}";
        }

        if (request.Status == 404 || code is "ResourceNotFound" or "ResourceGroupNotFound" or "SubscriptionNotFound" or "ParentResourceNotFound")
        {
            return "Azure couldn't find the Container App or environment dotMARC is set up to manage. " +
                $"Check the MtaSts settings for the subscription ID, resource group, Container App and environment names. {tag}";
        }

        if (request.Status == 429 || code is "TooManyRequests")
        {
            return $"Azure is rate-limiting requests. Nothing to do: dotMARC retries automatically. {tag}";
        }

        if (request.Status >= 500)
        {
            return $"Azure had a temporary problem. Nothing to do: dotMARC retries automatically. If it persists for hours, check Azure's service health. {tag}";
        }

        if (code == "ExistingManagedCertificateOperationInProgress")
        {
            return $"Azure is already issuing a certificate for {hostname}. Nothing to do: dotMARC retries automatically. {tag}";
        }

        return $"Azure rejected the request: {Summary(request)} dotMARC retries automatically; if this doesn't clear, send the details in the server logs along with the error code. {tag}";
    }

    /// <summary>Azure's message up to where it starts listing status, error code, body and headers.</summary>
    private static string Summary(RequestFailedException request)
    {
        var message = request.Message;
        var detailsStart = message.IndexOf("Status:", StringComparison.Ordinal);
        var summary = (detailsStart > 0 ? message[..detailsStart] : message).Trim();
        if (summary.Length == 0)
        {
            summary = message.Trim();
        }

        return summary.EndsWith('.') ? summary : summary + ".";
    }
}
