# Media Import Controller — Endpoint Reference

This controller provides a one-time media import tool that recreates the Umbraco v8 media tree in a new Umbraco v17 site, pointing each node at existing files in S3 without re-uploading anything.

> ⚠️ **Remove this controller once the full import is complete.**

---

## Prerequisites

- `media-export.csv` must be placed in the **project root** (same folder as `appsettings.json`)
- The Umbraco site must be running
- The CSV must be the full export from the v8 database query

---

## Endpoints

### `GET /api/media-import/status`

Returns a summary of the current state without running any import. Use this to verify the CSV is found and check how many nodes have already been imported.

**Example:**
```
https://localhost:44369/api/media-import/status
```

**Example response:**
```json
{
  "csvFound": true,
  "totalRows": 3860,
  "folders": 43,
  "mediaItems": 3817,
  "progressEntries": 150,
  "progressPath": "C:\\...\\media-import-progress.json",
  "logPath": "C:\\...\\media-import-log.txt"
}
```

---

### `GET /api/media-import/reset`

Deletes the progress file so the next run starts fresh. Use this if you need to wipe and re-import from scratch.

> ⚠️ Also manually delete any previously imported nodes from the Umbraco backoffice media section before resetting, to avoid duplicates.

**Example:**
```
https://localhost:44369/api/media-import/reset
```

**Example response:**
```json
{
  "message": "Progress file deleted. Next run will start fresh."
}
```

---

### `GET /api/media-import/run`

Runs the import. Supports dry run mode, row limits, and the ability to skip folders or media items independently.

**Query parameters:**

| Parameter      | Type    | Default | Description |
|----------------|---------|---------|-------------|
| `dryRun`       | bool    | `true`  | When `true`, logs what would be created without making any changes |
| `limit`        | int     | `10`    | Maximum number of rows to process per pass (folders and media counted separately) |
| `skipFolders`  | bool    | `false` | Skip Pass 1 (folder creation) |
| `skipMedia`    | bool    | `false` | Skip Pass 2 (media item creation) |

**Example response:**
```json
{
  "dryRun": false,
  "totalRows": 3860,
  "progressEntries": 43,
  "created": 43,
  "skipped": 0,
  "failed": 0,
  "resumed": 0,
  "log": [
    "[Created ] Folder: 'images' (old=1266 → new=1070)",
    "[Created ] Folder: 'Resource images' (old=1267 → new=1071)"
  ]
}
```

---

## Log Entry Statuses

| Status     | Meaning |
|------------|---------|
| `Created`  | Node successfully created in Umbraco |
| `Resumed`  | Node already existed in progress file — skipped safely |
| `Skipped`  | Node deliberately skipped (e.g. legacy `/media/` path not in S3) |
| `Failed`   | Node creation failed — check the detail message |
| `DryRun`   | Dry run only — shows what would have been created |

---

## Output Files

Both files are written to the project root alongside `media-export.csv`.

| File | Description |
|------|-------------|
| `media-import-progress.json` | Maps old v8 node IDs to new Umbraco v17 node IDs. Enables resume support — safe to stop and restart at any point. Delete this file to start fresh (or use `/reset`). |
| `media-import-log.txt` | Appended on every run including dry runs. Full audit trail of every node processed. |

---

## Recommended Run Sequence

### Step 1 — Verify CSV is loaded
```
GET /api/media-import/status
```
Confirm `csvFound: true` and `totalRows` matches expectations (~3860).

---

### Step 2 — Dry run folders
```
GET /api/media-import/run?dryRun=true&skipMedia=true&limit=43
```
Review the log to confirm folder hierarchy looks correct.

---

### Step 3 — Import all folders (live)
```
GET /api/media-import/run?dryRun=false&skipMedia=true&limit=43
```
Only 43 folders so one run covers them all. Verify the folder tree in the Umbraco backoffice before proceeding.

---

### Step 4 — Dry run first media batch
```
GET /api/media-import/run?dryRun=true&skipFolders=true&limit=20
```
Confirm log shows `{"src":"https://cdn.advent.com/assets/..."}` format for file paths.

---

### Step 5 — Import media in batches (live)
```
GET /api/media-import/run?dryRun=false&skipFolders=true&limit=200
```
Repeat this call until `created` returns `0` and `resumed` equals the total media item count. Progress is saved after every node so it is safe to stop and resume at any point.

---

### Step 6 — Final status check
```
GET /api/media-import/status
```
`progressEntries` should be close to `totalRows` (3860). Any gap indicates failed or skipped rows — check `media-import-log.txt` for details.

---

### Step 7 — Clean up
Once the import is verified:
- Delete `MediaImportController.cs` from the project
- Delete `media-export.csv` from the project root
- Delete `media-import-progress.json` and `media-import-log.txt`
- Remove `CsvHelper` from the `.csproj` if it was added solely for this import

---

## Notes

- The import is **non-destructive** — no S3 files are read, written, moved or deleted
- Running the same batch twice is safe — already-imported nodes are detected via the progress file and skipped with a `Resumed` status
- Dry runs use a copy of the progress state so real progress is never affected
- The `limit` parameter applies **per pass** — folders and media items are counted separately
