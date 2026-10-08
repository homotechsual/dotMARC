# Scheduled client reports design

**Release:** dotMARC 0.9.0
**Status:** approved in brainstorming, awaiting spec review

## Purpose

MSPs hand each client a regular summary of their email authentication posture, but dotMARC is a dashboard you have to open, so there is nothing to send. This adds branded client reports: a PDF per Group covering a week, month, quarter or custom range, sent by email on a schedule or on demand, in the same plain language and brand as the client portal.

## Decisions made in brainstorming

- **Email by Microsoft Graph or SMTP**, chosen in settings. Graph reuses the existing app registration with `Mail.Send` added; SMTP works with any provider.
- **Per Group.** One report per Group covering all its domains, sent to that Group's recipient list. No per-domain reports or free-form subscriptions.
- **Explicit recipients.** Recipients are typed addresses only; portal users are not added automatically. The recipient field suggests the people who already hold a grant scoped to that Group.
- **Weekly, monthly or quarterly per Group**, or Off. Plus ad-hoc **Send now** and **Download PDF** for any past week, month, quarter or custom range, whether or not a schedule is on.
- **Full content:** cover, summary table, trend, per-domain detail and a "what to do next" list.
- **Approach A: PDFsharp and MigraDoc** (MIT), laid out in C#, with a bundled Roboto font. Rejected: HTML rendered by headless Chromium (adds 300 to 400 MB and a browser process to every self-hosted image) and QuestPDF (its free licence stops at $1M revenue, which would oblige larger MSPs to buy a licence just to run dotMARC).
- **One report time zone and send hour for the install**, defaulting to UTC and 06:00.

## Scope

**In:** email settings and sending (Graph and SMTP) with a test send; report settings (time zone, send hour); per-Group schedules and recipients; report generation as PDF; the email body; the scheduler with retries; ad-hoc send and download; delivery history; a "Client report failed" alert type; audit; a `ReportsManage` permission; demo behaviour; docs and roadmap.

**Out for 0.9.0:** per-domain reports and free-form subscriptions; per-Group time zones; automatically including portal users; reports in formats other than PDF; an API for reports; using the new email sender for alerts (it is built to allow that later).

## 1. Email delivery

### `EmailSettings` (singleton, seeded `Id = 1`)

| Column | Notes |
| --- | --- |
| Provider | `Off` (default), `Graph`, `Smtp` |
| FromAddress | required unless Off; validated as an email, max 254 |
| FromName | nullable, max 100; when empty the MSP brand's product name is used |
| GraphSenderMailbox | nullable, max 254; the mailbox Graph sends as; defaults to the configured reports mailbox when empty |
| SmtpHost | required for SMTP, max 253 |
| SmtpPort | 1 to 65535, default 587 |
| SmtpSecurity | `StartTls` (default), `SslOnConnect`, `None` |
| SmtpUsername | nullable, max 254 |
| SmtpPasswordConfigured | bool; the password itself lives in `ISecretStore` under `Email.SmtpPassword` |

Saved through `EmailSettingsService.SaveAsync(context, actor, updated, newSmtpPassword, ct)`, audited as `settings.email.saved` (the password recorded only as changed or not).

### Senders

- `IEmailSender.SendAsync(EmailMessage message, CancellationToken ct)` where `EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string TextBody, IReadOnlyList<EmailAttachment> Attachments)` and `EmailAttachment(string FileName, string ContentType, byte[] Bytes)`. Throws `EmailSendException` with the provider's message on failure.
- `EmailSenderFactory` picks the implementation from `EmailSettings.Provider`; `Off` yields none.
- `GraphEmailSender` posts `users/{mailbox}/sendMail` with the attachment inline (Graph accepts attachments up to 3 MB this way), using the existing `IGraphTokenProvider`.
- `SmtpEmailSender` uses MailKit (MIT).
- Each message goes to all recipients in **To** in one send. A provider error fails the whole send.
- A send is refused before contacting the provider when there are no recipients, more than 25, or the attachments total over 3 MB (a report is typically well under 1 MB).

