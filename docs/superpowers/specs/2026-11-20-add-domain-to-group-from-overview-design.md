# Add a domain to a group from its Overview (0.14 candidate)

A candidate for 0.14, written up for review. Nothing here is built.

## Intent

Someone with permission to edit domains, looking at a domain, should be able to see which groups it is in and put it in another group without leaving the page. Today the Overview doesn't mention groups at all, and the only place to change them is the Groups column on Manage domains.

## What happens today

* The domain page (`DomainDetail.razor`) already loads the domain with its groups (`Include(d => d.Groups)`, and a `_groupNames` map used for sender decisions), but the Overview tab does not show them.
* Groups are changed on **Manage domains**, in a multi-select in the Groups column, for people who pass the `DomainsEdit` policy. It calls `GroupManagementService.SetDomainGroupsAsync(db, actor, domainId, groupIds)`, which records the change in the audit log. Anyone else sees the group names as text.
* Someone limited to some groups can only open a domain that is in one of their groups (the page refuses it otherwise).
* In the web UI nobody who is limited to groups can edit. `IsScopable` is true only for the built-in Viewer role and cannot be set on a custom role, and Viewer has no edit permissions. So the edit control is only ever shown to people with no group limit, and Manage domains loads every group and domain without any scope handling because it never needs it.
* Group-limited writers exist only as API keys. `PUT` on a domain's groups (`DomainWriteEndpoints.SetGroupsAsync`) refuses groups outside the key's own groups (`403`), and keeps the domain's groups the key can't see ("groups outside its own are kept", as `api.mdx` says). It does not stop a key removing the domain from the last of its own groups, after which the key can no longer see the domain.
* Creating a group needs the `GroupsOrTagsWrite` policy, on Manage groups.

## Goals

* The Overview shows the domain's groups.
* A person with `DomainsEdit` can add the domain to a group, and remove it from one, from the Overview.
* The change is audited, exactly as it is from Manage domains, because it uses the same service.

## Non-goals

* A bulk "add these domains to a group" action. That is a natural follow-up, most useful on the Dashboard's By group view for the **No group** table, and is not part of this.
* Creating a group from the domain page, unless it is cheap (see open questions).

## Design

* **Show.** A **Groups** line near the top of the Overview: a chip for each group the domain is in, each opening the Dashboard filtered to that group, or "No groups" in secondary text. Everyone who can see the domain sees it.
* **Edit.** For `DomainsEdit`, the line gets an **Add to group** control. Reuse `SearchableMultiSelect`, which Manage domains uses, with the groups the domain is not in already, so it behaves the same and is searchable for estates with many groups. Each chip has a remove (x). Both call `SetDomainGroupsAsync` with the new full list and then refresh the line, with a snackbar naming what changed ("Added contoso.io to Aurora Retail").
* **Say what it affects.** Groups decide which alert rules (by scope), ticket rule overrides and PSA companies apply, so a short note under the control says that, with a link to the alert rules page. No attempt to compute the exact effect in this slice.
* **Group-limited editors (a rule, set by the user).** Someone limited to groups can only put a domain in their own groups, and cannot remove the last group that gives them access to it. In the UI this cannot arise today (see above), so the Overview needs no special case, but the control must not assume it stays that way: if a group-limited role with edit permission ever exists, the options offered are the person's own groups and removing the last one is refused with a message saying so.

## A separate gap this turned up

The rule above is not enforced for the one place group-limited writers exist, API keys. A key limited to groups can remove a domain from the last of its own groups and then loses sight of it, with nothing in the response or the docs saying so. Enforcing it means `SetGroupsAsync` returns an error ("this would take the domain out of every group this key can see") when the resulting groups would include none of the key's own, with a test beside the existing scoped-key tests, and a line in `api.mdx` under Keys limited to groups. It changes documented API behaviour a little, so it is better as its own small change than folded into this one, and could ship as a patch since nothing else depends on it.

## Left to the builder

Chip and button layout, whether the control sits in the Overview header or in its own card, wording.

## Anti-goals

A change that leaves the page stale. A group-limited person removing their own access. A second code path that changes groups without the audit entry.

## Test plan

* The service is already covered. Add a test that the Overview's group editing calls it with the full resulting list (the new group added, an existing one removed, nothing else disturbed).
* A structural test that the edit control is inside a `DomainsEdit` `AuthorizeView` and the plain chips are not.
* Browser check on the demo: add the domain to a group and the chip appears, remove it and it goes, the audit log shows both, and a Viewer (including the group-limited demo viewer) sees the chips with no controls.

## Open questions

* Should the API's last-group rule be fixed now, as the small separate change described above?
* Should the control offer **Create group** to people who also have `GroupsOrTagsWrite`?
* Should a chip open the Dashboard filtered to the group, or Manage groups?
