# REST API

The API uses **`http://localhost:5252/api`** by default. JSON request bodies require **`Content-Type: application/json`**; schedule timestamps include UTC offsets and durations are reported in seconds.

## Authentication

Read, validation, serialization, and preview routes need no key. Protected write routes require **`X-Api-Key`** matching **`Scheduler:ApiKey`**.

Merge this section into **`settings.json`**, then restart:

```json
{
  "Scheduler": {
    "ApiKey": "replace-with-a-long-random-secret"
  }
}
```

| Response | Meaning |
| --- | --- |
| **401** | Missing or incorrect key on a protected route |
| **403** | Protected route disabled because no server key is configured |
| **400** | Invalid save, malformed search, or rejected preview |
| **404** | Requested channel, backup, or artwork not found |

Validation routes can return **200** with **`isValid: false`** or **`valid: false`**; inspect the body. The key protects API write routes, not access to the Web UI.

## Core endpoints

### Status and metadata

| Method | Route | Result |
| --- | --- | --- |
| **GET** | `/api/health` | Configuration status, instance ID, last build time, channel count, errors, warnings |
| **GET** | `/api/performance` | Latest resource sample, advice, GPU problem, 24/7 channel status |
| **GET** | `/api/metadata` | Enum values, query fields/operators, holidays, promo windows, time zones |

### Configuration

| Method | Route | Body | Key | Result |
| --- | --- | --- | --- | --- |
| **GET** | `/api/config` | — | No | Paths, raw YAML, parsed model, validation |
| **POST** | `/api/config/validate` | `{"yaml":"..."}` | No | Validation without saving |
| **PUT** | `/api/config` | `{"yaml":"..."}` | Yes | Validated atomic save |
| **POST** | `/api/config/serialize` | Scheduler object model | No | YAML text (`text/yaml`) |
| **POST** | `/api/config/reload` | — | Yes | Reload and validation |
| **GET** | `/api/config/backups` | — | No | Backup list, newest first |
| **GET** | `/api/config/backups/{id}` | — | No | Backup metadata and YAML |
| **POST** | `/api/config/backups/{id}/restore` | — | Yes | Restore, with a backup of the replaced version |

### Library

| Method | Route | Input | Key | Result |
| --- | --- | --- | --- | --- |
| **GET** | `/api/library/search` | `query`, optional `limit` | No | `total`, `normalizedQuery`, `items` |
| **POST** | `/api/library/query/parse` | `{"query":"..."}` | No | Validity, normalized query, AST or error |
| **POST** | `/api/library/refresh` | — | Yes | Refresh enabled library sources |
| **GET** | `/api/library/artwork/{id}` | Optional `type`, `w` | No | Image from the owning media source |

Search defaults to **100** results and clamps **`limit`** to **1–1000**. Artwork width defaults to **300** and clamps to **32–1280**.

### Schedules

| Method | Route | Input | Key | Result |
| --- | --- | --- | --- | --- |
| **GET** | `/api/schedules` | — | No | Latest coordinator schedules |
| **GET** | `/api/schedules/{channelId}` | — | No | One channel schedule |
| **POST** | `/api/rebuild` | Optional `publish` query parameter | Yes | Replanned schedules; publishes by default |
| **POST** | `/api/preview` | Preview request | No | Dry-run schedule |
| **POST** | `/api/channels/{channelId}/reset-rotation` | — | Yes | Reset playback positions and rebuild |

**`publish=false`** suppresses output publication; use **`/api/preview`** for a dry run that does not advance rotation state.

## Validate and save YAML

Use **PowerShell** from a directory containing **`schedule.yaml`**:

```powershell
$base = 'http://localhost:5252'
$headers = @{ 'X-Api-Key' = $env:SCHEDULARRTV_API_KEY }
$body = @{ yaml = Get-Content -Raw -LiteralPath './schedule.yaml' } | ConvertTo-Json

$validation = Invoke-RestMethod -Method Post -Uri "$base/api/config/validate" `
    -ContentType 'application/json' -Body $body
if (-not $validation.isValid) {
    throw ($validation.errors -join [Environment]::NewLine)
}

Invoke-RestMethod -Method Put -Uri "$base/api/config" `
    -Headers $headers -ContentType 'application/json' -Body $body
```

Set **`SCHEDULARRTV_API_KEY`** in the calling shell to the configured key. It is an example client variable, not the server's configuration key.

## Preview a channel

```http
POST /api/preview
Content-Type: application/json

{
  "channelId": "movies",
  "start": "2026-10-11T18:00:00Z",
  "hours": 6
}
```

| Field | Default |
| --- | --- |
| **`channelId`** | Required existing ID in the selected configuration |
| **`yaml`** | Current configuration when omitted or blank |
| **`start`** | Current UTC time |
| **`hours`** | `24`, clamped to `0.25–168` |

Preview returns channel metadata, window boundaries, **`paddingSeconds`**, **`entries`**, and **`diagnostics`**. Entries include start/end, kind, title, item ID, source in-point, trimming flags, and segment number/count.

## Search the library

```powershell
$query = [Uri]::EscapeDataString("kind == 'Movie'")
Invoke-RestMethod "http://localhost:5252/api/library/search?query=$query&limit=20"
```

Validate a query without searching:

```http
POST /api/library/query/parse
Content-Type: application/json

{"query":"show:'Clone High' AND season >= 1"}
```

See [Query language](scheduling.md#query-language) for syntax.

## Rebuild or restore

```powershell
$base = 'http://localhost:5252'
$headers = @{ 'X-Api-Key' = $env:SCHEDULARRTV_API_KEY }
Invoke-RestMethod -Method Post -Uri "$base/api/rebuild" -Headers $headers
Invoke-RestMethod -Uri "$base/api/config/backups"
```

Restore a selected backup ID with **`POST /api/config/backups/{id}/restore`**. Restore also reloads the Web UI editor, replacing its working schedule.

## Plugin and internal routes

Enabled plugins can add endpoints outside the core table. **Generated promos** serves cached files through **`GET /api/generated-promos/files/{path}`**; live plugins expose their stream addresses in their guides.

**`POST /api/app/shutdown`** is launcher control, requiring the internal control token rather than **`X-Api-Key`**. The [endpoint implementation](../src/SchedularrTV.Service/ApiEndpoints.cs) is the reference for core request and response models.