### Test send

`EmailTestService.SendTestAsync(settings, to, ct)` sends a short plain message and returns the provider's error text on failure, for the settings page.

## 2. Report settings and schedules

### `ReportSettings` (singleton, seeded `Id = 1`)

| Column | Notes |
| --- | --- |
| TimeZoneId | IANA id, default `UTC`; validated with `TimeZoneInfo.FindSystemTimeZoneById` |
| SendHour | 0 to 23, default 6 |

Saved through `ReportSettingsService.SaveAsync(context, actor, updated, ct)`, audited as `settings.reports.saved`. The settings page shares one screen with email settings: **Manage > Email & reports**.

### `GroupReportSchedule` (optional, one per Group)

| Column | Notes |
| --- | --- |
| GroupId | key and FK to Group, cascade delete |
| Frequency | `Off`, `Weekly`, `Monthly`, `Quarterly` |
| Recipients | Postgres `text[]`, each a valid email, 0 to 25, stored lower-cased and de-duplicated |

Saved through `ClientReportService.SetScheduleAsync(context, actor, groupId, frequency, recipients, ct)`, audited as `group.report_schedule_changed`. A schedule other than Off needs at least one recipient. Clearing to Off with no recipients removes the row.

### `ClientReportDelivery`

| Column | Notes |
| --- | --- |
| Id | |
| GroupId | FK to Group, cascade delete |
| PeriodStart, PeriodEnd | `DateOnly`, inclusive start and end days in the report time zone |
| Kind | `Scheduled`, `Manual` |
| Recipients | `text[]` |
| RequestedBy | nullable actor email, for manual sends |
| Status | `Pending`, `Sent`, `Failed`, `Skipped` |
| Attempts | int |
| LastAttemptUtc, SentUtc | nullable |
| Error | nullable, max 1000; also the skip reason |

A unique index on `(GroupId, PeriodStart, PeriodEnd)` filtered to `Kind = Scheduled` makes a scheduled period deliverable once. Manual sends never block or satisfy a scheduled one.

### Permission and scope

A new `Permission.ReportsManage` gates the settings page, the Reports dialog, send now and download. A staff member limited to some Groups can manage reports only for those Groups (the same rule as `PortalScope.CanPreview`). The locked Admin role gets it automatically, since `AccessBootstrapper` resyncs it to every permission at startup; Viewer doesn't, and custom roles can opt in on the Access page.

## 3. Periods

`ReportPeriod` is pure logic over a `TimeZoneInfo`:

- `Previous(frequency, zone, nowUtc)`: the last complete period. Weeks run Monday to Sunday; months are calendar months; quarters start in January, April, July and October. Boundaries are local midnight.
- `DueUtc(period, zone, sendHour)`: the instant after the period ends at which it may be sent, the local `sendHour` on the first day after the period. For a local time that doesn't exist (a daylight saving gap), the first valid instant after it.
- `Custom(startDay, endDay)`: validated to be in the past, start before or on end, at most 366 days.
- `Label`: "Week of 2 March 2026", "March 2026", "Q1 2026", or "1 January to 30 June 2026" for a custom range.
- `PreviousEqualLength`: the period of the same length immediately before, for the change column.
- A report counts the aggregate reports whose `DateRangeBeginUtc`, converted to the report time zone, falls on a day within the period.

## 4. Report contents

`ClientReportBuilder.BuildAsync(groupId, period, ct)` returns a `ClientReport` model; `ClientReportPdf.Render(model) : byte[]`; `ClientReportEmail.Build(model) : (Subject, Html, Text)`.

### Brand

The Group's resolved brand from `PortalBrandLoader` (logo, colours, display name, product name, support details). MigraDoc can't draw SVG, so the PDF uses the light logo only when it is PNG or JPEG; an SVG or missing logo falls back to the product name as text. The Branding page and Group dialog say so beside the logo fields. The email body references no images (see Email body).

### Cover

