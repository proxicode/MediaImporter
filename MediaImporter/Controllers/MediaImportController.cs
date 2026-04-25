using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using Umbraco.Cms.Core.Services;

namespace SSCAdvent.Controllers;

/// <summary>
/// One-time media import controller that creates Umbraco media nodes from a CSV export
/// of a v8 site, pointing each node at existing S3 files without re-uploading anything.
/// 
/// Drop media-export.csv into the project root before running.
/// Progress is persisted to media-import-progress.json so runs can be safely resumed.
/// DELETE this controller once the import is complete.
/// </summary>
[ApiController]
[Route("api/media-import")]
public class MediaImportController : ControllerBase
{
    private readonly IMediaService _mediaService;

    private static readonly string CsvPath = Path.Combine(Directory.GetCurrentDirectory(), "media-export-test.csv");
    private static readonly string ProgressPath = Path.Combine(Directory.GetCurrentDirectory(), "media-import-progress.json");
    private static readonly string LogPath = Path.Combine(Directory.GetCurrentDirectory(), "media-import-log.txt");

    // Media type aliases
    private const string AliasFolder = "Folder";
    private const string AliasImage = "Image";
    private const string AliasFile = "File";
    private const string AliasSvg = "VectorGraphics";
    private const string CdnBase = "https://cdn.advent.com";

    public MediaImportController(IMediaService mediaService)
    {
        _mediaService = mediaService;
    }

    // ── Status endpoint ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current progress state without running any import.
    /// GET /api/media-import/status
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var progress = LoadProgress();

        if (!System.IO.File.Exists(CsvPath))
            return Ok(new { csvFound = false, progressEntries = progress.Count });

        var allRows = LoadCsv();
        var folders = allRows.Count(r => r.MediaType == "Folder");
        var mediaItems = allRows.Count(r => r.MediaType != "Folder");

