using Microsoft.AspNetCore.Components.Authorization;

namespace DotMarc.Audit;

/// <summary>Gives a page the actor for the person using it, from the circuit's authentication state.</summary>
public sealed class AuditActorAccessor(AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<AuditActor> GetAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return AuditActor.FromPrincipal(state.User);
    }
}