Logo or product name, the Group's heading, "Email security report", the period label with exact dates, and the portal's verdict sentence (`PortalStatus.Verdict`) from each domain's status at generation time.

### Summary table

One row per domain in the Group, ordered by name: status (Protected, Monitoring only, Needs attention, No reports yet, coloured as in the portal), messages in the period, DMARC pass rate (`DomainStatistics.GetPassRate` definition), and the change from the previous equal-length period in percentage points ("+3.2 pts", "-1.0 pts", or "new" when the previous period had no messages).

### Trend

A line chart of daily pass rate per domain over the period (weekly points when the period is over 92 days), drawn with MigraDoc/PDFsharp graphics, gaps where there is no data, at most 6 domains charted (by message volume) and the rest named beneath.

### Per domain

- **Policy:** the portal's policy sentence.
- **Health:** the portal's health checks with their labels.
- **Top senders:** the top 10 source IPs by messages in the period: IP, owner name from IP enrichment when known, messages, passing, failing, share of the domain's mail.
- **What receivers did:** delivered, quarantined and rejected message counts and percentages, and how many failing messages were delivered anyway.
- **Alerts:** alerts for the domain raised or resolved within the period, with dates.

### What to do next

Built from fixed rules with fixed wording, in this order, one line each:

1. Policy `none`: "Move {domain} from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam."
2. Policy `quarantine` with health otherwise passing: "When you're ready, move {domain} to a reject policy."
3. Each failing health check: a fixed sentence per check, such as "Publish an SPF record for {domain}." or "DKIM isn't set up for {domain}. Set it up with each service that sends as it."
4. A sender with an owner name failing DMARC for more than 5% of a domain's messages in the period: "{owner} sent {n} messages as {domain} that failed DMARC. If they send for you, add them to SPF or set up DKIM for them."
5. No reports for a domain in the period: "No DMARC reports arrived for {domain}. Check its DMARC record's reporting address."

When nothing applies: "Nothing to do. Every domain is protected."

### Edge cases

A Group with no domains renders a one-page report saying so. All text from data (names, owners, alert titles) is treated as text, never markup.

### Email body

Subject "{heading} email security report: {period label}". A short HTML body in the brand's primary colour with the verdict, the summary table, the support details, and "The full report is attached." A plain-text alternative carries the same. No remote images, so nothing is blocked or tracked. The PDF is attached as "{heading} email security report {period}.pdf" (file name characters made safe).

## 5. Sending

### Scheduler

`ClientReportScheduler`, a hosted service, runs every 15 minutes (and once at startup after a short delay):

1. Load `ReportSettings` and every schedule with a frequency other than Off.
2. For each Group separately: work out `Previous(frequency, zone, now)`. If `now` is before `DueUtc`, skip. Otherwise find the scheduled delivery row for that period, creating it as `Pending` if missing.
3. If it is `Sent`, `Skipped` or `Failed` (given up), skip. If `Pending` and the last attempt was less than an hour ago, skip.
4. If email is Off, mark it `Skipped` ("Email is off") and move on, with no alert. If the Group has no domains, mark it `Skipped` ("No domains").
5. Build, render and send to the schedule's recipients. On success mark `Sent` and resolve any open "Client report failed" alert for the Group. On failure record the error and attempt count; after the attempts span 24 hours, mark `Failed` and raise the alert.
6. A failure in one Group is caught, logged and recorded; the loop carries on with the next.

The scheduler uses `TimeProvider` so tests can drive the clock.

### Alert

A new alert type `ClientReportFailed` ("Client report failed"), keyed by Group, raised through the existing `AlertingService` (respecting its channels, cooldown and PSA ticket rules), with the Group name, period and last error. It resolves automatically on the Group's next successful scheduled or manual send.

### Manual send and download

