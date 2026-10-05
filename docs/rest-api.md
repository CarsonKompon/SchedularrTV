# REST API

The web UI is the main way to use SchedularrTV, but the same server exposes a REST API under `/api`.

Read endpoints are open. Mutating endpoints (🔒) require an `X-Api-Key` header and are **disabled** when
`Scheduler:ApiKey` is unset. Set it in `settings.json`:

```jsonc
"Scheduler": { "ApiKey": "a-long-random-string" }
```

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/health` | Status, last build time, config errors and warnings. |
| `GET` | `/api/config` | Raw YAML, parsed object model and validation. |
| `POST` | `/api/config/validate` | Validate YAML without saving. |
| `PUT` | `/api/config` | Validate and atomically save. 🔒 |
| `POST` | `/api/config/serialize` | Object model → YAML. |
| `POST` | `/api/config/reload` | Re-read from disk. 🔒 |
| `GET` | `/api/config/backups` | Earlier versions of the YAML, newest first (one is kept before every change). |
| `GET` | `/api/config/backups/{id}` | One backup's details and YAML. |
| `POST` | `/api/config/backups/{id}/restore` | Write a backup back as the schedule (what it replaces is backed up first). 🔒 |
| `GET` | `/api/library/search?query=` | Run a query against the library. |
| `POST` | `/api/library/query/parse` | Validate a query and return its AST. |
| `POST` | `/api/library/refresh` | Drop the Jellyfin snapshot. 🔒 |
| `GET` | `/api/schedules` | Latest published schedules. |
| `GET` | `/api/schedules/{channelId}` | One channel. |
| `POST` | `/api/rebuild` | Force a rebuild. 🔒 |
| `POST` | `/api/preview` | Dry-run a channel from ad-hoc YAML; never publishes or advances rotation state. |
| `GET` | `/api/metadata` | Enum values, query fields and operators, holidays, time zones. |
| `POST` | `/api/channels/{channelId}/reset-rotation` | Reset a channel's playback positions and rebuild. 🔒 |
| `GET` | `/api/generated-promos/files/{path}` | A rendered promo from the cache. |
