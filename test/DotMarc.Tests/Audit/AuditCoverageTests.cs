using System.Reflection;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Audit;

/// <summary>A new mutating method on an audited service must take an actor, or its changes go unrecorded. This
/// fails the build's tests if one doesn't.</summary>
public sealed class AuditCoverageTests
{
    private static readonly Type[] AuditedServices =
    [
        typeof(DomainManagementService), typeof(GroupManagementService), typeof(TagManagementService),
        typeof(RoleManagementService), typeof(UserAccessManagementService), typeof(ApiKeyManagementService), typeof(AlertTicketRuleService),
        typeof(NotificationSettingsService), typeof(HaloPsaSettingsService), typeof(DotMarc.Psa.ConnectWise.ConnectWiseSettingsService), typeof(DotMarc.Psa.Autotask.AutotaskSettingsService), typeof(CloudflareDnsSettingsService),
        typeof(AzureDnsSettingsService), typeof(GoogleCloudDnsSettingsService), typeof(AuditSettingsService), typeof(DotMarc.Portal.BrandingSettingsService), typeof(DotMarc.Email.EmailSettingsService),
    ];

    private static readonly string[] ReadMethodPrefixes = ["Get", "List", "Count", "Resolve"];

    [Fact]
    public void EveryMutatingServiceMethod_TakesTheActorRightAfterTheContext()
    {
        var mutatingMethods = AuditedServices
            .SelectMany(service => service.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !ReadMethodPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        // Guards against the list above silently matching nothing.
        Assert.True(mutatingMethods.Count >= 30, $"Expected at least 30 mutating methods, found {mutatingMethods.Count}.");

        var missingActor = mutatingMethods
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length < 2
                    || parameters[0].ParameterType != typeof(DotMarcDbContext)
                    || parameters[1].ParameterType != typeof(AuditActor);
            })
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();

        Assert.True(missingActor.Count == 0,
            "These change data without taking (DotMarcDbContext context, AuditActor actor, ...): " + string.Join(", ", missingActor));
    }
}
