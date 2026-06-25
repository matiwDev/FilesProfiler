using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Creatush.FilesProfiler
{
    // -------------------------------------------------------------------------
    // Data types
    // -------------------------------------------------------------------------

    public enum DuplicateMatchType { Exact, Name }

    public class AssetEntry
    {
        public string        AssetPath;
        public string        GUID;
        public string        AbsolutePath;
        public long          SizeBytes;
        public string        MD5;
        public AssetCategory Category;
        public List<string>  ReferencedBy = new();

        public string Name      => Path.GetFileName(AssetPath);
        public string Extension => Path.GetExtension(AssetPath).ToLowerInvariant();
        public bool   IsUnused  => ReferencedBy.Count == 0;

        public string SizeHuman
        {
            get
            {
                double b = SizeBytes;
                foreach (var unit in new[] { "B", "KB", "MB", "GB" })
                {
                    if (b < 1024) return $"{b:F1} {unit}";
                    b /= 1024;
                }
                return $"{b:F1} TB";
            }
        }
    }

    public class DuplicateGroup
    {
        public string             HashValue;
        public DuplicateMatchType MatchType;
        public AssetCategory      Category;
        public List<AssetEntry>   Assets = new();
        public long WastedBytes => Assets.Skip(1).Sum(a => a.SizeBytes);
    }

    public class SwapResult
    {
        public AssetEntry   FromAsset;
        public AssetEntry   ToAsset;
        public List<string> FilesUpdated = new();
        public int          ReplacementsMade;
        public bool         DryRun;
    }

    /// <summary>Full result of a scan pass — may cover one category or all of them.</summary>
    public class ScanResult
    {
        public List<AssetEntry>                          AllAssets = new();
        public Dictionary<AssetCategory, List<AssetEntry>> ByCategory = new();
        public List<AssetEntry>                           Unused = new();
        public List<DuplicateGroup>                        Duplicates = new();
    }

    // -------------------------------------------------------------------------
    // Scanner
    // -------------------------------------------------------------------------

    public static class FilesScanner
    {
        /// <summary>
        /// Discover every asset under the given roots. If categoryFilter is set,
        /// only assets of that category are returned in AllAssets/ByCategory —
        /// but the caller is still responsible for building the reference index
        /// across ALL reference-bearing files for correctness.
        /// </summary>
        public static List<AssetEntry> DiscoverAssets(
            string[] rootFolders, AssetCategory? categoryFilter = null)
        {
            var guids = rootFolders != null && rootFolders.Length > 0
                ? AssetDatabase.FindAssets("", rootFolders)
                : AssetDatabase.FindAssets("");

            var result = new List<AssetEntry>();
            var seen   = new HashSet<string>();

            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || !seen.Add(assetPath)) continue;
                if (AssetDatabase.IsValidFolder(assetPath)) continue;

                var ext = Path.GetExtension(assetPath);
                var def = AssetCategories.Resolve(ext);

                if (categoryFilter.HasValue && def.Category != categoryFilter.Value) continue;

                var abs = Path.GetFullPath(assetPath);
                result.Add(new AssetEntry
                {
                    AssetPath    = assetPath,
                    GUID         = guid,
                    AbsolutePath = abs,
                    SizeBytes    = File.Exists(abs) ? new FileInfo(abs).Length : 0,
                    Category     = def.Category,
                });
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Unused detection
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a reference index by scanning every reference-bearing asset
        /// in scope (materials, prefabs, scenes, controllers, etc.) for GUID
        /// dependencies, then flags entries in `assets` with zero references.
        /// `assets` may be a subset (e.g. just Textures) — the reference scan
        /// itself always covers the full scope for correctness.
        /// </summary>
        public static List<AssetEntry> FindUnused(
            List<AssetEntry> assets,
            string[] rootFolders,
            Action<int, int, string> onProgress = null)
        {
            var map = new Dictionary<string, AssetEntry>();
            foreach (var a in assets)
                if (!map.ContainsKey(a.GUID)) map[a.GUID] = a;

            var refExts = AssetCategories.ReferenceBearingExtensions();

            var allGuids = rootFolders != null && rootFolders.Length > 0
                ? AssetDatabase.FindAssets("", rootFolders)
                : AssetDatabase.FindAssets("");

            // Filter down to reference-bearing files only (perf optimisation)
            var refPaths = new List<string>();
            foreach (var g in allGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (refExts.Contains(Path.GetExtension(p)))
                    refPaths.Add(p);
            }

            int total = refPaths.Count, done = 0;
            foreach (var refPath in refPaths)
            {
                onProgress?.Invoke(done++, total, Path.GetFileName(refPath));

                var deps = AssetDatabase.GetDependencies(refPath, recursive: false);
                foreach (var dep in deps)
                {
                    if (dep == refPath) continue;
                    var depGuid = AssetDatabase.AssetPathToGUID(dep);
                    if (map.TryGetValue(depGuid, out var entry) &&
                        !entry.ReferencedBy.Contains(refPath))
                        entry.ReferencedBy.Add(refPath);
                }
            }

            return assets.Where(a => a.IsUnused).ToList();
        }

        // ------------------------------------------------------------------
        // Duplicate detection
        // ------------------------------------------------------------------

        public static List<DuplicateGroup> FindDuplicates(
            List<AssetEntry> assets,
            DuplicateMatchType mode = DuplicateMatchType.Exact,
            Action<int, int, string> onProgress = null)
        {
            if (mode == DuplicateMatchType.Exact)
            {
                int total = assets.Count, done = 0;
                foreach (var a in assets)
                {
                    onProgress?.Invoke(done++, total, a.Name);
                    a.MD5 = ComputeMD5(a.AbsolutePath);
                }
            }

            Func<AssetEntry, string> keyFn = mode == DuplicateMatchType.Exact
                ? (a => a.MD5)
                : (a => Path.GetFileNameWithoutExtension(a.AssetPath).ToLowerInvariant());

            var buckets = new Dictionary<string, List<AssetEntry>>();
            foreach (var a in assets)
            {
                var k = keyFn(a);
                if (string.IsNullOrEmpty(k)) continue;
                if (!buckets.ContainsKey(k)) buckets[k] = new List<AssetEntry>();
                buckets[k].Add(a);
            }

            return buckets.Values
                .Where(v => v.Count > 1)
                .Select(v => new DuplicateGroup
                {
                    HashValue = keyFn(v[0]),
                    MatchType = mode,
                    Category  = v[0].Category,
                    Assets    = v,
                })
                .ToList();
        }

        // ------------------------------------------------------------------
        // GUID Swap — works for ANY asset type, not just textures
        // ------------------------------------------------------------------

        public static SwapResult SwapGUID(
            AssetEntry fromAsset,
            AssetEntry toAsset,
            bool dryRun = true,
            bool backup = true,
            string[] rootFolders = null,
            Action<int, int, string> onProgress = null)
        {
            var result = new SwapResult { FromAsset = fromAsset, ToAsset = toAsset, DryRun = dryRun };
            if (fromAsset.GUID == toAsset.GUID) return result;

            var refExts = AssetCategories.ReferenceBearingExtensions();
            var allGuids = rootFolders != null && rootFolders.Length > 0
                ? AssetDatabase.FindAssets("", rootFolders)
                : AssetDatabase.FindAssets("");

            var refPaths = new List<string>();
            foreach (var g in allGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (refExts.Contains(Path.GetExtension(p)))
                    refPaths.Add(p);
            }

            int total = refPaths.Count, done = 0;
            foreach (var refPath in refPaths)
            {
                onProgress?.Invoke(done++, total, Path.GetFileName(refPath));

                var abs = Path.GetFullPath(refPath);
                if (!File.Exists(abs)) continue;

                string content;
                try { content = File.ReadAllText(abs); }
                catch { continue; }

                if (!content.Contains(fromAsset.GUID)) continue;

                result.FilesUpdated.Add(refPath);
                result.ReplacementsMade += CountOccurrences(content, fromAsset.GUID);

                if (!dryRun)
                {
                    if (backup) File.Copy(abs, abs + ".bak", overwrite: true);
                    File.WriteAllText(abs, content.Replace(fromAsset.GUID, toAsset.GUID));
                }
            }

            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static string ComputeMD5(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(path);
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        static int CountOccurrences(string text, string pattern)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += pattern.Length;
            }
            return count;
        }
    }
}
