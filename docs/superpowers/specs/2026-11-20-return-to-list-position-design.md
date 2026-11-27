# Return to where you were in a list (0.14 candidate)

A candidate for 0.14, written up for review. Nothing here is built.

## Intent

Open a domain from the Dashboard, press Back, and be where you were: the same scroll position, the same filters and the same sort. Today the list comes back from the top, so working down a long list of domains means scrolling to your place again after every visit.

## What happens today (measured)

On the demo, with the Dashboard scrolled to 500px of a 3,000px page (25 domains), opening a domain and returning landed at 0px both ways:

* the page's **Back** button, which is a plain link (`<MudButton Href="/dashboard">` in `DomainDetail.razor`), a fresh navigation to the Dashboard.
* the browser's own back arrow.

Why it can't just work:

* Pages for signed-in users render as Blazor Interactive Server (`App.razor`), and the Dashboard fills its table after it loads, in `OnInitializedAsync`. The browser tries to restore a scroll position before the rows exist, so it has nowhere to scroll to.
* A returning Dashboard is a new component. Its filters, search text, sort and view come from the saved default or from nothing, not from what you had a minute ago. Restoring scroll alone would put you at a different row if any of those differ.
* Rows open the domain through `NavigationManager.NavigateTo`, so there is a history entry to come back to, but nothing records the list's state.
* The Dashboard table has no paging, so every row is in the page and the scroll height is predictable once it has loaded. There are no JavaScript files of dotMARC's own under `wwwroot` today.

## Goals

* Back to a list returns the scroll position, filters, search text, sort column and direction, and the All domains or By group view, as they were.
* It works for both the page's Back button and the browser's back arrow.
* A fresh visit (from the menu, a bookmark, or a new tab) behaves as now, starting from the saved default.

## Non-goals

* Remembering across sign-ins or browsers. The memory is per browser tab and short lived.
* Restoring the scroll inside a domain's tabs.

## Design

1. **Remember the list's state when leaving it.** A small `ListPositionMemory` keeps, per list route, the view state (a record: grouped view, group, tag, column filters, search text, sort and direction) plus the scroll offset. The Dashboard registers a location-changing handler (`NavigationManager.RegisterLocationChangingHandler`) and saves when the destination is not the list itself. The scroll offset is read with one JS call. Storage is the browser's `sessionStorage` (per tab, gone when the tab closes), keyed by route, with a timestamp so something older than 30 minutes is ignored.
2. **Make Back a real "back".** On the domain page, the Back button goes to the previous history entry when that entry is the list, and to `/dashboard` otherwise (someone who arrived from an alert or a link). Navigation state (`NavigationManager.HistoryEntryState`) or a one-line `history.back()` call tells which. The browser's own back arrow already goes to the previous entry, so it needs nothing.
3. **Restore on arrival.** When the Dashboard loads and finds a remembered state for its route that was saved by leaving it, it applies the state before the first data load (so the right rows are there), then after the table has rendered it scrolls to the saved offset. The scroll call has to wait until after the render, and to cap at the page height, because the offset can exceed it if rows were removed meanwhile. The remembered state is used once, then discarded.
4. **Precedence.** A remembered state wins over the saved default for that arrival. A fresh visit has none, so the saved default applies as today.
5. **One helper for every list.** The Dashboard first. Alerts, Manage domains, the audit log and any other list that opens something and comes back can use the same helper later, each deciding what its state record holds.

## Left to the builder

The shape of the state record, how the sort is restored through MudBlazor's `MudTable` (probably the one fiddly part: it needs a spike to confirm the sort label API can be driven from code), and whether the JS is a small module file or inline.

## Anti-goals

Restoring a stale list after a long break. Scroll jumping while rows are still loading. A Back button that leaves the app when there is no list to go back to.

## Test plan

* Pure tests for the memory: save and take once, the 30 minute expiry, route keys kept apart, a corrupt stored value ignored.
* A structural test that the domain page's Back handling falls back to `/dashboard`.
* Browser check on the demo (the recipe above): scroll to 500px, open a row, then Back and the browser back arrow each land within a few pixels of 500, with filters, search and sort intact. Also a fresh visit from the menu starts at the top.

## Open questions

* Should the page's Back button always use history, or only when the previous entry is the list?
* Which lists beyond the Dashboard matter most (Alerts and Manage domains are the likely ones)?
* Should the state also survive a reload of the same tab?
