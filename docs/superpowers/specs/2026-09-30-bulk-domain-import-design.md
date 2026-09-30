# Bulk domain import: add and update many domains at once from a pasted list, CSV or Excel file

Date: 2026-09-30. Status: approved design, not yet planned or built. Target release: v0.8.0.

## Problem

Domains are added and configured one at a time on Manage domains and each domain's page. Onboarding a client with
dozens of domains, or an MSP's whole customer base, is slow and error-prone that way, and so is re-organising many
existing domains. This is the "Allow bulk domain onboarding with CSV or paste-a-list imports" idea on the roadmap.

## Decisions

1. **One input, three ways in.** The page accepts a pasted list, a CSV file or an Excel `.xlsx` file. All three become
   the same rows and go through the same rules, so a pasted list is simply one column. Uploading a file replaces
   whatever is in the paste box.
2. **Nothing is saved until the preview is confirmed.** The preview shows what will happen to every row, with
   anything that will be removed shown in red.
3. **Unknown groups, tags and Halo clients are flagged, not guessed.** For each unknown name the person chooses to
   create it (groups and tags only, and only with permission), map it to an existing one (close matches suggested, to
   catch typos), or leave it out. A name that differs from an existing one only in case is treated as that one.
4. **Existing domains are the person's choice, per import**, with three modes: **Skip**, **Add**, or **Match the
   file**. Add can also remove named groups and tags with `-Name` entries.
5. **A blank cell leaves that value as it is**, except groups and tags in Match mode, where blank means none.
6. **All or nothing.** The import runs in one transaction.
7. **The audit log sees an import as the individual changes it makes,** by reusing the existing service methods, plus
   one summary entry.
8. **A dedicated page**, `/domains/import`, reached from an **Import domains** button on Manage domains.
9. **Health checks aren't run during the import.** New domains have never been checked, so the next polling cycle
   picks them up, as it does for a domain added by hand.

## Input

### Ways in

* **Paste:** a text box, read as CSV.
* **CSV file:** UTF-8, with or without a byte order mark. At most 1 MB.
* **Excel file:** `.xlsx` only, read with the ExcelDataReader package (MIT licence), first worksheet only. Each cell
  becomes text: numbers and dates in invariant culture, formulas as their saved value. At most 5 MB. Old `.xls` files
  are refused with a message to save as `.xlsx` or CSV.

After that, all three are the same list of rows of cells.

### Reading rows

* CSV cells follow RFC 4180: a cell may be wrapped in double quotes, and a quote inside it is doubled. A quoted cell may
  contain commas and line breaks.
* Blank rows, and rows whose first cell starts with `#`, are ignored.
* At most 1,000 data rows.
* Over a size or row limit, a file that isn't valid UTF-8 text, or an unreadable `.xlsx`, the whole input is refused
  with a message saying why. Nothing else refuses the whole input.

### Columns

| Column | Header names accepted | Value |
| --- | --- | --- |
| Domain | `domain` | Required. See Domain names. |
| Groups | `groups`, `group` | `;`-separated names. In Add mode an entry `-Name` removes that group. |
| Tags | `tags`, `tag` | `;`-separated names. In Add mode an entry `-Name` removes that tag. |
| Halo client | `halo client`, `halo` | A Halo client's name. |
| Monitored | `monitored` | `yes`/`no`, `true`/`false`, `1`/`0`. |
| DKIM selectors | `dkim selectors`, `dkim` | `;`-separated selectors, each a valid DNS label. Replaces the domain's list. |
| MTA-STS mode | `mta-sts mode`, `mta-sts` | `off`, `none`, `testing` or `enforce`. |
| MTA-STS MX hosts | `mta-sts mx hosts`, `mx hosts` | `;`-separated host names. |
| MTA-STS max age | `mta-sts max age`, `max age` | Seconds, 1 to 31,557,600 (the MTA-STS maximum). |

* **Without a header row**, cells are read in the order above, and later columns can be left off. So a list of domains,
  one per line, is valid input.
* **A header row** is recognised when one of the first row's cells is `domain`. A data row never holds that, since a
  domain has a dot. Header names are compared
  ignoring case, spaces, hyphens and underscores. With a header, columns can be in any order and any can be missing.
  An unrecognised header is shown as a warning and its column ignored.
