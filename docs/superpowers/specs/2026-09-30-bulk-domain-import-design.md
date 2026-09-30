# Bulk domain import: add many domains at once from a pasted list or a CSV file

Date: 2026-09-30. Status: approved design, not yet planned or built. Target release: v0.8.0.

## Problem

Domains are added one at a time on Manage domains. Onboarding a client with dozens of domains, or an MSP's whole
customer base, is slow and error-prone that way. This is the "Allow bulk domain onboarding with CSV or paste-a-list
imports" idea on the roadmap.

## Decisions

1. **One input, two ways in.** The page accepts a pasted list or an uploaded CSV file. Both go through the same
   parser, so a pasted list is simply a CSV with one column. Uploading a file replaces whatever is in the paste box.
2. **Nothing is saved until the preview is confirmed.** The preview shows what will happen to every row.
3. **Unknown groups and tags are flagged, not guessed.** For each group or tag name that doesn't exist, the person
   chooses to create it (only if they have permission), map it to an existing one (close matches suggested, to catch
   typos), or leave it out. A name that differs from an existing one only in case is treated as that one.
4. **Existing domains are the person's choice, per import.** A switch on the preview either skips domains that are
   already monitored, or adds the listed groups and tags to them. Nothing is ever removed from an existing domain.
5. **All or nothing.** The import runs in one transaction.
6. **The audit log sees an import as the individual changes it makes,** by reusing the existing service methods, plus
   one summary entry.
7. **A dedicated page**, `/domains/import`, reached from an **Import domains** button on Manage domains.
8. **Health checks aren't run during the import.** New domains have never been checked, so the next polling cycle
   picks them up, as it does for a domain added by hand.

## Input format

* Columns, in order: **domain**, **groups**, **tags**. Only the domain is required.
* A header row is optional. It is recognised when the first cell of the first non-blank line is `domain`, ignoring
  case and spaces.
* Several groups or tags in one cell are separated by `;`, for example `Client A;Client B`. Each name is trimmed, and
  empty names are ignored.
* Cells follow RFC 4180: a cell may be wrapped in double quotes, and a quote inside it is doubled. A quoted cell may
  contain commas and line breaks.
* A UTF-8 byte order mark is ignored. Blank lines, and lines whose first cell starts with `#`, are ignored.
* Limits: a file of at most 1 MB, and at most 1,000 data rows. Over either limit, or a file that isn't valid UTF-8
  text, the whole input is refused with a message saying why. Nothing else refuses the whole input.

## Domain names

`DomainNameValidator.TryNormalize` becomes stricter, for single adds as well as imports:

* Trimmed, lowercased, and a single trailing dot removed.
* Unicode names are converted to their ASCII (`xn--`) form with `IdnMapping`, as DMARC reports use.
* At least two labels, at most 253 characters in total. Each label is 1 to 63 characters of letters, digits and
  hyphens, and doesn't start or end with a hyphen. The last label isn't all digits (so an IP address is refused).

Everything it accepts today that is a real hostname still passes. What it newly refuses includes URLs
(`https://contoso.com/`), email addresses and IP addresses. Domains already in the database aren't re-validated.

## The preview

### Rows

Each data row gets one status:

| Status | Meaning |
| --- | --- |
| **New** | The domain will be added, with its groups and tags. |
| **Already monitored** | The domain exists. It is skipped, or has the row's groups and tags added, depending on the switch. |
| **Duplicate** | The same domain appears on an earlier row. This row is merged into that one: their groups and tags are combined. |
| **Invalid** | The domain isn't a valid name. The reason is shown, and the row isn't imported. |

Rows keep their line number in the input, so the person can find them.

### Unknown groups and tags

A panel above the table lists each distinct unknown group name and unknown tag name once, with the rows that use it.
For each, the person picks:

* **Create it.** Offered only with `GroupsAdd` (for groups) or `TagsAdd` (for tags). A new tag gets the default colour,
  which can be changed later on Manage groups.
* **Map to** an existing group or tag, from a searchable list.
* **Leave it out.** The name is dropped from every row that uses it.

