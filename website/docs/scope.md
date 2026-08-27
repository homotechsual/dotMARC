---
sidebar_position: 5
---

# Scope

dotMARC deliberately does not cover everything a DMARC tool could. Out of scope for this build:

- **Forensic (RUF) reports.** Only DMARC aggregate (RUA) reports are ingested and parsed.
- **Push notifications.** No email digests or real-time alerts — dotMARC is a dashboard you check,
  not a system that pages you.
- **Long-term raw-data rollups.** There's no 12-month historical raw-data aggregation job; reports
  are retained and queryable, but no separate rollup pipeline summarizes older data down.

If any of these matter for your use case, they're reasonable things to build on top of dotMARC's
existing data model, but they aren't part of what ships today.
