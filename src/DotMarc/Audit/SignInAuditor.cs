using System.Security.Claims;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Records a sign-in once, where it happens (the Entra token-validated event, or the demo sign-in), not on
/// every request. Whether it counts as refused depends on the person having an access grant, the same lookup the
/// claims transformation makes.</summary>
public sealed class SignInAuditor(IDbContextFactory<DotMarcDbContext> dbContextFactory, AuditRecorder recorder, ILogger<SignInAuditor> logger)
{
    public async Task RecordAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var actor = AuditActor.FromPrincipal(principal);
        bool hasAccess;
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            hasAccess = await UserAccessManagementService.ResolveAsync(context, actor.ObjectId, actor.Email, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception exception)
        {
            // Best-effort: recording a sign-in must never stop one.
            logger.LogWarning(exception, "Couldn't check {ActorName}'s access to record their sign-in", actor.Name);
            return;
        }

        var entry = hasAccess
            ? AuditLog.Create(actor, AuditEntryKind.SignIn, AuditActions.SignInSucceeded, null, $"{actor.Name} signed in")
            : AuditLog.Create(actor, AuditEntryKind.SignIn, AuditActions.SignInRefused, null, $"{actor.Name} tried to sign in without an access grant");
        await recorder.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}
