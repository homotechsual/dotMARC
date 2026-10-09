using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting.ClientReports;

public sealed record ManualSendResult(bool Sent, string Message);

/// <summary>On-demand reports from the Reports dialog: send now (recorded as a Manual delivery and audited) and
/// download. Neither affects the schedule.</summary>
public sealed class ClientReportRunner(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ClientReportBuilder builder,
    IEmailSenderFactory senderFactory,
    IAlertingService alerting,
    TimeProvider timeProvider)
{
    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return ReportSettingsService.ResolveZone((await ReportSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false)).TimeZoneId);
    }

    public async Task<(byte[] Pdf, string FileName)?> RenderAsync(int groupId, ReportPeriod period, CancellationToken cancellationToken)
    {
        var report = await builder.BuildAsync(groupId, period, await ZoneAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return report is null ? null : (ClientReportDocument.Render(report), ClientReportDocument.FileName(report));
    }

    public async Task<ManualSendResult> SendNowAsync(AuditActor actor, int groupId, ReportPeriod period, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        // The dialog awaits this directly, so an unexpected error (the database unreachable, say) must come back as a
        // message rather than end the user's session.
        try
        {
            return await SendNowUncheckedAsync(actor, groupId, period, recipients, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ManualSendResult(false, $"Couldn't send the report: {exception.Message}");
        }
    }

    private async Task<ManualSendResult> SendNowUncheckedAsync(AuditActor actor, int groupId, ReportPeriod period, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        List<string> tidied;
        try
        {
            tidied = ReportRecipients.Normalise(recipients);
        }
        catch (ArgumentException exception)
        {
            return new ManualSendResult(false, exception.Message.Split(" (Parameter")[0]);
        }

        if (tidied.Count == 0)
        {
            return new ManualSendResult(false, "Add at least one recipient.");
        }

        var sender = await senderFactory.GetAsync(cancellationToken).ConfigureAwait(false);
        if (sender is null)
        {
            return new ManualSendResult(false, "Email is off. Set it up on Email & reports first.");
        }

        var report = await builder.BuildAsync(groupId, period, await ZoneAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            return new ManualSendResult(false, "That Group no longer exists.");
        }

        var now = timeProvider.GetUtcNow();
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await context.Groups.SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
        var delivery = new ClientReportDelivery
        {
            GroupId = groupId, PeriodStart = period.Start, PeriodEnd = period.End, Kind = ClientReportDeliveryKind.Manual, Recipients = tidied,
            RequestedBy = actor.Email, Attempts = 1, FirstAttemptUtc = now, LastAttemptUtc = now,
        };
        context.ClientReportDeliveries.Add(delivery);
        try
        {
            await sender.SendAsync(ClientReportEmail.Compose(report, ClientReportDocument.Render(report), tidied), cancellationToken).ConfigureAwait(false);
            delivery.Status = ClientReportDeliveryStatus.Sent;
            delivery.SentUtc = now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            delivery.Status = ClientReportDeliveryStatus.Failed;
            delivery.Error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
        }

        if (delivery.Status == ClientReportDeliveryStatus.Sent)
        {
            AuditLog.Record(context, actor, AuditActions.ClientReportSent, AuditTarget.For(group),
                $"Sent the {period.Label} report for group {group.Name} to {tidied.Count} recipient{(tidied.Count == 1 ? "" : "s")}");
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (delivery.Status == ClientReportDeliveryStatus.Sent)
        {
            await alerting.ResolveClientReportFailedAsync(groupId, cancellationToken).ConfigureAwait(false);
            return new ManualSendResult(true, $"Sent the {period.Label} report to {tidied.Count} recipient{(tidied.Count == 1 ? "" : "s")}.");
        }

        return new ManualSendResult(false, $"Couldn't send the report: {delivery.Error}");
    }
}
