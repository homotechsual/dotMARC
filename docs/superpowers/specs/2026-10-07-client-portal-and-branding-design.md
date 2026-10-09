# Client portal and branding design

**Release:** dotMARC 0.9.0
**Status:** approved in brainstorming, awaiting spec review

## Purpose

MSPs give clients read-only access to their own domains today, but a client signing in sees the operator dashboard. This adds a simplified, branded client portal: a plain-language view of the client's domains under the MSP's brand, which a Group can override with the client's own. It lets an MSP present dotMARC as part of their service.

## Decisions made in brainstorming

- **Same URL.** Clients sign in at the existing dotMARC address. No per-client domains or links.
- **Per-person switch.** An access grant has a Client portal option. Grants with it land on the portal; everyone else keeps the full app, including group-scoped staff.
- **Layered branding.** An MSP-wide default (product name, colours, logos, support contact, footer) applies to every portal; a Group can override its display name, logo and colours.
- **Summary plus drill-in.** A home page summarising each domain in plain language, and a simplified read-only domain page.
- **Approach A:** a portal area inside the app with its own layout, reusing the existing data and authorization. Rejected: theming today's Viewer pages (still an operator tool), and a separate front end over the Public API (a second app to build and secure).

## Scope

**In:** the portal flag and its enforcement; portal home and domain pages; plain-language status; MSP branding settings with logos and a preview; Group branding overrides; preview as client; a demo Client persona; docs.

**Out for 0.9.0:** custom domains per client; branded sign-in (sign-in stays Entra); per-client emails and reports (the next 0.9.0 item); any client actions such as acknowledging alerts.

## 1. Data model and branding

### Portal access

- `UserAccess.IsClientPortal` (bool, default false), set on the Access page through `UserAccessManagementService` and audited as `access.client_portal_changed`.
- `UserAccessClaimsTransformation` adds the claim `dotmarc:client-portal` = `true` when set. API keys never carry it.
- Turning the switch on is refused for a grant with no scoped Groups ("A client portal grant must be limited to the client's Groups."), and for a role that isn't scopable. Removing a grant's last scoped Group while the switch is on is refused the same way.

### `BrandingSettings` (singleton, seeded `Id = 1`)

| Column | Notes |
| --- | --- |
| ProductName | string, max 60, default "dotMARC" |
| PrimaryColour | `#RRGGBB`, default dotMARC's current primary |
| SecondaryColour | `#RRGGBB`, default dotMARC's current secondary |
| LogoImageId | nullable FK to `BrandingImage` (light backgrounds) |
| DarkLogoImageId | nullable FK to `BrandingImage` (dark backgrounds; falls back to LogoImageId) |
| SupportEmail | nullable, validated as an email |
| SupportUrl | nullable, absolute https URL |
| SupportPhone | nullable, max 40 |
| FooterText | nullable, max 200, plain text |

Saved through `BrandingSettingsService.SaveAsync(context, actor, updated, ct)`, audited as `settings.branding.saved`.

### `GroupBranding` (optional, one per Group)

| Column | Notes |
| --- | --- |
| GroupId | PK and FK to Group, cascade delete |
| DisplayName | nullable, max 100 |
| LogoImageId | nullable FK to `BrandingImage` |
| DarkLogoImageId | nullable FK to `BrandingImage` |
| PrimaryColour | nullable `#RRGGBB` |
| SecondaryColour | nullable `#RRGGBB` |

Saved through `GroupManagementService.SetBrandingAsync(context, actor, groupId, GroupBrandingInput input, ct)`, audited as `group.branding_changed`. A row whose fields are all blank is deleted.

### `BrandingImage`

| Column | Notes |
| --- | --- |
| Id | Guid, new on every upload, so URLs never serve a stale image |
| ContentType | `image/png`, `image/jpeg` or `image/svg+xml` |
| Bytes | bytea, at most 512 KB |
| Sha256 | hex, used as the ETag |
| UploadedUtc | timestamp |