        return Ok(new
        {
            csvFound = true,
            totalRows = allRows.Count,
            folders,
            mediaItems,
            progressEntries = progress.Count,
            progressPath = ProgressPath,
            logPath = LogPath
        });
    }

    // ── Reset endpoint ───────────────────────────────────────────────────────

    /// <summary>
    /// Deletes progress file to allow a fresh import run.
    /// GET /api/media-import/reset
    /// </summary>
    [HttpGet("reset")]
    public IActionResult Reset()
    {
        if (System.IO.File.Exists(ProgressPath))
            System.IO.File.Delete(ProgressPath);

        return Ok(new { message = "Progress file deleted. Next run will start fresh." });
    }

    // ── Main import endpoint ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the media import.
    /// GET /api/media-import/run?dryRun=true&amp;limit=10&amp;skipFolders=false&amp;skipMedia=false
    /// </summary>
    [HttpGet("run")]
    public IActionResult Run(
        [FromQuery] bool dryRun = true,
        [FromQuery] int limit = 10,
        [FromQuery] bool skipFolders = false,
        [FromQuery] bool skipMedia = false)
    {
        if (!System.IO.File.Exists(CsvPath))
            return BadRequest($"CSV not found at: {CsvPath}");

        var allRows = LoadCsv();

        // Load persisted progress: maps old v8 int ID → new Umbraco int ID
        // In dry run we use a separate in-memory map so we don't pollute real progress
        var persistedProgress = LoadProgress();
        var idMap = dryRun
            ? new Dictionary<int, int>(persistedProgress)   // copy for dry run
            : persistedProgress;                             // reference for live run

        var log = new List<string>();
        int created = 0, skipped = 0, failed = 0, resumed = 0;

        void AppendLog(string line)
        {
            log.Add(line);
            // Also append to persistent log file (even in dry run for visibility)
            System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
        }

        // ── Pass 1: Folders ──────────────────────────────────────────────────

        if (!skipFolders)
        {
            var folders = allRows
                .Where(r => r.MediaType == "Folder")
                .Take(limit)
                .ToList();

            foreach (var row in folders)
            {
                // Already imported — skip but keep in idMap for child resolution
                if (idMap.ContainsKey(row.Id))
                {
                    AppendLog($"[Resumed ] Folder: '{row.NodeName}' (old={row.Id} → new={idMap[row.Id]})");
                    resumed++;
                    continue;
                }

                int parentId = ResolveParentId(row.ParentId, idMap);

                if (dryRun)
                {
                    AppendLog($"[DryRun  ] Folder: '{row.NodeName}' under parentId={parentId}");
                    idMap[row.Id] = -999; // placeholder so children resolve
                    continue;
                }

                try
                {
                    var folder = _mediaService.CreateMedia(row.NodeName, parentId, AliasFolder);
                    _mediaService.Save(folder);
                    idMap[row.Id] = folder.Id;
                    SaveProgress(idMap);
                    AppendLog($"[Created ] Folder: '{row.NodeName}' (old={row.Id} → new={folder.Id})");
                    created++;
                }
                catch (Exception ex)
                {
                    AppendLog($"[Failed  ] Folder: '{row.NodeName}' → {ex.Message}");
                    failed++;
                }
            }
        }

        // ── Pass 2: Media items ──────────────────────────────────────────────

        if (!skipMedia)
        {
            var mediaItems = allRows
                .Where(r => r.MediaType != "Folder")
                .Take(limit)
                .ToList();

            foreach (var row in mediaItems)
            {
                // Skip legacy /media/ paths that aren't in S3
                if (row.S3RelativePath?.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    AppendLog($"[Skipped ] '{row.NodeName}' → legacy /media/ path, not in S3");
                    skipped++;
                    continue;
                }

                // Already imported
                if (idMap.ContainsKey(row.Id))
                {
                    AppendLog($"[Resumed ] {row.MediaType}: '{row.NodeName}' (old={row.Id} → new={idMap[row.Id]})");
                    resumed++;
                    continue;
                }

                int parentId = ResolveParentId(row.ParentId, idMap);
                var mediaTypeAlias = ResolveMediaTypeAlias(row.S3RelativePath, row.MediaType);
                var filePath = "/" + row.S3RelativePath?.TrimStart('/');
                var fileJson = JsonSerializer.Serialize(new { src = $"{CdnBase}{filePath}" });

                if (dryRun)
                {
                    //AppendLog($"[DryRun  ] {mediaTypeAlias}: '{row.NodeName}' → {filePath} under parentId={parentId}");
                    AppendLog($"[DryRun  ] {mediaTypeAlias}: '{row.NodeName}' → {fileJson} under parentId={parentId}");
                    idMap[row.Id] = -999;
                    continue;
                }

                try
                {
                    var media = _mediaService.CreateMedia(row.NodeName, parentId, mediaTypeAlias);
                    media.SetValue("umbracoFile", fileJson);
                    _mediaService.Save(media);
                    idMap[row.Id] = media.Id;
                    SaveProgress(idMap);
                    AppendLog($"[Created ] {mediaTypeAlias}: '{row.NodeName}' → {filePath} (old={row.Id} → new={media.Id})");
                    created++;
                }
                catch (Exception ex)
                {
                    AppendLog($"[Failed  ] '{row.NodeName}' → {ex.Message}");
                    failed++;
                }
            }
        }

        return Ok(new
        {
            dryRun,
            totalRows = allRows.Count,
            progressEntries = idMap.Count(e => e.Value != -999),
            created,
            skipped,
            failed,
            resumed,
            log
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int ResolveParentId(int oldParentId, Dictionary<int, int> idMap)
    {
        if (oldParentId == -1) return -1;
        return idMap.TryGetValue(oldParentId, out var newId) ? newId : -1;
    }

    private static string ResolveMediaTypeAlias(string? s3Path, string csvMediaType)
    {
        if (csvMediaType == "Folder") return AliasFolder;
        var ext = Path.GetExtension(s3Path ?? "").ToLowerInvariant().TrimStart('.');
        return ext switch
        {
            "svg" => AliasSvg,
            "jpg" or "jpeg" or "jfif" or "png"
                or "gif" or "webp" => AliasImage,
            _ => AliasFile
        };
    }

    private static List<MediaRow> LoadCsv()
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            MissingFieldFound = null,
            BadDataFound = null
        };

        using var reader = new StreamReader(CsvPath);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<MediaRow>().ToList();
    }

    private static Dictionary<int, int> LoadProgress()
    {
        if (!System.IO.File.Exists(ProgressPath))
            return [];

        try
        {
            var json = System.IO.File.ReadAllText(ProgressPath);
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveProgress(Dictionary<int, int> idMap)
    {
        // Only persist real mappings, not dry-run placeholders
        var toSave = idMap.Where(e => e.Value != -999)
                          .ToDictionary(e => e.Key, e => e.Value);
        var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(ProgressPath, json);
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class MediaRow
{
    [CsvHelper.Configuration.Attributes.Index(0)] public int Id { get; set; }
    [CsvHelper.Configuration.Attributes.Index(1)] public int ParentId { get; set; }
    [CsvHelper.Configuration.Attributes.Index(2)] public int SortOrder { get; set; }
    [CsvHelper.Configuration.Attributes.Index(3)] public string NodeName { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Index(4)] public string NodePath { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Index(5)] public string FullFolderPath { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Index(6)] public string CreateDate { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Index(7)] public string MediaType { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Index(8)] public string? FilePathJson { get; set; }
    [CsvHelper.Configuration.Attributes.Index(9)] public string? S3RelativePath { get; set; }
}