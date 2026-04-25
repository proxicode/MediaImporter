# Media Import Controller — Endpoint Reference

This controller provides a one-time media import tool that recreates the Umbraco v8 media tree in a new Umbraco v17 site, pointing each node at existing files in S3 without re-uploading anything.

> ⚠️ **Remove this controller once the full import is complete.**

---

## Background & Use Case

This tool was written to solve a specific migration problem: the v8 media library already existed in S3, and the goal was to recreate the Umbraco database entries to point at those files — **without re-uploading anything and without creating duplicates**.

Rather than treating files as the source of truth and importing them through Umbraco's normal media pipeline (which would upload each file again and generate new derivatives), this tool writes media nodes directly via `IMediaService`, setting the `umbracoFile` property to a JSON blob that references the existing CDN URL. The files on S3 are never touched.

**This approach is not a general-purpose media importer.** If your use case involves:
- Uploading new files that don't already exist on S3
- Importing from a local file system rather than a CDN
- A straightforward v8-to-v13+ migration without a custom CDN setup

...then this tool will likely need modification before it fits your workflow. The core resumable-import pattern (progress JSON, dry run mode, per-pass limits) is reusable, but the file path handling and `umbracoFile` JSON format are specific to this CDN-backed S3 setup.

---

## Prerequisites

- `media-export.csv` must be placed in the **project root** (same folder as `appsettings.json`)
- The Umbraco site must be running
- The CSV must be the full export from the v8 database query (see [Generating the CSV](#generating-the-csv) below)

---

## Generating the CSV

The file `v8-media-export-sql.txt` contains a SQL query to run against your **Umbraco v8 SQL Server database**. It walks the full media tree using a recursive CTE and joins property data to extract each node's CDN file path.

### Steps

1. Open SQL Server Management Studio (or Azure Data Studio) and connect to your v8 database.
2. Open `v8-media-export-sql.txt` and update the two occurrences of `https://cdn.example.com/` to match your actual CDN base URL — this strips the CDN prefix so only the S3-relative path is stored in the CSV.
3. Run the query.
4. Export the results as CSV **without a header row** — in SSMS: right-click the results grid → **Save Results As…** → set *Save as type* to `CSV`.
5. Name the file `media-export.csv` and place it in the project root alongside `appsettings.json`.

### Expected columns (in order)

| # | Column | Description |
|---|--------|-------------|
| 0 | `id` | v8 node ID |
| 1 | `parentId` | v8 parent node ID (`-1` for root) |
| 2 | `sortOrder` | Node sort order |
| 3 | `nodeName` | Display name |
| 4 | `nodePath` | Umbraco path string (e.g. `-1,1050,1266`) |
| 5 | `fullFolderPath` | Human-readable folder path |
| 6 | `createDate` | Node creation date |
| 7 | `mediaType` | `Folder`, `Image`, `File`, or `VectorGraphics` |
| 8 | `filePathJson` | Raw JSON value from `umbracoFile` property |
| 9 | `s3RelativePath` | CDN-prefix-stripped file path used to build the new media URL |

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
Confirm log shows `{"src":"https://cdn.example.com/assets/..."}` format for file paths.

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