`ClientReportService.SendNowAsync(context, actor, groupId, period, recipients, ct)` builds, renders and sends at once, records a `Manual` delivery (Sent or Failed with the error), audits `group.report_sent` (Group, period, recipient count), and returns the outcome for the dialog. `ClientReportService.RenderAsync(groupId, period, ct)` returns the PDF for download, served by `GET /reports/groups/{groupId}/pdf?start=yyyy-MM-dd&end=yyyy-MM-dd` (authorized with `ReportsManage` and Group scope), with `Content-Disposition: attachment`.

## 6. Screens

### Manage > Email & reports (`/reports/settings`, `ReportsManage`)

Email: provider, from address and name, Graph mailbox, SMTP host, port, security, username and write-only password, each with `FieldWithHelp`; **Send test email** to a typed address, showing success or the provider's error. Reports: time zone (searchable list of IANA zones) and send hour. A docs link to Client reports.

### Reports dialog (Manage groups row button, `ReportsManage`)

- **Schedule:** frequency and recipients as chips; typing suggests the emails of grants scoped to the Group; invalid or more than 25 addresses are refused with a message. Save.
- **Status:** "Next report: {label}, sends {local date and time} ({zone})" or "Not scheduled", and the last scheduled delivery's outcome with its error.
- **Send now / Download PDF:** a period picker (previous or earlier week, month, quarter, or custom dates) and, for Send now, recipients prefilled from the schedule and editable.
- **History:** the last 10 deliveries: period, when, Scheduled or Manual (with who), recipients, outcome.
- When email is Off, the dialog says so and links to the settings page; Download still works.

Manage groups shows a chip per row: the frequency, or "Report failed" in red when the latest scheduled delivery failed.

### Demo

Email is forced Off (settings page read-only with a note); Send now says the demo doesn't send email; Download works on demo data. The demo seeds Aurora Retail with a monthly schedule to `reports@aurora-retail.example` so the dialog shows a schedule.

## 7. Errors

- Provider rejection, authentication or network failure: recorded on the delivery with the provider's message; retried hourly for 24 hours (scheduled) or shown in the dialog (manual).
- `Mail.Send` not granted: Graph's 403 message is shown in the test send and recorded on deliveries; the docs explain the permission.
- An invalid time zone id in the database (for example after an OS change): fall back to UTC and log a warning.
- Rendering failure: treated as a failed send for that Group only.

## 8. Testing

- **Unit:** `ReportPeriod` (weekly, monthly, quarterly; Europe/London and Australia/Sydney daylight saving changes; year end; due times; custom range validation; labels); report calculations against `DomainStatistics`; change from previous period including "new"; receiver actions; top senders with owners; each next-step rule and the "nothing to do" line; PDF text extraction contains the verdict, domains, next steps and dates, renders with no logo, an SVG logo and no domains, and embeds the font; email body encodes names.
- **Database:** settings and schedule services validate and audit; recipient rules; SMTP password in the secret store; the scheduler with a fake clock and fake sender (sends once per period, not before the send hour, retries then gives up and alerts, later success resolves, one failing Group doesn't block others, email Off skips, manual sends don't block scheduled ones); Group scope on send and download.
- **Senders:** SMTP against an in-process SMTP server (recipients, subject, from, attachment); Graph against a fake HTTP handler (request body shape).
- **Web:** the download endpoint requires `ReportsManage` and Group scope; portal users are refused.
- **Browser (demo):** the Email & reports page, the Reports dialog, Download PDF opened and checked, Send now's demo message.

## 9. Docs

A new `website/docs/client-reports.mdx`: setting up Graph (adding `Mail.Send`, restricting it to one mailbox with an Exchange application access policy) or SMTP; the time zone and send hour; schedules and recipients; what a report contains; ad-hoc sends and downloads; the failure alert. Linked from the client portal and getting-started pages and the sidebar's administer section. The roadmap entry "Add scheduled client report export (PDF and email digest)" is marked complete.

## 10. Delivery order

1. Email settings, senders and test send.
2. Report settings, periods, schedules and deliveries.
3. Report model and calculations.
4. PDF and email rendering.
5. Scheduler, alert, manual send and download.
6. Screens, demo, docs.