The default choice is **Map to** the best match when one is close enough, otherwise **Create it** when permitted,
otherwise **Leave it out**. A match is close enough when the names are equal after ignoring case, punctuation and
spacing, and endings such as Limited, Ltd, LLC and Inc (the same loose comparison `HaloGroupSuggestions` uses), or when
they are within two single-character edits of each other. Up to three suggestions are shown, best first.

### Permissions

* The page needs `DomainsAdd`.
* The group and tag columns are applied only with `DomainsEdit`, the same rule as the pickers on Manage domains.
  Without it the preview says the groups and tags will be ignored, and the unknown-name panel isn't shown.

## Importing

When the preview is confirmed, in one transaction:

1. Create the groups and tags the person chose to create (`GroupManagementService.AddGroupAsync`,
   `TagManagementService.AddTagAsync`). If one now exists with that name, because someone created it after the
   preview, that one is used instead.
2. Add each **New** domain (`DomainManagementService.AddDomainAsync`). If it now exists, it is treated as
   **Already monitored**.
3. Set each domain's groups and tags (`GroupManagementService.SetDomainGroupsAsync`,
   `TagManagementService.SetDomainTagsAsync`) to the union of what it has and what the row lists. For a new domain that
   is just the row's. Existing domains are only touched when the switch says to add their groups and tags.
4. Record one `domains.imported` entry summarising the counts, for example "Imported 42 domains, updated 5, skipped 3
   invalid and 2 already monitored". `DomainsImported` joins `AuditActions` and its `All` list (label "Domains
   imported"), so the Audit log page can filter on it.

`AuditLog.SaveAndRecordAsync` currently always begins its own transaction. It changes to join the caller's transaction
when one is already open, so the services it backs can run inside the import's transaction.

Because the existing services do the work, the audit log gets exactly the entries it would for the same changes made
by hand, each attributed to the person importing.

### The result

The page shows the counts and a row-by-row list of what happened, a link back to Manage domains, and a note that
health checks run for the new domains within the next polling cycle.

## Errors

* A bad row never fails the input. It is **Invalid**, with its reason.
* The whole input is refused only for the size and row limits, or text that isn't valid UTF-8.
* If importing fails, the transaction rolls back, nothing is saved, and the person stays on the preview with a message,
  so they can try again.

## Testing

* **Parser:** a single column; all three columns; a header row, and data whose first domain happens to look like a
  header value; quoted cells with commas, quotes and line breaks; a byte order mark; `#` comments and blank lines;
  `;` lists with stray spaces and empty names; the row limit.
* **Domain names:** today's accepted names still pass; URLs, email addresses, IP addresses, bad labels and over-long
  names are refused; Unicode names become `xn--` form; a trailing dot is removed.
* **Suggestions:** loose matches and one- or two-edit typos are suggested, best first; unrelated names aren't.
* **Planner** (against the database): each row status; duplicates merged; unknown names detected; a case-only
  difference isn't unknown; **Create it** isn't offered without permission; group and tag columns ignored without
  `DomainsEdit`.
* **Importing** (against the database): domains, groups, tags and assignments saved; the expected audit entries,
  including `domains.imported`; existing domains gain groups and tags but lose none; with the switch off they are
  untouched; a failure partway saves nothing.
* **Browser, on the demo:** paste a list, upload a CSV, map a typo to an existing group, create a new one, import, and
  check the result page, Manage domains and the audit log.

## Rollout

* No database migration.
* The page links to a small sample CSV, served as a static file.
* Docs: a new "Import domains" page, linked from the Manage domains docs.
* Roadmap: the idea is set to `complete` for `v0.8.0` in `canny-roadmap.json`.

## Out of scope

* A Halo client column, and any other per-domain settings (MTA-STS, DKIM selectors, monitoring off).
* Importing from Excel `.xlsx` files. A spreadsheet can be saved as CSV.
* Removing groups or tags from existing domains.
* Imports larger than 1,000 rows, and running imports in the background.