* In `;` lists each entry is trimmed, and empty entries are ignored.
* **A blank cell leaves that value as it is.** The one exception is Groups and Tags in Match mode (below).
* **A value that doesn't make sense** (for example `monitored` = `maybe`, or a max age of 0) doesn't fail the row. The
  row is imported without that one value, and the preview says why.

### MTA-STS values

* `off` turns MTA-STS hosting off. `none`, `testing` and `enforce` turn it on (if it isn't already) with that mode.
* Turning it on needs MX hosts: the row's, or the domain's saved ones. If there are neither, the MX hosts are looked up
  in DNS at preview time, as the domain page's Enable button does, and the preview shows what was found. If none are
  found, MTA-STS isn't turned on for that row, and the preview says why.
* A max age that isn't given uses the domain's current value, or 604,800 seconds (one week) for a domain turning
  MTA-STS on for the first time, as the Enable button does.
* The changes go through `DomainManagementService.SetMtaStsConfigAsync`, so hosting is provisioned by the polling cycle
  as it is today.

## Domain names

`DomainNameValidator.TryNormalize` becomes stricter, for single adds as well as imports:

* Trimmed, lowercased, and a single trailing dot removed.
* Unicode names are converted to their ASCII (`xn--`) form with `IdnMapping`, as DMARC reports use.
* At least two labels, at most 253 characters in total. Each label is 1 to 63 characters of letters, digits and
  hyphens, and doesn't start or end with a hyphen. The last label isn't all digits (so an IP address is refused).

Everything it accepts today that is a real hostname still passes. What it newly refuses includes URLs
(`https://contoso.com/`), email addresses and IP addresses. Domains already in the database aren't re-validated.

## Existing domains

A switch on the preview picks one of three modes for rows whose domain is already monitored:

| Mode | Groups and tags | Other columns |
| --- | --- | --- |
| **Skip** (default) | Untouched. | Untouched. |
| **Add** | The row's names are added. `-Name` entries remove that group or tag. Nothing else is removed. | Non-blank values are applied. |
| **Match the file** | Become exactly what the row lists, for each of the two columns present in the input. A blank cell means none. `-Name` entries are ignored. | Non-blank values are applied. |

For new domains the mode makes no difference: the domain gets the row's groups, tags and values. `-Name` entries on a
new domain are ignored with a note, since there is nothing to remove.

Every removal (a group or tag coming off a domain, or a setting being turned off) is shown in red on the preview.

## The preview

### Rows

Each data row gets one status:

| Status | Meaning |
| --- | --- |
| **New** | The domain will be added, with its groups, tags and values. |
| **Already monitored** | The domain exists. What happens depends on the mode. |
| **Duplicate** | The same domain appears on an earlier row. This row is merged into that one: their groups and tags are combined, and for other values the later non-blank one wins. |
| **Invalid** | The domain isn't a valid name. The reason is shown, and the row isn't imported. |

Rows keep their line number (or row number in Excel), so the person can find them. Each row shows the changes it will
make, and any values that will be ignored, with the reason.

### Unknown groups, tags and Halo clients

A panel above the table lists each distinct unknown group, tag and Halo client name once, with the rows that use it.
For each, the person picks:

* **Create it.** Groups and tags only, and only with `GroupsAdd` (groups) or `TagsAdd` (tags). A new tag gets the
  default colour, which can be changed later on Manage groups. Halo clients can't be created: they live in Halo.
* **Map to** an existing group, tag or Halo client, from a searchable list.
* **Leave it out.** The name is dropped from every row that uses it.

The default is **Map to** an existing name that is the same name written differently: equal after ignoring case,
punctuation and spacing, and endings such as Limited, Ltd, LLC and Inc (the same loose comparison `HaloGroupSuggestions`
uses). Otherwise it is **Create it** when possible, otherwise **Leave it out**. Names within two single-character edits
are suggested (up to three, best first) but never chosen by default, because a near miss can be a genuinely different
name, such as "Client C" and "Client A". (Changed while planning: the first version also defaulted to typo matches.)

`-Name` removal entries naming something that doesn't exist are ignored with a note, since there's nothing to remove.

### Halo clients

The Halo client column is used only when HaloPSA is connected. The client list comes from `IHaloPsaClient.ListClientsAsync`.
If Halo isn't connected, or the list can't be loaded, the column is ignored and the preview says why.

### Permissions

* The page needs `DomainsAdd`.
* Groups, Tags, Halo client, Monitored and DKIM selectors need `DomainsEdit`, the same rule as on Manage domains and the
  domain page.
* The MTA-STS columns need `MtaStsManage`.
* Without the permission for a column, that column is ignored and the preview says so. The unknown-name panel only lists
  names from columns that will be used.

## Importing

When the preview is confirmed, in one transaction:

1. Create the groups and tags the person chose to create (`GroupManagementService.AddGroupAsync`,
   `TagManagementService.AddTagAsync`). If one now exists with that name, because someone created it after the preview,
   that one is used instead.
2. Add each **New** domain (`DomainManagementService.AddDomainAsync`). If it now exists, it is treated as
   **Already monitored**.
3. For each domain being changed, apply its values through the existing services, each of which records only what
   actually changes:
   * groups and tags: `GroupManagementService.SetDomainGroupsAsync`, `TagManagementService.SetDomainTagsAsync`, with the
     set worked out from the mode;
   * Halo client: `DomainManagementService.SetHaloClientIdAsync`;
   * monitored: `DomainManagementService.SetMonitoredAsync`;
   * DKIM selectors: `DomainManagementService.SetDkimSelectorsAsync`;
   * MTA-STS: `DomainManagementService.SetMtaStsConfigAsync`, with blank values filled from the domain's current ones.
4. Record one `domains.imported` entry summarising the counts, for example "Imported 42 domains, updated 5, skipped 3
   invalid and 2 already monitored". `DomainsImported` joins `AuditActions` and its `All` list (label "Domains
   imported"), so the Audit log page can filter on it.

`AuditLog.SaveAndRecordAsync` currently always begins its own transaction. It changes to join the caller's transaction
when one is already open, so the services it backs can run inside the import's transaction.

Because the existing services do the work, the audit log gets exactly the entries it would for the same changes made by
hand, each attributed to the person importing.

### The result

The page shows the counts and a row-by-row list of what happened, a link back to Manage domains, and a note that health
checks run for the new domains within the next polling cycle.

## Errors

* A bad row never fails the input: an invalid domain makes that row **Invalid**, and a bad value is skipped with its
  reason.
* The whole input is refused only for the size and row limits, text that isn't valid UTF-8, or an unreadable or `.xls`
  spreadsheet.
* If importing fails, the transaction rolls back, nothing is saved, and the person stays on the preview with a message,
  so they can try again.

## Testing

* **Reading input:** CSV with a single column; all columns positionally; a header row in a different order, with
  missing and unrecognised columns; quoted cells with commas, quotes and line breaks; a byte order mark; `#` comments
  and blank rows; `;` lists with stray spaces and empty entries; the row and size limits. `.xlsx` files built in the
  test: text, numbers, a formula's saved value, and an `.xls` being refused.
* **Values:** each column's accepted and refused values, including monitored words, DKIM selector labels, MTA-STS modes
  and the max age range.
* **Domain names:** today's accepted names still pass; URLs, email addresses, IP addresses, bad labels and over-long names
  are refused; Unicode names become `xn--` form; a trailing dot is removed.
* **Suggestions:** loose matches and one- or two-edit typos are suggested, best first; unrelated names aren't.
* **Planner** (against the database): each row status; duplicates merged; unknown groups, tags and Halo clients
  detected; a case-only difference isn't unknown; **Create it** not offered without permission or for Halo clients;
  columns ignored without their permission or without Halo; the three modes, `-Name` removals, and Match mode clearing;
  MTA-STS turning on with looked-up MX hosts, and not turning on when none are found.
* **Importing** (against the database): domains, groups, tags, assignments and settings saved; the expected audit
  entries, including `domains.imported`; each mode's effect on existing domains; a failure partway saves nothing.
* **Browser, on the demo:** paste a list, upload a CSV and an `.xlsx`, map a typo to an existing group, create a new
  one, use Add with a `-Name` removal and Match mode, import, and check the result page, Manage domains and the audit
  log.

## Rollout

* No database migration.
* A new package reference: ExcelDataReader.
* The page links to a sample CSV and a sample `.xlsx`, served as static files.
* Docs: a new "Import domains" page, linked from the Manage domains docs.
* Roadmap: the idea is set to `complete` for `v0.8.0` in `canny-roadmap.json`.

## Out of scope

* Imports larger than 1,000 rows, and running imports in the background.
* Old `.xls` files, other worksheets than the first, and other spreadsheet formats.
* Removing domains through an import.