- `BrandingImages.ValidateAsync(contentType, bytes)` checks the type by content (PNG and JPEG signatures; SVG parsed as XML), the size, and for SVG refuses `<script>`, `<foreignObject>`, any `on*` attribute, and any `href` or `xlink:href` that isn't a fragment (`#...`) or a `data:image/` URI.
- Images no longer referenced by `BrandingSettings` or any `GroupBranding` are deleted when branding is saved.
- `GET /branding/logo/{id}` serves the image anonymously (sign-in pages and emails may need it later), with `Cache-Control: public, max-age=31536000, immutable`, `ETag` = Sha256, `X-Content-Type-Options: nosniff`, and for SVG `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`. Unknown ids return 404.

### Brand resolution

`PortalBranding.Resolve(BrandingSettings msp, IReadOnlyList<(Group Group, GroupBranding? Branding)> scopedGroups) : ResolvedBrand`:

- Exactly one scoped Group has a `GroupBranding` row: its non-blank fields over the MSP default.
- None, or more than one: the MSP default.
- `Heading`: the single scoped Group's display name (its `GroupBranding.DisplayName` or else its Group name) when the person has exactly one scoped Group; otherwise the product name.
- `ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour, Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText)`.

### Colours

- `BrandColours.IsValid(string)` accepts `#RRGGBB` only (case-insensitive).
- `BrandColours.ContrastRatio(a, b)` (WCAG relative luminance). The settings preview warns when the primary colour against white, or against the dark background, is below 4.5:1. It warns; it doesn't refuse.
- `PortalTheme.For(ResolvedBrand)` builds the portal's `MudTheme`: Primary and Secondary from the brand in both palettes, everything else from dotMARC's existing theme.

## 2. Portal pages and enforcement

### Layout

