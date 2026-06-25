using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Creatush.FilesProfiler
{
    public class FilesProfilerWindow : EditorWindow
    {
        [MenuItem("Tools/Creatush/Files Profiler")]
        public static void Open() =>
            GetWindow<FilesProfilerWindow>("Files Profiler").minSize = new Vector2(780, 520);

        // ── Top-level mode ───────────────────────────────────────────────────
        enum MainView { Unused, Duplicates, Swap }
        MainView _mainView = MainView.Unused;

        AssetCategory? _activeCategory = null;   // null = "All"

        // Scope
        bool         _wholeProject = true;
        string       _customFolder = "Assets";
        DefaultAsset _folderObject;

        // ── Scan state (flat lists; filtered per-view by category) ──────────
        List<AssetEntry>     _assets   = new();
        List<AssetEntry>     _unused   = new();
        List<DuplicateGroup> _dupes    = new();
        HashSet<AssetCategory> _scannedCategories = new();
        bool                 _scanning;
        string               _statusMsg = "Press Scan to begin.";
        float                _progress;
        string               _progressLabel;
        DuplicateMatchType   _dupesMode = DuplicateMatchType.Exact;

        // Unused view filters
        Vector2      _unusedScroll;
        string       _unusedSearch = "";
        string       _unusedExtFilter = "All";
        HashSet<int> _unusedSelected = new();
        bool         _unusedShowPath = true;

        // Duplicates view filters
        Vector2         _dupesScroll;
        string          _dupesSearch = "";
        HashSet<string> _dupesCollapsed = new();
        bool            _dupesAutoDelete = false;
        bool            _dupesBackup    = true;
        Dictionary<string, HashSet<string>> _dupesGroupSelection = new(); // groupHash -> selected asset GUIDs to merge

        // ── Swap tab ──────────────────────────────────────────────────────────
        enum SwapRole { None, From, To }
        class SwapPickerEntry { public AssetEntry Asset; public SwapRole Role = SwapRole.None; }

        List<SwapPickerEntry> _swapList = new();
        SwapResult            _swapPreview;
        bool                  _swapDryRun = true;
        bool                  _swapBackup = true;
        Vector2               _swapPickerScroll;
        Vector2               _swapResultScroll;
        string                _swapSearch = "";

        AssetEntry SwapFrom => _swapList.FirstOrDefault(e => e.Role == SwapRole.From)?.Asset;
        AssetEntry SwapTo   => _swapList.FirstOrDefault(e => e.Role == SwapRole.To)?.Asset;

        // ── Styles ────────────────────────────────────────────────────────────
        GUIStyle _styleHeader, _styleSubLabel, _styleTag, _styleRowEven, _styleRowOdd,
                 _styleSectionTitle, _styleMonoSmall, _styleWarning, _styleSuccess;
        bool _stylesInit;

        void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _styleHeader = new GUIStyle(EditorStyles.boldLabel)
                { fontSize = 13, normal = { textColor = new Color(0.85f, 0.75f, 1f) } };

            _styleSectionTitle = new GUIStyle(EditorStyles.miniLabel)
                { fontSize = 10, fontStyle = FontStyle.Bold,
                  normal   = { textColor = new Color(0.5f, 0.5f, 0.6f) } };

            _styleSubLabel = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.55f, 0.55f, 0.6f) } };

            _styleTag = new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                  normal    = { textColor = Color.white },
                  padding   = new RectOffset(6, 6, 2, 2) };

            _styleMonoSmall = new GUIStyle(EditorStyles.miniLabel)
                { font   = (Font)EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf"),
                  normal = { textColor = new Color(0.5f, 0.45f, 0.75f) } };

            _styleWarning = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(1f, 0.75f, 0.2f) }, fontStyle = FontStyle.Bold };

            _styleSuccess = new GUIStyle(EditorStyles.miniLabel)
                { normal = { textColor = new Color(0.4f, 0.9f, 0.5f) }, fontStyle = FontStyle.Bold };

            var evenTex = MakeTex(new Color(0.22f, 0.22f, 0.24f, 0.4f));
            var oddTex  = MakeTex(new Color(0.18f, 0.18f, 0.20f, 0.4f));
            _styleRowEven = new GUIStyle { normal = { background = evenTex } };
            _styleRowOdd  = new GUIStyle { normal = { background = oddTex } };
        }

        static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        const float ROW_H = 22f;
        const float COL_NAME = 220f;
        const float COL_SIZE = 70f;
        const float COL_EXT = 56f;
        const float COL_REFS = 45f;
        const float COL_ROLE = 72f;

        // ── OnGUI ─────────────────────────────────────────────────────────────
        void OnGUI()
        {
            InitStyles();
            DrawTopBar();
            DrawCategoryAndViewBar();
            DrawScopeBar();
            DrawProgressBar();

            switch (_mainView)
            {
                case MainView.Unused:     DrawUnused(IsActiveCategoryScanned()); break;
                case MainView.Duplicates: DrawDuplicates(IsActiveCategoryScanned()); break;
                case MainView.Swap:       DrawSwap(); break;
            }

            DrawStatusBar();
        }

        bool IsActiveCategoryScanned() =>
            _activeCategory.HasValue
                ? _scannedCategories.Contains(_activeCategory.Value)
                : _scannedCategories.Count > 0;

        // ── Top bar ───────────────────────────────────────────────────────────
        void DrawTopBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("⬡  Files Profiler", _styleHeader, GUILayout.Width(160));
                GUILayout.FlexibleSpace();

                GUI.enabled = !_scanning;
                string tabLabel = _activeCategory.HasValue
                    ? AssetCategories.Get(_activeCategory.Value).DisplayName
                    : "All";
                if (GUILayout.Button($"▶  Scan \"{tabLabel}\"", EditorStyles.toolbarButton, GUILayout.Width(120)))
                    RunScan(_activeCategory);

                if (GUILayout.Button("▶▶  Scan All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                    RunScan(null);
                GUI.enabled = true;
            }
        }

        // ── Category dropdown + Unused / Duplicates / Swap on the same row ────
        void DrawCategoryAndViewBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Category:", GUILayout.Width(56));

                var labels = new List<string> { FormatCategoryLabel(null, "All") };
                labels.AddRange(AssetCategories.All.Select(d => FormatCategoryLabel(d.Category, $"{d.Icon} {d.DisplayName}")));

                int cur = _activeCategory.HasValue
                    ? AssetCategories.All.FindIndex(d => d.Category == _activeCategory.Value) + 1
                    : 0;
                int newCur = EditorGUILayout.Popup(cur, labels.ToArray(),
                    EditorStyles.toolbarPopup, GUILayout.Width(150));
                _activeCategory = newCur == 0 ? null : AssetCategories.All[newCur - 1].Category;

                GUILayout.Space(10);

                _mainView = (MainView)GUILayout.Toolbar((int)_mainView,
                    new[] { "Unused", "Duplicates", "⇄ GUID Swap" }, GUILayout.Width(260));

                GUILayout.FlexibleSpace();

                if (_mainView == MainView.Duplicates)
                {
                    GUILayout.Label("Match:", GUILayout.Width(42));
                    _dupesMode = (DuplicateMatchType)EditorGUILayout.EnumPopup(
                        _dupesMode, GUILayout.Width(90));
                }
            }
        }

        string FormatCategoryLabel(AssetCategory? cat, string baseLabel)
        {
            int unusedCount = cat.HasValue ? _unused.Count(a => a.Category == cat.Value) : _unused.Count;
            return unusedCount > 0 ? $"{baseLabel} ({unusedCount})" : baseLabel;
        }

        // ── Scope bar ─────────────────────────────────────────────────────────
        void DrawScopeBar()
        {
            using var h = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);
            GUILayout.Label("Scope:", GUILayout.Width(44));
            _wholeProject = GUILayout.Toggle(_wholeProject, "Whole project", GUILayout.Width(110));
            _wholeProject = !GUILayout.Toggle(!_wholeProject, "Custom folder:", GUILayout.Width(110));

            GUI.enabled = !_wholeProject;
            var picked = (DefaultAsset)EditorGUILayout.ObjectField(
                _folderObject, typeof(DefaultAsset), false, GUILayout.Width(180));
            if (picked != _folderObject)
            {
                _folderObject = picked;
                if (picked != null) _customFolder = AssetDatabase.GetAssetPath(picked);
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
        }

        void DrawProgressBar()
        {
            if (!_scanning) return;
            var r = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(r, _progress, _progressLabel ?? "Working…");
        }

        void DrawStatusBar()
        {
            using var h = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            GUILayout.Label(_statusMsg, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                $"{_assets.Count} files indexed   {_unused.Count} unused   {_dupes.Count} dupe groups   " +
                $"{_scannedCategories.Count}/{AssetCategories.All.Count} categories scanned",
                EditorStyles.miniLabel);
        }

        // =====================================================================
        // UNUSED VIEW
        // =====================================================================
        void DrawUnused(bool categoryScanned)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Search:", GUILayout.Width(46));
                _unusedSearch = EditorGUILayout.TextField(_unusedSearch,
                    EditorStyles.toolbarSearchField, GUILayout.Width(200));

                GUILayout.Space(8);
                if (!_activeCategory.HasValue)
                {
                    GUILayout.Label("Category:", GUILayout.Width(56));
                    var catNames = new[] { "All" }.Concat(AssetCategories.All.Select(d => d.DisplayName)).ToArray();
                    int cur = _unusedExtFilter == "All" ? 0 :
                        Array.IndexOf(catNames, _unusedExtFilter);
                    cur = EditorGUILayout.Popup(cur < 0 ? 0 : cur, catNames,
                        EditorStyles.toolbarPopup, GUILayout.Width(90));
                    _unusedExtFilter = catNames[cur];
                }

                GUILayout.FlexibleSpace();
                _unusedShowPath = GUILayout.Toggle(_unusedShowPath, "Show path", EditorStyles.toolbarButton);

                GUI.enabled = _unusedSelected.Count > 0;
                if (GUILayout.Button($"Delete selected ({_unusedSelected.Count})", EditorStyles.toolbarButton))
                    ConfirmAndDeleteSelected();
                GUI.enabled = true;
            }

            if (!categoryScanned)
            {
                DrawEmptyState("Not scanned yet. Press \"Scan\" above to detect unused files.");
                return;
            }

            var filtered = FilteredUnused();
            if (filtered.Count == 0)
            {
                DrawEmptyState(GetScopedUnused().Count == 0
                    ? "✓  No unused files found in this scope."
                    : "No files match the current filter.");
                return;
            }

            var cols = !_activeCategory.HasValue
                ? new[] { ("", 24f), ("File", COL_NAME), ("Size", COL_SIZE),
                          ("Category", 90f), ("Status", COL_REFS + 10),
                          (_unusedShowPath ? "Path" : "", 0f) }
                : new[] { ("", 24f), ("File", COL_NAME), ("Size", COL_SIZE),
                          ("Ext", COL_EXT), ("Status", COL_REFS + 10),
                          (_unusedShowPath ? "Path" : "", 0f) };
            DrawColumnHeader(cols);

            _unusedScroll = EditorGUILayout.BeginScrollView(_unusedScroll);
            for (int i = 0; i < filtered.Count; i++)
            {
                var a = filtered[i];
                bool sel = _unusedSelected.Contains(i);

                using var row = new EditorGUILayout.HorizontalScope(
                    i % 2 == 0 ? _styleRowEven : _styleRowOdd, GUILayout.Height(ROW_H));

                bool newSel = EditorGUILayout.Toggle(sel, GUILayout.Width(24));
                if (newSel != sel) { if (newSel) _unusedSelected.Add(i); else _unusedSelected.Remove(i); }

                var icon = AssetDatabase.GetCachedIcon(a.AssetPath);
                if (GUILayout.Button(new GUIContent(a.Name, icon, a.AssetPath),
                    EditorStyles.label, GUILayout.Width(COL_NAME), GUILayout.Height(ROW_H)))
                    PingAsset(a.AssetPath);

                GUILayout.Label(a.SizeHuman, _styleSubLabel, GUILayout.Width(COL_SIZE));

                if (!_activeCategory.HasValue)
                    DrawTag(AssetCategories.Get(a.Category).DisplayName, TagColor.Purple, 90);
                else
                    DrawTag(a.Extension.TrimStart('.').ToUpper(), TagColor.Purple, COL_EXT);

                DrawTag("unused", TagColor.Red, COL_REFS + 10);

                if (_unusedShowPath) GUILayout.Label(a.AssetPath, _styleSubLabel);
            }
            EditorGUILayout.EndScrollView();

            using var foot = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);
            long total = filtered.Sum(a => a.SizeBytes);
            long selectedTotal = _unusedSelected.Where(i => i < filtered.Count).Sum(i => filtered[i].SizeBytes);

            GUILayout.Label($"{filtered.Count} unused files  ·  {HumanSize(total)} reclaimable", _styleSubLabel);
            GUILayout.FlexibleSpace();

            if (_unusedSelected.Count > 0)
                GUILayout.Label($"{_unusedSelected.Count} selected  ·  {HumanSize(selectedTotal)}",
                    _styleWarning, GUILayout.Width(160));

            if (GUILayout.Button("Select All", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                _unusedSelected.Clear();
                for (int i = 0; i < filtered.Count; i++) _unusedSelected.Add(i);
            }
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50))) _unusedSelected.Clear();

            GUI.enabled = _unusedSelected.Count > 0;
            GUI.color = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button($"Remove Selected ({_unusedSelected.Count})", EditorStyles.miniButton, GUILayout.Width(140)))
                ConfirmAndDeleteSelected();
            GUI.color = Color.white;
            GUI.enabled = true;
        }

        List<AssetEntry> GetScopedUnused() =>
            _activeCategory.HasValue ? _unused.Where(a => a.Category == _activeCategory.Value).ToList() : _unused;

        List<AssetEntry> FilteredUnused()
        {
            var scoped = GetScopedUnused();
            return scoped.Where(a =>
                (string.IsNullOrEmpty(_unusedSearch) ||
                 a.Name.IndexOf(_unusedSearch, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (!_activeCategory.HasValue && _unusedExtFilter != "All"
                    ? AssetCategories.Get(a.Category).DisplayName == _unusedExtFilter
                    : true)
            ).ToList();
        }

        void ConfirmAndDeleteSelected()
        {
            var filtered = FilteredUnused();
            var toDelete = _unusedSelected.Where(i => i < filtered.Count).Select(i => filtered[i]).ToList();
            if (toDelete.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Delete Unused Files",
                $"Permanently delete {toDelete.Count} file(s) and their .meta files?\n\nThis cannot be undone.",
                "Delete", "Cancel")) return;

            int deleted = 0;
            foreach (var a in toDelete)
                if (AssetDatabase.DeleteAsset(a.AssetPath)) deleted++;

            AssetDatabase.Refresh();
            _unusedSelected.Clear();
            _statusMsg = $"Deleted {deleted} file(s).";
            RunScan(_activeCategory);
        }

        // =====================================================================
        // DUPLICATES VIEW
        // =====================================================================
        void DrawDuplicates(bool categoryScanned)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Search:", GUILayout.Width(46));
                _dupesSearch = EditorGUILayout.TextField(_dupesSearch,
                    EditorStyles.toolbarSearchField, GUILayout.Width(200));

                GUILayout.Space(10);
                _dupesAutoDelete = GUILayout.Toggle(_dupesAutoDelete,
                    "Auto-remove", EditorStyles.toolbarButton, GUILayout.Width(190));
                _dupesBackup = GUILayout.Toggle(_dupesBackup,
                    "Backup (.bak)", EditorStyles.toolbarButton, GUILayout.Width(150));

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Expand All", EditorStyles.toolbarButton)) _dupesCollapsed.Clear();
                if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton))
                    foreach (var g in _dupes) _dupesCollapsed.Add(g.HashValue);
            }

            if (_dupesAutoDelete)
                EditorGUILayout.HelpBox(
                    "Auto-remove is ON: after a merge completes, every duplicate redirected to the kept " +
                    "file will be permanently deleted (its .meta included). This cannot be undone.",
                    MessageType.Warning);

            if (!categoryScanned) { DrawEmptyState("Not scanned yet. Press \"Scan\" above to find duplicates."); return; }

            var scoped = _activeCategory.HasValue
                ? _dupes.Where(g => g.Category == _activeCategory.Value).ToList()
                : _dupes;

            if (scoped.Count == 0) { DrawEmptyState($"✓  No duplicates found ({_dupesMode} mode)."); return; }

            var filtered = scoped.Where(g =>
                string.IsNullOrEmpty(_dupesSearch) ||
                g.Assets.Any(a => a.Name.IndexOf(_dupesSearch, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            _dupesScroll = EditorGUILayout.BeginScrollView(_dupesScroll);
            for (int gi = 0; gi < filtered.Count; gi++)
            {
                var grp = filtered[gi];
                bool collapsed = _dupesCollapsed.Contains(grp.HashValue);
                var selection = GetGroupSelection(grp);
                int selectedCount = selection.Count;

                using (new EditorGUILayout.HorizontalScope(
                    new GUIStyle { normal = { background = MakeTex(new Color(0.18f,0.14f,0.26f,0.7f)) } }))
                {
                    if (GUILayout.Button(collapsed ? "▶" : "▼", EditorStyles.miniButton, GUILayout.Width(20)))
                    {
                        if (collapsed) _dupesCollapsed.Remove(grp.HashValue);
                        else           _dupesCollapsed.Add(grp.HashValue);
                    }

                    GUILayout.Label($"Group {gi+1}", _styleHeader, GUILayout.Width(70));
                    DrawTag(grp.MatchType == DuplicateMatchType.Exact ? "exact" : "name", TagColor.Purple, 50);
                    if (!_activeCategory.HasValue)
                        DrawTag(AssetCategories.Get(grp.Category).DisplayName, TagColor.Amber, 80);
                    GUILayout.Label($"{grp.Assets.Count} files  ·  {HumanSize(grp.WastedBytes)} wasted", _styleSubLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"key: {grp.HashValue[..Math.Min(16, grp.HashValue.Length)]}…", _styleMonoSmall);

                    GUI.enabled = selectedCount > 0;
                    GUI.color = new Color(0.85f, 0.6f, 1f);
                    if (GUILayout.Button($"Merge Selected ({selectedCount}) →", EditorStyles.miniButton, GUILayout.Width(140)))
                        MergeGroup(grp, selection);
                    GUI.color = Color.white;
                    GUI.enabled = true;
                }

                if (!collapsed)
                {
                    DrawColumnHeader(new[]
                    {
                        ("", 8f), ("Merge", 42f), ("File", COL_NAME),
                        ("Size", COL_SIZE), ("GUID", 220f), ("Action", 160f)
                    });

                    for (int ti = 0; ti < grp.Assets.Count; ti++)
                    {
                        var a = grp.Assets[ti];
                        bool keep = ti == 0;

                        using var row = new EditorGUILayout.HorizontalScope(
                            ti % 2 == 0 ? _styleRowEven : _styleRowOdd, GUILayout.Height(ROW_H));

                        GUILayout.Space(8);

                        if (keep)
                        {
                            GUILayout.Label("—", _styleSubLabel, GUILayout.Width(42));
                        }
                        else
                        {
                            bool isSel = selection.Contains(a.GUID);
                            bool newSel = EditorGUILayout.Toggle(isSel, GUILayout.Width(42));
                            if (newSel != isSel)
                            {
                                if (newSel) selection.Add(a.GUID); else selection.Remove(a.GUID);
                            }
                        }

                        var icon = AssetDatabase.GetCachedIcon(a.AssetPath);
                        GUILayout.Label(new GUIContent(icon), GUILayout.Width(18), GUILayout.Height(18));

                        if (GUILayout.Button(new GUIContent(a.Name, a.AssetPath),
                            EditorStyles.label, GUILayout.Width(COL_NAME - 18), GUILayout.Height(ROW_H)))
                            PingAsset(a.AssetPath);

                        GUILayout.Label(a.SizeHuman, _styleSubLabel, GUILayout.Width(COL_SIZE));
                        GUILayout.Label(a.GUID, _styleMonoSmall, GUILayout.Width(220));

                        if (keep)
                        {
                            DrawTag("KEEP", TagColor.Green, 80);
                        }
                        else
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                if (GUILayout.Button("Make Keep", EditorStyles.miniButton, GUILayout.Width(78)))
                                {
                                    grp.Assets.Remove(a);
                                    grp.Assets.Insert(0, a);
                                    // The asset that was previously "keep" should now default into the merge selection
                                    selection.Remove(a.GUID);
                                }
                                if (GUILayout.Button("→ Swap tab", EditorStyles.miniButton, GUILayout.Width(76)))
                                {
                                    _swapList.Clear();
                                    _swapPreview = null;
                                    _swapList.Add(new SwapPickerEntry { Asset = a, Role = SwapRole.From });
                                    _swapList.Add(new SwapPickerEntry { Asset = grp.Assets[0], Role = SwapRole.To });
                                    _mainView = MainView.Swap;
                                }
                            }
                        }
                    }
                }
                GUILayout.Space(4);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Returns the set of GUIDs currently checked for merging within a group,
        /// lazily initialised to "everything except the kept (first) asset".
        /// </summary>
        HashSet<string> GetGroupSelection(DuplicateGroup grp)
        {
            if (!_dupesGroupSelection.TryGetValue(grp.HashValue, out var set))
            {
                set = new HashSet<string>(grp.Assets.Skip(1).Select(a => a.GUID));
                _dupesGroupSelection[grp.HashValue] = set;
            }
            // Keep selection consistent if the "keep" asset changed (Make Keep button)
            set.Remove(grp.Assets[0].GUID);
            return set;
        }

        /// <summary>
        /// Redirects every selected duplicate's GUID to the kept asset's GUID
        /// (one swap per duplicate), then optionally deletes the redirected files.
        /// </summary>
        void MergeGroup(DuplicateGroup grp, HashSet<string> selectedGuids)
        {
            var keep = grp.Assets[0];
            var dups = grp.Assets.Where(a => selectedGuids.Contains(a.GUID)).ToList();
            if (dups.Count == 0) return;

            var roots = ScanRoots();

            // Dry run first to show an accurate confirmation
            int totalFiles = 0, totalReplacements = 0;
            foreach (var dup in dups)
            {
                var r = FilesScanner.SwapGUID(dup, keep, dryRun: true, backup: false, rootFolders: roots);
                totalFiles += r.FilesUpdated.Count;
                totalReplacements += r.ReplacementsMade;
            }

            string msg = $"Merge {dups.Count} duplicate(s) into \"{keep.Name}\"?\n\n" +
                         $"This will update {totalFiles} reference file occurrence(s) " +
                         $"({totalReplacements} GUID replacement(s) total).";
            if (_dupesAutoDelete)
                msg += $"\n\nThe {dups.Count} duplicate file(s) will then be PERMANENTLY DELETED.";
            if (_dupesBackup)
                msg += "\n\n.bak backups will be created for every modified reference file.";

            if (!EditorUtility.DisplayDialog("Merge Duplicate Group", msg, "Merge", "Cancel")) return;

            int filesUpdated = 0, replacements = 0, deleted = 0;
            foreach (var dup in dups)
            {
                var r = FilesScanner.SwapGUID(dup, keep, dryRun: false, backup: _dupesBackup, rootFolders: roots);
                filesUpdated += r.FilesUpdated.Count;
                replacements += r.ReplacementsMade;
            }

            if (_dupesAutoDelete)
            {
                foreach (var dup in dups)
                    if (AssetDatabase.DeleteAsset(dup.AssetPath)) deleted++;
                AssetDatabase.Refresh();
            }

            _dupesGroupSelection.Remove(grp.HashValue);
            _statusMsg = $"Merged {dups.Count} duplicate(s) into \"{keep.Name}\" — " +
                         $"{replacements} GUID replacement(s) in {filesUpdated} file(s)." +
                         (deleted > 0 ? $" Deleted {deleted} duplicate file(s)." : "");

            // Refresh affected category so the UI reflects the merge / deletions
            RunScan(grp.Category);
        }

        // =====================================================================
        // SWAP TAB (generalized to any asset type)
        // =====================================================================
        void DrawSwap()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSwapSlot("FROM  (will be replaced)", SwapFrom, new Color(0.9f, 0.3f, 0.3f, 0.15f));
                GUILayout.Label("  →  ",
                    new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter },
                    GUILayout.Width(36));
                DrawSwapSlot("TO  (replacement)", SwapTo, new Color(0.3f, 0.9f, 0.4f, 0.15f));
            }
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                _swapDryRun = GUILayout.Toggle(_swapDryRun, "Dry run (preview only)", GUILayout.Width(160));
                _swapBackup = GUILayout.Toggle(_swapBackup, "Create .bak backups", GUILayout.Width(160));
                GUILayout.FlexibleSpace();

                GUI.enabled = SwapFrom != null && SwapTo != null;
                if (GUILayout.Button("Preview", GUILayout.Width(80))) PreviewSwap();

                GUI.color = _swapDryRun ? Color.white : new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button(_swapDryRun ? "Dry Run" : "⚠  Apply Swap", GUILayout.Width(110))) ExecuteSwap();
                GUI.color = Color.white;
                GUI.enabled = true;

                if (GUILayout.Button("Clear All", GUILayout.Width(70))) ClearSwap();
            }

            if (_swapPreview != null)
            {
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    string modeLabel = _swapPreview.DryRun ? "DRY RUN" : "APPLIED";
                    GUILayout.Label(
                        $"{modeLabel}  ·  {_swapPreview.FilesUpdated.Count} file(s) affected  " +
                        $"·  {_swapPreview.ReplacementsMade} replacement(s)",
                        _swapPreview.DryRun ? _styleWarning : _styleSuccess);

                    _swapResultScroll = EditorGUILayout.BeginScrollView(_swapResultScroll, GUILayout.MaxHeight(100));
                    foreach (var f in _swapPreview.FilesUpdated)
                    {
                        using var hr = new EditorGUILayout.HorizontalScope();
                        GUILayout.Label("•", GUILayout.Width(12));
                        if (GUILayout.Button(f, EditorStyles.miniLabel)) PingAsset(f);
                    }
                    EditorGUILayout.EndScrollView();
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("PICKER", _styleSectionTitle, GUILayout.Width(50));
                GUILayout.Space(6);
                GUILayout.Label("Search:", GUILayout.Width(46));
                _swapSearch = EditorGUILayout.TextField(_swapSearch,
                    EditorStyles.toolbarSearchField, GUILayout.Width(160));

                GUILayout.Space(6);
                GUILayout.Label(_activeCategory.HasValue
                    ? $"Filtering by: {AssetCategories.Get(_activeCategory.Value).DisplayName} (set via Category dropdown above)"
                    : "Showing all categories", _styleSubLabel);

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{_swapList.Count} in list", _styleSubLabel);
                GUILayout.Space(4);

                if (GUILayout.Button("Clear list", EditorStyles.toolbarButton, GUILayout.Width(68)))
                {
                    _swapList.Clear();
                    _swapPreview = null;
                }
                if (_assets.Count > 0 && GUILayout.Button("Load all", EditorStyles.toolbarButton, GUILayout.Width(62)))
                    PopulatePickerFromScan();
            }

            DrawDragDropZone();

            DrawColumnHeader(new[]
            {
                ("", 8f), ("", 18f), ("File", COL_NAME),
                ("Size", COL_SIZE), ("Category", 80f), ("Refs", COL_REFS),
                ("Role", COL_ROLE), ("GUID", 0f),
            });

            var visible = _swapList
                .Where(e => (string.IsNullOrEmpty(_swapSearch) ||
                             e.Asset.Name.IndexOf(_swapSearch, StringComparison.OrdinalIgnoreCase) >= 0) &&
                            (!_activeCategory.HasValue || e.Asset.Category == _activeCategory.Value))
                .ToList();

            _swapPickerScroll = EditorGUILayout.BeginScrollView(_swapPickerScroll);
            for (int i = 0; i < visible.Count; i++)
            {
                var entry = visible[i];
                var a = entry.Asset;

                Color bg = entry.Role switch
                {
                    SwapRole.From => new Color(0.55f, 0.18f, 0.18f, 0.45f),
                    SwapRole.To   => new Color(0.18f, 0.52f, 0.25f, 0.45f),
                    _             => i % 2 == 0 ? new Color(0.22f,0.22f,0.24f,0.4f) : new Color(0.18f,0.18f,0.20f,0.4f),
                };
                var rowStyle = new GUIStyle { normal = { background = MakeTex(bg) } };
                using var row = new EditorGUILayout.HorizontalScope(rowStyle, GUILayout.Height(ROW_H));

                GUILayout.Space(8);
                var icon = AssetDatabase.GetCachedIcon(a.AssetPath);
                GUILayout.Label(new GUIContent(icon), GUILayout.Width(18), GUILayout.Height(18));

                if (GUILayout.Button(new GUIContent(a.Name, a.AssetPath),
                    EditorStyles.label, GUILayout.Width(COL_NAME), GUILayout.Height(ROW_H)))
                    PingAsset(a.AssetPath);

                GUILayout.Label(a.SizeHuman, _styleSubLabel, GUILayout.Width(COL_SIZE));
                DrawTag(AssetCategories.Get(a.Category).DisplayName, TagColor.Purple, 80);
                GUILayout.Label(a.ReferencedBy.Count.ToString(), _styleSubLabel, GUILayout.Width(COL_REFS));

                var newRole = (SwapRole)EditorGUILayout.EnumPopup(entry.Role, GUILayout.Width(COL_ROLE));
                if (newRole != entry.Role)
                {
                    if (newRole == SwapRole.From)
                        foreach (var e in _swapList) if (e.Role == SwapRole.From) e.Role = SwapRole.None;
                    if (newRole == SwapRole.To)
                        foreach (var e in _swapList) if (e.Role == SwapRole.To) e.Role = SwapRole.None;
                    entry.Role = newRole;
                    _swapPreview = null;
                }

                GUILayout.Label(a.GUID, _styleMonoSmall);

                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(18)))
                {
                    _swapList.Remove(entry);
                    _swapPreview = null;
                    break;
                }
            }
            EditorGUILayout.EndScrollView();

            if (visible.Count == 0 && _swapList.Count == 0)
                DrawEmptyState("Drag any asset from the Project window, or run a Scan and press \"Load all\".");
        }

        Rect _swapDropRect;

        void DrawDragDropZone()
        {
            var zoneStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.55f, 0.55f, 0.65f) },
            };

            var rect = EditorGUILayout.GetControlRect(false, 28);
            _swapDropRect = rect;

            bool hovering = rect.Contains(Event.current.mousePosition)
                         && (DragAndDrop.objectReferences?.Length ?? 0) > 0;

            EditorGUI.DrawRect(rect, hovering
                ? new Color(0.3f, 0.28f, 0.45f, 0.6f)
                : new Color(0.18f, 0.18f, 0.22f, 0.4f));

            var borderStyle = new GUIStyle { normal = { background = MakeTex(hovering
                ? new Color(0.6f, 0.5f, 1f, 0.9f)
                : new Color(0.35f, 0.35f, 0.45f, 0.5f)) } };
            GUI.Box(new Rect(rect.x, rect.y, rect.width, 1), GUIContent.none, borderStyle);
            GUI.Box(new Rect(rect.x, rect.yMax - 1, rect.width, 1), GUIContent.none, borderStyle);

            GUI.Label(rect, hovering
                ? "Release to add to picker list ↓"
                : "⊕  Drag any file (or folder) here from the Project window", zoneStyle);

            HandleDragDrop(rect);
        }

        void HandleDragDrop(Rect dropRect)
        {
            var ev = Event.current;
            if (!dropRect.Contains(ev.mousePosition)) return;

            switch (ev.type)
            {
                case EventType.DragUpdated:
                    DragAndDrop.visualMode = DragAndDrop.objectReferences.Any(IsDroppableAsset)
                        ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    ev.Use();
                    break;

                case EventType.DragPerform:
                    DragAndDrop.AcceptDrag();
                    ev.Use();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var path = AssetDatabase.GetAssetPath(obj);
                        if (string.IsNullOrEmpty(path)) continue;

                        if (AssetDatabase.IsValidFolder(path))
                            AddFolderToPickerList(path);
                        else
                            AddToPickerList(path);
                    }
                    Repaint();
                    break;

                case EventType.DragExited:
                    Repaint();
                    break;
            }
        }

        static bool IsDroppableAsset(UnityEngine.Object obj)
        {
            if (obj == null) return false;
            var path = AssetDatabase.GetAssetPath(obj);
            return !string.IsNullOrEmpty(path);   // any asset or folder is droppable
        }

        void AddToPickerList(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath)) return;

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid) || _swapList.Any(e => e.Asset.GUID == guid)) return;

            var existing = _assets.FirstOrDefault(a => a.GUID == guid);
            if (existing != null) { _swapList.Add(new SwapPickerEntry { Asset = existing }); return; }

            var abs = Path.GetFullPath(assetPath);
            var def = AssetCategories.Resolve(Path.GetExtension(assetPath));
            var entry = new AssetEntry
            {
                AssetPath = assetPath, GUID = guid, AbsolutePath = abs,
                SizeBytes = File.Exists(abs) ? new FileInfo(abs).Length : 0,
                Category  = def.Category,
            };
            _swapList.Add(new SwapPickerEntry { Asset = entry });
        }

        void AddFolderToPickerList(string folderPath)
        {
            var guids = AssetDatabase.FindAssets("", new[] { folderPath });
            foreach (var g in guids)
                AddToPickerList(AssetDatabase.GUIDToAssetPath(g));
        }

        void PopulatePickerFromScan()
        {
            foreach (var a in _assets)
            {
                if (_swapList.Any(e => e.Asset.GUID == a.GUID)) continue;
                _swapList.Add(new SwapPickerEntry { Asset = a });
            }
        }

        void DrawSwapSlot(string label, AssetEntry a, Color bgColor)
        {
            var bgStyle = new GUIStyle(EditorStyles.helpBox) { normal = { background = MakeTex(bgColor) } };
            using var v = new EditorGUILayout.VerticalScope(bgStyle, GUILayout.Width(290));
            GUILayout.Label(label, _styleSectionTitle);

            if (a == null)
            {
                GUILayout.Label("— none selected —", _styleSubLabel);
                GUILayout.Label("Set role in the picker below", _styleSubLabel);
            }
            else
            {
                var icon = AssetDatabase.GetCachedIcon(a.AssetPath);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(icon), GUILayout.Width(32), GUILayout.Height(32));
                    using var vv = new EditorGUILayout.VerticalScope();
                    GUILayout.Label(a.Name, EditorStyles.boldLabel);
                    GUILayout.Label($"{AssetCategories.Get(a.Category).DisplayName}  ·  {a.SizeHuman}  ·  {a.ReferencedBy.Count} refs",
                        _styleSubLabel);
                }
                GUILayout.Label(a.GUID, _styleMonoSmall);
            }
        }

        void PreviewSwap()
        {
            var from = SwapFrom; var to = SwapTo;
            if (from == null || to == null) return;
            _swapPreview = FilesScanner.SwapGUID(from, to, dryRun: true, backup: false, rootFolders: ScanRoots());
        }

        void ExecuteSwap()
        {
            var from = SwapFrom; var to = SwapTo;
            if (from == null || to == null) return;

            if (!_swapDryRun && !EditorUtility.DisplayDialog("Apply GUID Swap",
                $"Replace all references to\n  {from.Name}\nwith\n  {to.Name}\n\n" +
                $"This will modify {_swapPreview?.FilesUpdated.Count ?? 0} file(s) on disk." +
                (_swapBackup ? "\n\n.bak backups will be created." : ""),
                "Apply", "Cancel")) return;

            _swapPreview = FilesScanner.SwapGUID(from, to,
                dryRun: _swapDryRun, backup: _swapBackup, rootFolders: ScanRoots());

            _statusMsg = _swapDryRun
                ? $"Dry run: {_swapPreview.FilesUpdated.Count} file(s) would be updated."
                : $"Swap applied: {_swapPreview.ReplacementsMade} replacement(s) in {_swapPreview.FilesUpdated.Count} file(s).";
        }

        void ClearSwap()
        {
            foreach (var e in _swapList) e.Role = SwapRole.None;
            _swapPreview = null;
        }

        // =====================================================================
        // SCAN — either a single category or everything
        // =====================================================================
        void RunScan(AssetCategory? categoryFilter)
        {
            _scanning = true;
            var roots = ScanRoots();

            try
            {
                SetProgress(0.05f, categoryFilter.HasValue
                    ? $"Discovering {AssetCategories.Get(categoryFilter.Value).DisplayName}…"
                    : "Discovering all files…");

                var discovered = FilesScanner.DiscoverAssets(roots, categoryFilter);

                if (categoryFilter.HasValue)
                {
                    _assets.RemoveAll(a => a.Category == categoryFilter.Value);
                    _assets.AddRange(discovered);
                    _scannedCategories.Add(categoryFilter.Value);
                }
                else
                {
                    _assets = discovered;
                    _scannedCategories = new HashSet<AssetCategory>(AssetCategories.All.Select(d => d.Category));
                }

                SetProgress(0.25f, "Scanning references…");
                var unusedSubset = FilesScanner.FindUnused(discovered, roots, (done, total, name) =>
                    SetProgress(0.25f + 0.5f * done / Math.Max(total, 1), $"Scanning {name}…"));

                if (categoryFilter.HasValue)
                {
                    _unused.RemoveAll(a => a.Category == categoryFilter.Value);
                    _unused.AddRange(unusedSubset);
                }
                else
                {
                    _unused = unusedSubset;
                }

                SetProgress(0.8f, "Computing hashes…");
                var dupesSubset = FilesScanner.FindDuplicates(discovered, _dupesMode, (done, total, name) =>
                    SetProgress(0.8f + 0.18f * done / Math.Max(total, 1), $"Hashing {name}…"));

                if (categoryFilter.HasValue)
                {
                    _dupes.RemoveAll(g => g.Category == categoryFilter.Value);
                    _dupes.AddRange(dupesSubset);
                }
                else
                {
                    _dupes = dupesSubset;
                }

                SetProgress(1f, "Done.");
                _statusMsg = categoryFilter.HasValue
                    ? $"Scanned {AssetCategories.Get(categoryFilter.Value).DisplayName}: " +
                      $"{discovered.Count} files, {unusedSubset.Count} unused, {dupesSubset.Count} dupe groups."
                    : $"Full scan complete — {_assets.Count} files, {_unused.Count} unused, {_dupes.Count} dupe groups.";
            }
            catch (Exception e)
            {
                _statusMsg = $"Error during scan: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                _scanning = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        void SetProgress(float p, string label)
        {
            _progress = p;
            _progressLabel = label;
            EditorUtility.DisplayProgressBar("Files Profiler", label, p);
            Repaint();
        }

        string[] ScanRoots() =>
            _wholeProject || string.IsNullOrEmpty(_customFolder) ? null : new[] { _customFolder };

        // =====================================================================
        // SHARED DRAWING HELPERS
        // =====================================================================
        void DrawColumnHeader((string label, float width)[] cols)
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            foreach (var (label, width) in cols)
            {
                if (width > 0) GUILayout.Label(label, _styleSectionTitle, GUILayout.Width(width));
                else            GUILayout.Label(label, _styleSectionTitle);
            }
        }

        enum TagColor { Red, Green, Purple, Amber }

        void DrawTag(string text, TagColor color, float width)
        {
            var (bg, fg) = color switch
            {
                TagColor.Red   => (new Color(0.6f,0.15f,0.15f), new Color(1f,0.7f,0.7f)),
                TagColor.Green => (new Color(0.1f,0.45f,0.2f),  new Color(0.6f,1f,0.7f)),
                TagColor.Amber => (new Color(0.5f,0.35f,0.05f), new Color(1f,0.85f,0.4f)),
                _              => (new Color(0.3f,0.2f,0.55f),  new Color(0.8f,0.7f,1f)),
            };
            var s = new GUIStyle(_styleTag)
                { normal = { background = MakeTex(bg), textColor = fg },
                  fixedHeight = 16, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(text, s, GUILayout.Width(width), GUILayout.Height(16));
        }

        void DrawEmptyState(string msg)
        {
            GUILayout.FlexibleSpace();
            using var h = new EditorGUILayout.HorizontalScope();
            GUILayout.FlexibleSpace();
            GUILayout.Label(msg, EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.FlexibleSpace();
        }

        static void PingAsset(string assetPath)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
        }

        static string HumanSize(long bytes)
        {
            double b = bytes;
            foreach (var u in new[]{"B","KB","MB","GB"})
            {
                if (b < 1024) return $"{b:F1} {u}";
                b /= 1024;
            }
            return $"{b:F1} TB";
        }
    }
}
