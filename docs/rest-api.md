# REST API

The web UI is the main way to use SchedularrTV, but the same server also exposes a REST API under /api for scripts and other tools.

## Authentication

- Read endpoints are open.
- Mutating endpoints (marked **Yes** under **Key** below) require an `X-Api-Key` header.

> [!IMPORTANT]
> Mutating endpoints are **disabled** when `Scheduler:ApiKey` is unset.

Set the key in `settings.json`:

```jsonc
"Scheduler": { "ApiKey": "a-long-random-string" }
```

Then send it with each mutating request, for example:

```http
POST /api/rebuild
X-Api-Key: a-long-random-string
```

## Endpoints

### Status and metadata

| Method | Route | Purpose | Key |
| --- | --- | --- | --- |
| `GET` | `/api/health` | Status, last build time, config errors and warnings. | |
| `GET` | `/api/metadata` | Enum values, query fields and operators, holidays, time zones. | |

### Configuration

| Method | Route | Purpose | Key |
| --- | --- | --- | --- |
| `GET` | `/api/config` | Raw YAML, parsed object model and validation. | |
| `POST` | `/api/config/validate` | Validate YAML without saving. | |
| `PUT` | `/api/config` | Validate and atomically save. | Yes |
| `POST` | `/api/config/serialize` | Object model → YAML. | |
| `POST` | `/api/config/reload` | Re-read from disk. | Yes |
| `GET` | `/api/config/backups` | Earlier versions of the YAML, newest first (one is kept before every change). | |
| `GET` | `/api/config/backups/{id}` | One backup's details and YAML. | |
| `POST` | `/api/config/backups/{id}/restore` | Write a backup back as the schedule (what it replaces is backed up first). | Yes |

### Library

| Method | Route | Purpose | Key |
| --- | --- | --- | --- |
| `GET` | `/api/library/search?query=` | Run a query against the library. See [Query language](scheduling.md#query-language). | |
| `POST` | `/api/library/query/parse` | Validate a query and return its AST. | |
| `POST` | `/api/library/refresh` | Drop the Jellyfin snapshot. | Yes |

### Schedules and channels

| Method | Route | Purpose | Key |
| --- | --- | --- | --- |
| `GET` | `/api/schedules` | Latest published schedules. | |
| `GET` | `/api/schedules/{channelId}` | One channel. | |
| `POST` | `/api/rebuild` | Force a rebuild. | Yes |
| `POST` | `/api/preview` | Dry-run a channel from ad-hoc YAML; never publishes or advances rotation state. | |
| `POST` | `/api/channels/{channelId}/reset-rotation` | Reset a channel's playback positions and rebuild. | Yes |

### Generated promos

| Method | Route | Purpose | Key |
| --- | --- | --- | --- |
| `GET` | `/api/generated-promos/files/{path}` | A rendered promo from the cache. | |