`PortalLayout`: app bar with the brand logo (dark logo in dark mode; the product name as text if there's no logo), the heading, a dark-mode toggle and Sign out. The footer shows the support contacts and footer text. No Manage menu, no internal links. The brand is resolved once per circuit from the user's scoped Groups.

### `/portal` (home)

- A heading with the client's name, and a verdict sentence: "All 3 domains are protected." or "2 of 3 domains are fully protected. 1 needs attention."
- A card per domain in the person's scoped Groups (monitored domains only), ordered with attention-needing first:
  - the status and, when not Protected, the reasons (one line each);
  - DMARC pass rate over the last 30 days and a small 30-day trend line, from `DomainStatistics`;
  - open alerts (title and when raised);
  - a link to the domain page.

### `PortalStatus`

`PortalStatus.For(Domain domain, DomainStatistics stats, IReadOnlyList<AlertEvent> openAlerts) : PortalDomainStatus(PortalHealth Health, IReadOnlyList<string> Reasons)`, with `PortalHealth { Protected, MonitoringOnly, NeedsAttention, NoReportsYet }`:

- **NeedsAttention** when any health check (DMARC record, DMARC authorization, SPF, MX, DKIM, TLS reporting, MTA-STS when hosted) is failing, or there are open alerts. Each failing check and open alert adds a reason in plain language, taken from the existing status presentation text where it exists.
- **NoReportsYet** when no aggregate report has arrived and nothing above applies: "No DMARC reports have arrived yet. They usually start within a few days."
- **MonitoringOnly** when the DMARC policy is `p=none`: "The DMARC policy only monitors, so mail spoofing this domain isn't blocked yet."
- **Protected** otherwise (policy quarantine or reject, all checks passing, no open alerts).

### `/portal/domains/{name}`

- Only domains in the person's scoped Groups; anything else is a "not found" page (never revealing that it exists).
- **Policy:** the current DMARC policy, subdomain policy and percentage in one sentence.
- **Health:** each check with a plain status and the existing explanation text. No push, recheck or configure buttons.
- **Who sends as this domain:** the top 10 sending sources by volume over 30 days, with pass and fail counts, reusing the domain page's source grouping. No raw XML, IP enrichment detail or report lists.
- **Alerts:** open alerts, and alerts resolved in the last 30 days, read-only.

### Empty states

No domains: "There are no domains to show yet." plus the support contact. A domain with no reports: the NoReportsYet text. Never an error page.

### Enforcement

- New requirement `NotClientPortalRequirement`, satisfied when the user lacks the `dotmarc:client-portal` claim. It is added to every existing named policy and to the fallback (authenticated) policy, so portal users are denied every internal page, every API endpoint and every write.
- Policy `ClientPortal`: authenticated with the claim. Portal pages use it.
- Redirects: a portal user requesting `/`, `/dashboard` or any page that denies them for this reason is sent to `/portal` (`AccessDenied` checks the claim and redirects). Other users requesting `/portal` are sent to `/dashboard`.
- Data scoping: portal pages read domains through the same group-scope filter the dashboard uses (`dotmarc:scoped-group` claims), never through a parameter.

### Preview as client

`/portal/preview/{groupId}`, policy `GroupsOrTagsWrite` (staff only): renders the portal exactly as a client scoped to that one Group would see it, with a "Preview: this is what clients of {Group} see" banner. Not audited (read-only).

### Demo

A third persona, "Demo Client", with the portal switch on and scoped to Aurora Retail. The demo seeds MSP branding ("Nova MSP", its colours, support contact) and Aurora Retail branding (display name and colours; no uploaded logo, so the name shows as text).

## 3. Settings pages

- **Manage > Branding** (`/branding/settings`, policy `AccessManage`): product name, both colours (with colour pickers and hex fields), light and dark logos (upload, remove), support email, URL and phone, footer text. A live preview of the portal app bar and one domain card in light and dark, using the unsaved values. A contrast warning beside each colour when it applies.
- **Manage Groups:** a **Branding** button per Group (policy `GroupsOrTagsWrite`) opening a dialog with display name, logos and colours, each showing "Using the MSP default" when blank, a preview, and a **Preview as client** link.
- **Access page:** a **Client portal** switch on each grant, shown only for scopable roles, with help: "This person sees a simplified, branded view of their Groups' domains instead of dotMARC. Use it for client contacts."

## 4. Errors

- Upload refused: "Logos must be PNG, JPEG or SVG, up to 512 KB." or "This SVG contains scripts or external links, so it can't be used."
- Invalid colour, email or URL on save: the field-specific message, nothing saved.
- Missing logo id: 404; the layout falls back to the product name.
- A portal user whose scoped Groups were all removed by an admin can't occur (refused), but if the data says otherwise the portal shows the empty state rather than every domain.

## 5. Testing

- **Unit:** `PortalBranding.Resolve` (no branded Group, one, several, field fallback, heading rules); `PortalStatus.For` for each health and reason; `BrandingImages` validation (PNG and JPEG signatures, oversize, SVG with script, `onload`, `foreignObject`, external `href`, `xlink:href`, allowed fragment and data image); `BrandColours` validation and contrast.
- **Postgres:** branding settings save and audit; Group branding save, clear-to-delete and audit; image replacement deletes the unreferenced image; the portal switch refused without scoped Groups and audited when set; removing the last scoped Group refused while the switch is on.
- **Web (`WebApplicationFactory`, demo mode with the Demo Client persona):** `/dashboard` redirects to `/portal`; `/domains`, `/groups`, `/alerts/settings` are denied with a redirect to `/portal`; `/api/v1/domains` with the portal cookie is 403; `/portal` lists only Aurora Retail's domains; another Group's domain page is not found; `/branding/logo/{id}` returns the bytes with the caching, nosniff and CSP headers.
- **Audit coverage:** the new mutating service methods take the actor.
- **Browser check** (playwright-edge, demo): Demo Client home and domain page in light and dark; the Branding page preview; Group branding and Preview as client.

## 6. Delivery order

One plan in three phases, each leaving the suite green:

1. **Portal access and pages (MSP default brand only):** the flag, claim, `NotClientPortalRequirement`, redirects, `PortalLayout`, `PortalStatus`, home and domain pages, the demo persona, the Access page switch.
2. **MSP branding:** `BrandingSettings`, `BrandingImage` with validation and serving, `PortalTheme`, the Branding page with preview.
3. **Group overrides:** `GroupBranding`, resolution per Group, the Group branding dialog, preview as client, docs (`website/docs/client-portal.mdx`) and the roadmap entry marked complete.
