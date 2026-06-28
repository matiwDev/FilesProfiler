using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;
using UnityEditor.U2D;

namespace Creatush.BuildProfiler
{

#if UNITY_ADDRESSABLES
using UnityEditor.AddressableAssets;
#endif

    public class BuildProfiler : EditorWindow
    {
        enum Tab { AllAssets, Duplicates, Unused, Special, Optimize }

        class AssetRecord
        {
            public string path;
            public string name;
            public string type;
            public long sizeBytes;
            public string sizeLabel;
            public List<string> usedInScenes = new();
            public List<string> directRefs = new();   // assets that directly reference this
            public List<string> indirectRefs = new();   // assets 2+ hops away that pull this in
            public bool inResources;
            public bool inStreamingAssets;
            public bool isAddressable;
            public bool isUnused;
            public bool showDirect;                // foldout state
            public bool showIndirect;              // foldout state
        }

        class DuplicateGroup
        {
            public string hash;
            public string assetType;
            public long sizeBytes;
            public List<string> paths = new();
            public string wastedLabel;
            public bool expanded;
            public bool selected;                  // included in next batch consolidation
            public int keepIndex = 0;         // which path to keep
        }

        class MaterialDupGroup
        {
            public string textureSignature;
            public List<string> materialPaths = new();
            public bool expanded;
        }

        class TextureIssue
        {
            public string path;
            public string name;
            public long sizeBytes;
            public string sizeLabel;
            public int width, height;
            public List<string> problems = new();
            public long estSavingsBytes;
            public bool selected;
        }

        class AudioIssue
        {
            public string path;
            public string name;
            public long sizeBytes;
            public string sizeLabel;
            public string problem;
            public long estSavingsBytes;
            public bool selected;
        }

        class AtlasOpportunity
        {
            public string folder;
            public List<string> paths = new();
            public long totalBytes;
            public bool selected;
            public bool expanded;
        }

        class ProceduralCandidate
        {
            public string path;
            public string name;
            public long sizeBytes;
            public string reason;
            public bool selected;
        }

        // ═══════════════════════════════════════════════════════════
        //  STATE
        // ═══════════════════════════════════════════════════════════

        Tab _tab = Tab.AllAssets;
        List<AssetRecord> _allAssets = new();
        List<DuplicateGroup> _dupTextures = new();
        List<DuplicateGroup> _dupMeshes = new();
        List<MaterialDupGroup> _dupMaterials = new();
        List<AssetRecord> _unused = new();
        List<AssetRecord> _resources = new();
        List<AssetRecord> _streaming = new();
        List<AssetRecord> _addressables = new();

        List<TextureIssue> _textureIssues = new();
        List<AudioIssue> _audioIssues = new();
        List<AtlasOpportunity> _atlasOpportunities = new();
        List<ProceduralCandidate> _proceduralCandidates = new();

        // ── Optimize tab: fix options ───────────────────────────────
        bool _fixCompression = true;
        bool _fixSpriteMipmaps = true;
        bool _fixReadWrite = true;
        float _audioQuality = 0.5f;

        bool _scanning;
        float _progress;
        string _progressLabel = "";

        // ── Scope ──────────────────────────────────────────────────
        // When true, the scan is restricted to assets that actually end up
        // in a Player build: dependencies of enabled Build Settings scenes,
        // Resources/, StreamingAssets/, and Addressables entries.
        bool _buildAssetsOnly = true;

        // ── Filters ────────────────────────────────────────────────
        string _search = "";
        string[] _typeOptions = { "All", "Texture", "Mesh", "Audio", "Material", "Prefab", "Script", "Other" };
        int _typeIndex;
        bool _filterUnused;
        bool _filterResources;
        bool _filterAddressable;
        bool _filterHasIndirect;

        // ── Sort ───────────────────────────────────────────────────
        enum SortCol { Name, Type, Size, Scenes, Direct, Indirect, Flags }
        SortCol _sortCol = SortCol.Size;
        bool _sortAsc;

        // ── Column widths (resizable) ──────────────────────────────
        // Order: Name | Type | Size | Scenes | Direct | Indirect | Flags
        float[] _colW = { 280, 72, 74, 120, 90, 90, 105 };
        float[] _colMinW = { 80, 40, 50, 60, 50, 50, 60 };
        int _resizingCol = -1;
        float _resizeStartX;
        float _resizeStartW;

        // ── Scroll ─────────────────────────────────────────────────
        Vector2 _scrollAll, _scrollDup, _scrollUnused, _scrollSpecial, _scrollOptimize;

        // ── Styles (built lazily) ──────────────────────────────────
        GUIStyle _styleRow;
        GUIStyle _styleRowAlt;
        GUIStyle _styleHeader;
        GUIStyle _styleLink;
        GUIStyle _styleTag;
        GUIStyle _styleDivider;
        bool _stylesBuilt;

        // ═══════════════════════════════════════════════════════════
        //  OPEN
        // ═══════════════════════════════════════════════════════════

        [MenuItem("Tools/Creatush/Build Profiler")]
        static void Open()
        {
            var w = GetWindow<BuildProfiler>("Asset Analyzer");
            w.minSize = new Vector2(700, 400);
        }

        // ═══════════════════════════════════════════════════════════
        //  STYLES
        // ═══════════════════════════════════════════════════════════

        void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _styleRow = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(6, 6, 3, 3),
                fontSize = 12,
                richText = true
            };
            _styleRowAlt = new GUIStyle(_styleRow);
            _styleRowAlt.normal.background = MakeTex(1, 1,
                EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.04f)
                    : new Color(0f, 0f, 0f, 0.03f));

            _styleHeader = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 4, 0, 0)
            };

            _styleLink = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                richText = true,
                padding = new RectOffset(4, 4, 3, 3),
                wordWrap = false
            };
            _styleLink.normal.textColor = new Color(0.25f, 0.60f, 1f);
            _styleLink.hover.textColor = new Color(0.4f, 0.75f, 1f);

            _styleTag = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 9,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(4, 4, 1, 1),
                alignment = TextAnchor.MiddleCenter
            };

            _styleDivider = new GUIStyle
            {
                fixedHeight = 1,
                margin = new RectOffset(0, 0, 2, 2)
            };
            _styleDivider.normal.background = MakeTex(1, 1,
                EditorGUIUtility.isProSkin
                    ? new Color(1, 1, 1, 0.1f)
                    : new Color(0, 0, 0, 0.12f));
        }

        static Texture2D MakeTex(int w, int h, Color col)
        {
            var t = new Texture2D(w, h);
            for (int i = 0; i < w * h; i++) t.SetPixel(0, 0, col);
            t.Apply(); return t;
        }

        // ═══════════════════════════════════════════════════════════
        //  ON GUI
        // ═══════════════════════════════════════════════════════════

        void OnGUI()
        {
            BuildStyles();
            DrawTopBar();

            if (_scanning)
            {
                GUILayout.Space(50);
                EditorGUI.ProgressBar(new Rect(20, 90, position.width - 40, 22), _progress, _progressLabel);
                Repaint();
                return;
            }

            if (_allAssets.Count == 0)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Click  ▶ Scan Project  in the toolbar to begin.",
                    EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
                return;
            }

            DrawDashboard();

            switch (_tab)
            {
                case Tab.AllAssets: DrawAllAssets(); break;
                case Tab.Duplicates: DrawDuplicates(); break;
                case Tab.Unused: DrawUnused(); break;
                case Tab.Special: DrawSpecial(); break;
                case Tab.Optimize: DrawOptimize(); break;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  DASHBOARD
        // ═══════════════════════════════════════════════════════════

        void DrawDashboard()
        {
            long totalBytes = _allAssets.Sum(a => a.sizeBytes);
            long dupSavings = _dupTextures.Sum(g => g.sizeBytes * (g.paths.Count - 1))
                              + _dupMeshes.Sum(g => g.sizeBytes * (g.paths.Count - 1));
            long texSavings = _textureIssues.Sum(t => t.estSavingsBytes);
            long audSavings = _audioIssues.Sum(a => a.estSavingsBytes);
            long totalSavings = dupSavings + texSavings + audSavings;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            DrawDashStat("Build Footprint", FormatBytes(totalBytes), new Color(0.30f, 0.55f, 0.95f));
            DrawDashStat("Est. Reclaimable", FormatBytes(totalSavings), new Color(0.95f, 0.55f, 0.20f));
            DrawDashStat("Duplicate Groups", $"{_dupTextures.Count + _dupMeshes.Count}", new Color(0.88f, 0.32f, 0.32f));
            DrawDashStat("Atlas Opportunities", $"{_atlasOpportunities.Count}", new Color(0.30f, 0.75f, 0.55f));
            DrawDashStat("Procedural Candidates", $"{_proceduralCandidates.Count}", new Color(0.58f, 0.42f, 0.92f));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var divider = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(divider, EditorGUIUtility.isProSkin
                ? new Color(1, 1, 1, 0.08f) : new Color(0, 0, 0, 0.1f));
            GUILayout.Space(4);
        }

        void DrawDashStat(string label, string value, Color accent)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(150));
            var barRect = EditorGUILayout.GetControlRect(false, 3, GUILayout.Width(60));
            EditorGUI.DrawRect(barRect, accent);
            GUILayout.Label(value, new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 });
            GUILayout.Label(label, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            GUILayout.Space(16);
        }

        // ═══════════════════════════════════════════════════════════
        //  TOP BAR
        // ═══════════════════════════════════════════════════════════

        void DrawTopBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Logo / title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            GUILayout.Label("⬡  Asset Analyzer", titleStyle, GUILayout.Width(140));

            // Tab strip
            string[] tabLabels =
            {
            $"▤ All Assets  ({_allAssets.Count})",
            $"⧉ Duplicates  ({_dupTextures.Count + _dupMeshes.Count + _dupMaterials.Count})",
            $"⌫ Unused  ({_unused.Count})",
            $"★ Special  ({_resources.Count + _streaming.Count + _addressables.Count})",
            $"⚙ Optimize  ({_textureIssues.Count + _audioIssues.Count + _atlasOpportunities.Count + _proceduralCandidates.Count})"
        };
            _tab = (Tab)GUILayout.Toolbar((int)_tab, tabLabels, EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            _buildAssetsOnly = GUILayout.Toggle(
                _buildAssetsOnly,
                new GUIContent("Build Assets Only",
                    "Restrict the scan to what actually ships: dependencies of " +
                    "enabled Build Settings scenes, Resources/, StreamingAssets/, " +
                    "and Addressables entries. Re-run Scan after toggling."),
                EditorStyles.toolbarButton, GUILayout.Width(120));
            GUILayout.Space(6);

            if (GUILayout.Button("▶ Scan", EditorStyles.toolbarButton, GUILayout.Width(70)))
                RunScan();
            if (_allAssets.Count > 0 && GUILayout.Button("↓ CSV", EditorStyles.toolbarButton, GUILayout.Width(55)))
                ExportCSV();

            EditorGUILayout.EndHorizontal();
        }

        // ═══════════════════════════════════════════════════════════
        //  ALL ASSETS TAB
        // ═══════════════════════════════════════════════════════════

        void DrawAllAssets()
        {
            // ── Filter bar ────────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Filter:", EditorStyles.toolbarButton, GUILayout.Width(40));
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(180));
            _typeIndex = EditorGUILayout.Popup(_typeIndex, _typeOptions, EditorStyles.toolbarPopup, GUILayout.Width(80));
            GUILayout.Space(6);
            _filterUnused = GUILayout.Toggle(_filterUnused, "Unused", EditorStyles.toolbarButton, GUILayout.Width(55));
            _filterResources = GUILayout.Toggle(_filterResources, "Resources", EditorStyles.toolbarButton, GUILayout.Width(72));
            _filterAddressable = GUILayout.Toggle(_filterAddressable, "Addressable", EditorStyles.toolbarButton, GUILayout.Width(82));
            _filterHasIndirect = GUILayout.Toggle(_filterHasIndirect, "Has Indirect", EditorStyles.toolbarButton, GUILayout.Width(85));
            GUILayout.FlexibleSpace();

            // Summary
            long totalBytes = _allAssets.Sum(a => a.sizeBytes);
            GUILayout.Label($"Total on disk: {FormatBytes(totalBytes)}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            // ── Build filtered + sorted list ──────────────────────
            string typeFilter = _typeOptions[_typeIndex];
            var filtered = _allAssets
                .Where(a =>
                    (string.IsNullOrEmpty(_search) || a.path.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                 && (typeFilter == "All" || a.type == typeFilter)
                 && (!_filterUnused || a.isUnused)
                 && (!_filterResources || a.inResources)
                 && (!_filterAddressable || a.isAddressable)
                 && (!_filterHasIndirect || a.indirectRefs.Count > 0))
                .ToList();

            SortList(filtered);

            // ── Column headers (resizable + sortable) ─────────────
            DrawColumnHeaders();
            HandleColumnResize();

            // ── Rows ──────────────────────────────────────────────
            _scrollAll = EditorGUILayout.BeginScrollView(_scrollAll);
            for (int i = 0; i < filtered.Count; i++)
            {
                DrawAssetRow(filtered[i], i);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawColumnHeaders()
        {
            string[] labels = { "Name", "Type", "Size", "Scenes", "Direct Refs", "Indirect Refs", "Flags" };
            SortCol[] cols = { SortCol.Name, SortCol.Type, SortCol.Size,
                             SortCol.Scenes, SortCol.Direct, SortCol.Indirect, SortCol.Flags };

            Rect r = EditorGUILayout.GetControlRect(false, 20);
            float x = r.x;

            for (int i = 0; i < labels.Length; i++)
            {
                float w = _colW[i];
                var cellRect = new Rect(x, r.y, w - 4, r.height);

                // Sort arrow
                string arrow = (_sortCol == cols[i]) ? (_sortAsc ? " ▲" : " ▼") : "";
                if (GUI.Button(cellRect, labels[i] + arrow, _styleHeader))
                {
                    if (_sortCol == cols[i]) _sortAsc = !_sortAsc;
                    else { _sortCol = cols[i]; _sortAsc = false; }
                }

                // Resize handle
                var handleRect = new Rect(x + w - 4, r.y, 8, r.height);
                EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeHorizontal);

                if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
                {
                    _resizingCol = i;
                    _resizeStartX = Event.current.mousePosition.x;
                    _resizeStartW = _colW[i];
                    Event.current.Use();
                }

                x += w;
            }
        }

        void HandleColumnResize()
        {
            if (_resizingCol < 0) return;
            if (Event.current.type == EventType.MouseDrag)
            {
                float delta = Event.current.mousePosition.x - _resizeStartX;
                _colW[_resizingCol] = Mathf.Max(_colMinW[_resizingCol], _resizeStartW + delta);
                Repaint();
            }
            if (Event.current.type == EventType.MouseUp)
                _resizingCol = -1;
        }

        void DrawAssetRow(AssetRecord a, int index)
        {
            var rowStyle = index % 2 == 0 ? _styleRow : _styleRowAlt;
            Rect r = EditorGUILayout.GetControlRect(false, 20);
            GUI.Box(r, GUIContent.none, rowStyle);

            float x = r.x;

            // ── Name (clickable link) ──────────────────────────────
            var nameRect = new Rect(x, r.y, _colW[0] - 4, r.height);
            string shortName = a.name.Length > 38 ? "…" + a.name.Substring(a.name.Length - 37) : a.name;
            if (GUI.Button(nameRect, shortName, _styleLink)) PingAsset(a.path);
            x += _colW[0];

            // ── Type ──────────────────────────────────────────────
            GUI.Label(new Rect(x, r.y, _colW[1] - 4, r.height), a.type, _styleRow); x += _colW[1];

            // ── Size ──────────────────────────────────────────────
            GUI.Label(new Rect(x, r.y, _colW[2] - 4, r.height), a.sizeLabel, _styleRow); x += _colW[2];

            // ── Scenes ────────────────────────────────────────────
            string sceneTxt = a.usedInScenes.Count == 0 ? "—" : string.Join(", ", a.usedInScenes.Take(3));
            if (a.usedInScenes.Count > 3) sceneTxt += $" +{a.usedInScenes.Count - 3}";
            GUI.Label(new Rect(x, r.y, _colW[3] - 4, r.height), sceneTxt, _styleRow); x += _colW[3];

            // ── Direct Refs (foldout + clickable links) ───────────
            var directRect = new Rect(x, r.y, _colW[4] - 4, r.height);
            if (a.directRefs.Count > 0)
            {
                string dLabel = a.showDirect
                    ? $"▾ {a.directRefs.Count} asset(s)"
                    : $"▸ {a.directRefs.Count} asset(s)";
                if (GUI.Button(directRect, dLabel, _styleLink))
                    a.showDirect = !a.showDirect;
            }
            else
            {
                GUI.Label(directRect, "—", _styleRow);
            }
            x += _colW[4];

            // ── Indirect Refs (foldout + clickable links) ─────────
            var indirectRect = new Rect(x, r.y, _colW[5] - 4, r.height);
            if (a.indirectRefs.Count > 0)
            {
                string label = a.showIndirect
                    ? $"▾ {a.indirectRefs.Count} ref(s)"
                    : $"▸ {a.indirectRefs.Count} ref(s)";
                if (GUI.Button(indirectRect, label, _styleLink))
                    a.showIndirect = !a.showIndirect;
            }
            else
            {
                GUI.Label(indirectRect, "—", _styleRow);
            }
            x += _colW[5];

            // ── Flags ─────────────────────────────────────────────
            DrawFlags(new Rect(x, r.y, _colW[6], r.height), a);

            // ── Direct expandable rows ─────────────────────────────
            if (a.showDirect && a.directRefs.Count > 0)
            {
                foreach (var refPath in a.directRefs)
                {
                    Rect subR = EditorGUILayout.GetControlRect(false, 18);
                    GUI.Box(subR, GUIContent.none, _styleRowAlt);
                    float indent = _colW[0] + _colW[1] + _colW[2] + _colW[3];
                    var linkR = new Rect(subR.x + indent + 14, subR.y, _colW[4] - 18, subR.height);
                    string shortRef = refPath.Length > 48 ? "↳ …" + refPath.Substring(refPath.Length - 45) : "↳ " + refPath;
                    if (GUI.Button(linkR, shortRef, _styleLink))
                        PingAsset(refPath);
                }
            }

            // ── Indirect expandable rows ───────────────────────────
            if (a.showIndirect && a.indirectRefs.Count > 0)
            {
                foreach (var refPath in a.indirectRefs)
                {
                    Rect subR = EditorGUILayout.GetControlRect(false, 18);
                    GUI.Box(subR, GUIContent.none, _styleRowAlt);
                    float indent = _colW[0] + _colW[1] + _colW[2] + _colW[3] + _colW[4];
                    var linkR = new Rect(subR.x + indent + 14, subR.y, _colW[5] - 18, subR.height);
                    string shortRef = refPath.Length > 48 ? "↳ …" + refPath.Substring(refPath.Length - 45) : "↳ " + refPath;
                    if (GUI.Button(linkR, shortRef, _styleLink))
                        PingAsset(refPath);
                }
            }
        }

        void DrawFlags(Rect r, AssetRecord a)
        {
            float tx = r.x + 2;
            if (a.inResources) tx = DrawTag(tx, r.y, "[Res]", new Color(0.1f, 0.7f, 0.4f), Color.white);
            if (a.inStreamingAssets) tx = DrawTag(tx, r.y, "[Stream]", new Color(0.85f, 0.6f, 0.1f), Color.white);
            if (a.isAddressable) tx = DrawTag(tx, r.y, "[Addr]", new Color(0.3f, 0.4f, 0.9f), Color.white);
            if (a.isUnused) tx = DrawTag(tx, r.y, "Unused", new Color(0.8f, 0.2f, 0.2f), Color.white);
        }

        float DrawTag(float x, float y, string label, Color bg, Color fg)
        {
            var content = new GUIContent(label);
            float w = _styleTag.CalcSize(content).x + 8;
            var r = new Rect(x, y + 3, w, 14);
            EditorGUI.DrawRect(r, bg);
            _styleTag.normal.textColor = fg;
            GUI.Label(r, label, _styleTag);
            return x + w + 3;
        }

        void SortList(List<AssetRecord> list)
        {
            Comparison<AssetRecord> cmp = _sortCol switch
            {
                SortCol.Name => (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase),
                SortCol.Type => (a, b) => string.Compare(a.type, b.type, StringComparison.OrdinalIgnoreCase),
                SortCol.Size => (a, b) => b.sizeBytes.CompareTo(a.sizeBytes),
                SortCol.Scenes => (a, b) => b.usedInScenes.Count.CompareTo(a.usedInScenes.Count),
                SortCol.Direct => (a, b) => b.directRefs.Count.CompareTo(a.directRefs.Count),
                SortCol.Indirect => (a, b) => b.indirectRefs.Count.CompareTo(a.indirectRefs.Count),
                SortCol.Flags => (a, b) => FlagScore(b).CompareTo(FlagScore(a)),
                _ => (a, b) => 0
            };
            if (_sortAsc) list.Sort((a, b) => cmp(b, a));
            else list.Sort(cmp);
        }

        static int FlagScore(AssetRecord a) =>
            (a.isUnused ? 8 : 0) | (a.inResources ? 4 : 0) | (a.isAddressable ? 2 : 0) | (a.inStreamingAssets ? 1 : 0);

        // ═══════════════════════════════════════════════════════════
        //  DUPLICATES TAB
        // ═══════════════════════════════════════════════════════════

        void DrawDuplicates()
        {
            long wasted = _dupTextures.Sum(g => g.sizeBytes * (g.paths.Count - 1))
                        + _dupMeshes.Sum(g => g.sizeBytes * (g.paths.Count - 1));

            EditorGUILayout.HelpBox(
                $"Potential savings: {FormatBytes(wasted)}\n" +
                "Tick the checkbox on the groups you want, or use a single group's own Consolidate button.\n" +
                "All GUID references to removed copies are rewritten to point to the keeper.",
                MessageType.Warning);

            // ── Batch bar ──────────────────────────────────────────
            var batchable = _dupTextures.Concat(_dupMeshes).ToList();
            if (batchable.Count > 0)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                bool allSelected = batchable.All(g => g.selected);
                if (GUILayout.Button(allSelected ? "Deselect All" : "Select All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                    foreach (var g in batchable) g.selected = !allSelected;

                GUILayout.FlexibleSpace();

                int selCount = batchable.Count(g => g.selected);
                long selSavings = batchable.Where(g => g.selected).Sum(g => g.sizeBytes * (g.paths.Count - 1));
                using (new EditorGUI.DisabledScope(selCount == 0))
                {
                    Color bg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.4f, 0.35f);
                    if (GUILayout.Button($"Consolidate Selected  ({selCount} groups • {FormatBytes(selSavings)})",
                        EditorStyles.toolbarButton, GUILayout.Width(280)))
                    {
                        if (EditorUtility.DisplayDialog("Consolidate Selected Duplicates",
                            $"This will rewrite references and delete duplicate files across {selCount} group(s), " +
                            $"freeing roughly {FormatBytes(selSavings)}.\n\n" +
                            "Make sure your project is committed to version control before proceeding.",
                            "Consolidate", "Cancel"))
                        {
                            ConsolidateDuplicatesBatch(batchable.Where(g => g.selected).ToList());
                        }
                    }
                    GUI.backgroundColor = bg;
                }
                EditorGUILayout.EndHorizontal();
            }

            _scrollDup = EditorGUILayout.BeginScrollView(_scrollDup);

            if (_dupTextures.Count > 0)
            {
                SectionLabel($"Duplicate Textures — {_dupTextures.Count} groups");
                foreach (var g in _dupTextures) DrawDuplicateGroup(g);
            }

            if (_dupMeshes.Count > 0)
            {
                GUILayout.Space(8);
                SectionLabel($"Duplicate Meshes — {_dupMeshes.Count} groups");
                foreach (var g in _dupMeshes) DrawDuplicateGroup(g);
            }

            if (_dupMaterials.Count > 0)
            {
                GUILayout.Space(8);
                SectionLabel($"Materials with Identical Texture Sets — {_dupMaterials.Count} groups");
                foreach (var g in _dupMaterials) DrawMaterialDupGroup(g);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawDuplicateGroup(DuplicateGroup g)
        {
            // ── Foldout header ────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            g.selected = GUILayout.Toggle(g.selected, "", GUILayout.Width(18));
            g.expanded = EditorGUILayout.Foldout(g.expanded,
                $"{g.assetType}  ×{g.paths.Count}   {FormatBytes(g.sizeBytes)} each   [{g.wastedLabel}]",
                true, EditorStyles.foldout);
            EditorGUILayout.EndHorizontal();
            if (!g.expanded) return;

            EditorGUI.indentLevel++;

            for (int i = 0; i < g.paths.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                // ○ / ● keep radio
                bool isKeeper = g.keepIndex == i;
                Color prev = GUI.color;
                GUI.color = isKeeper ? new Color(0.3f, 0.9f, 0.4f) : Color.white;
                if (GUILayout.Button(isKeeper ? "● KEEP" : "○ keep", GUILayout.Width(65)))
                    g.keepIndex = i;
                GUI.color = prev;

                // Clickable path
                if (GUILayout.Button(g.paths[i], EditorStyles.linkLabel))
                    PingAsset(g.paths[i]);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;

            // ── Action button ─────────────────────────────────────
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            Color btnC = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.4f, 0.35f);
            if (GUILayout.Button($"Consolidate → Keep  \"{Path.GetFileName(g.paths[g.keepIndex])}\"  & Delete Others",
                GUILayout.Height(24)))
            {
                if (EditorUtility.DisplayDialog("Consolidate Duplicates",
                    $"This will:\n" +
                    $"• Rewrite all GUID references to point to:\n  {g.paths[g.keepIndex]}\n" +
                    $"• Delete {g.paths.Count - 1} duplicate file(s)\n\n" +
                    "Make sure your project is committed to version control before proceeding.",
                    "Consolidate", "Cancel"))
                {
                    ConsolidateDuplicatesBatch(new List<DuplicateGroup> { g });
                }
            }
            GUI.backgroundColor = btnC;
            GUILayout.Space(20);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        void DrawMaterialDupGroup(MaterialDupGroup g)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            g.expanded = EditorGUILayout.Foldout(g.expanded,
                $"{g.materialPaths.Count} materials share the same texture set — consider merging",
                true, EditorStyles.foldout);
            EditorGUILayout.EndHorizontal();
            if (!g.expanded) return;

            EditorGUI.indentLevel++;
            foreach (var p in g.materialPaths)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(p, EditorStyles.linkLabel)) PingAsset(p);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        // ── GUID consolidation ────────────────────────────────────

        void ConsolidateDuplicatesBatch(List<DuplicateGroup> groups)
        {
            if (groups.Count == 0) return;

            // Collect every text-based asset that might reference any of the duplicate GUIDs
            var allTextPaths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/") && IsTextAsset(p))
                .ToList();

            int totalDeleted = 0, totalRewrites = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var g in groups)
                {
                    string keepPath = g.paths[g.keepIndex];
                    string keepGuid = AssetDatabase.AssetPathToGUID(keepPath);
                    var toDelete = g.paths.Where((p, i) => i != g.keepIndex).ToList();

                    foreach (var dupPath in toDelete)
                    {
                        string dupGuid = AssetDatabase.AssetPathToGUID(dupPath);

                        foreach (var assetPath in allTextPaths)
                        {
                            string fullPath = Path.GetFullPath(assetPath);
                            if (!File.Exists(fullPath)) continue;

                            string text = File.ReadAllText(fullPath);
                            if (!text.Contains(dupGuid)) continue;

                            File.WriteAllText(fullPath, text.Replace(dupGuid, keepGuid));
                            totalRewrites++;
                        }

                        AssetDatabase.DeleteAsset(dupPath);
                        totalDeleted++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();
            Debug.Log($"[AssetSizeAnalyzer] Consolidated {groups.Count} group(s) → removed {totalDeleted} " +
                      $"duplicate file(s)  ({totalRewrites} reference rewrite(s))");

            // Refresh our own data
            RunScan();
        }

        static bool IsTextAsset(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            return ext is ".unity" or ".prefab" or ".mat" or ".asset" or ".anim"
                       or ".controller" or ".overrideController" or ".playable"
                       or ".shadergraph" or ".hlsl" or ".shader" or ".spriteatlas"
                       or ".lighting" or ".renderTexture" or ".flare" or ".mixer";
        }

        // ═══════════════════════════════════════════════════════════
        //  UNUSED TAB
        // ═══════════════════════════════════════════════════════════

        void DrawUnused()
        {
            long totalWaste = _unused.Sum(a => a.sizeBytes);
            EditorGUILayout.HelpBox(
                $"{_unused.Count} assets appear unreferenced.  Combined size: {FormatBytes(totalWaste)}\n" +
                "Note: scripts loaded via reflection or string paths won't appear in dependency graphs.",
                MessageType.Info);

            DrawColumnHeaders();
            HandleColumnResize();

            _scrollUnused = EditorGUILayout.BeginScrollView(_scrollUnused);
            var sorted = _unused.OrderByDescending(a => a.sizeBytes).ToList();
            for (int i = 0; i < sorted.Count; i++)
                DrawAssetRow(sorted[i], i);
            EditorGUILayout.EndScrollView();
        }

        // ═══════════════════════════════════════════════════════════
        //  SPECIAL TAB
        // ═══════════════════════════════════════════════════════════

        void DrawSpecial()
        {
            _scrollSpecial = EditorGUILayout.BeginScrollView(_scrollSpecial);

            DrawSpecialSection(
                "Resources/  — Always included in build",
                _resources,
                "Assets inside Resources/ are bundled even if never called at runtime. " +
                "Move infrequently used assets to Addressables and load them by key.");

            GUILayout.Space(10);
            DrawSpecialSection(
                "StreamingAssets/  — Copied verbatim",
                _streaming,
                "These files are not processed by Unity. Compress what you can and remove anything not used at runtime.");

            GUILayout.Space(10);
            DrawSpecialSection(
                "Addressables",
                _addressables,
                "Loaded on demand. Verify groups use LZ4 compression and that stale entries have been removed from groups.");

            EditorGUILayout.EndScrollView();
        }

        void DrawSpecialSection(string title, List<AssetRecord> list, string tip)
        {
            long total = list.Sum(a => a.sizeBytes);
            SectionLabel($"{title}  —  {list.Count} assets, {FormatBytes(total)}");
            EditorGUILayout.HelpBox(tip, MessageType.None);

            DrawColumnHeaders();
            HandleColumnResize();

            var sorted = list.OrderByDescending(x => x.sizeBytes).Take(200).ToList();
            for (int i = 0; i < sorted.Count; i++)
                DrawAssetRow(sorted[i], i);
        }

        // ═══════════════════════════════════════════════════════════
        //  OPTIMIZE TAB
        // ═══════════════════════════════════════════════════════════

        void DrawOptimize()
        {
            _scrollOptimize = EditorGUILayout.BeginScrollView(_scrollOptimize);

            DrawTextureIssuesSection();
            GUILayout.Space(10);
            DrawAudioIssuesSection();
            GUILayout.Space(10);
            DrawAtlasOpportunitiesSection();
            GUILayout.Space(10);
            DrawProceduralCandidatesSection();

            EditorGUILayout.EndScrollView();
        }

        // ── Texture import fixes ────────────────────────────────────

        void DrawTextureIssuesSection()
        {
            SectionLabel($"Texture Import Issues — {_textureIssues.Count} flagged, " +
                          $"est. {FormatBytes(_textureIssues.Sum(t => t.estSavingsBytes))} reclaimable");

            if (_textureIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("No texture import issues found.", MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool allSel = _textureIssues.All(t => t.selected);
            if (GUILayout.Button(allSel ? "Deselect All" : "Select All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                foreach (var t in _textureIssues) t.selected = !allSel;

            GUILayout.Space(10);
            _fixCompression = GUILayout.Toggle(_fixCompression, "Enable Compression", EditorStyles.toolbarButton, GUILayout.Width(135));
            _fixSpriteMipmaps = GUILayout.Toggle(_fixSpriteMipmaps, "Disable Sprite Mipmaps", EditorStyles.toolbarButton, GUILayout.Width(145));
            _fixReadWrite = GUILayout.Toggle(_fixReadWrite, "Clear Read/Write", EditorStyles.toolbarButton, GUILayout.Width(120));
            GUILayout.FlexibleSpace();

            int selCount = _textureIssues.Count(t => t.selected);
            using (new EditorGUI.DisabledScope(selCount == 0))
            {
                if (GUILayout.Button($"Apply to Selected ({selCount})", EditorStyles.toolbarButton, GUILayout.Width(155)))
                {
                    if (EditorUtility.DisplayDialog("Apply Texture Fixes",
                        $"Reimport {selCount} texture(s) with the checked fixes above?",
                        "Apply", "Cancel"))
                    {
                        FixTextureIssuesBatch(_textureIssues.Where(t => t.selected).ToList(),
                            _fixCompression, _fixSpriteMipmaps, _fixReadWrite);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            foreach (var t in _textureIssues.OrderByDescending(t => t.estSavingsBytes))
                DrawTextureIssueRow(t);
        }

        void DrawTextureIssueRow(TextureIssue t)
        {
            EditorGUILayout.BeginHorizontal();
            t.selected = GUILayout.Toggle(t.selected, "", GUILayout.Width(18));
            if (GUILayout.Button(t.name, _styleLink, GUILayout.Width(220))) PingAsset(t.path);
            GUILayout.Label($"{t.width}×{t.height}  {t.sizeLabel}", EditorStyles.miniLabel, GUILayout.Width(150));
            GUILayout.Label(string.Join(", ", t.problems), EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label($"~{FormatBytes(t.estSavingsBytes)}", EditorStyles.miniBoldLabel, GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();
        }

        // ── Audio import fixes ──────────────────────────────────────

        void DrawAudioIssuesSection()
        {
            SectionLabel($"Audio Import Issues — {_audioIssues.Count} flagged, " +
                          $"est. {FormatBytes(_audioIssues.Sum(a => a.estSavingsBytes))} reclaimable");

            if (_audioIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("No uncompressed audio clips found.", MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool allSel = _audioIssues.All(a => a.selected);
            if (GUILayout.Button(allSel ? "Deselect All" : "Select All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                foreach (var a in _audioIssues) a.selected = !allSel;

            GUILayout.Space(10);
            GUILayout.Label("Vorbis quality", EditorStyles.miniLabel, GUILayout.Width(82));
            _audioQuality = GUILayout.HorizontalSlider(_audioQuality, 0.1f, 1f, GUILayout.Width(100));
            GUILayout.Label($"{_audioQuality:P0}", EditorStyles.miniLabel, GUILayout.Width(38));
            GUILayout.FlexibleSpace();

            int selCount = _audioIssues.Count(a => a.selected);
            using (new EditorGUI.DisabledScope(selCount == 0))
            {
                if (GUILayout.Button($"Apply to Selected ({selCount})", EditorStyles.toolbarButton, GUILayout.Width(155)))
                {
                    if (EditorUtility.DisplayDialog("Apply Audio Fixes",
                        $"Convert {selCount} clip(s) to Vorbis at {_audioQuality:P0} quality?",
                        "Apply", "Cancel"))
                    {
                        FixAudioIssuesBatch(_audioIssues.Where(a => a.selected).ToList(), _audioQuality);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            foreach (var a in _audioIssues.OrderByDescending(a => a.estSavingsBytes))
                DrawAudioIssueRow(a);
        }

        void DrawAudioIssueRow(AudioIssue a)
        {
            EditorGUILayout.BeginHorizontal();
            a.selected = GUILayout.Toggle(a.selected, "", GUILayout.Width(18));
            if (GUILayout.Button(a.name, _styleLink, GUILayout.Width(220))) PingAsset(a.path);
            GUILayout.Label(a.sizeLabel, EditorStyles.miniLabel, GUILayout.Width(70));
            GUILayout.Label(a.problem, EditorStyles.miniLabel);
            GUILayout.Label($"~{FormatBytes(a.estSavingsBytes)}", EditorStyles.miniBoldLabel, GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();
        }

        // ── Atlas opportunities ──────────────────────────────────────

        void DrawAtlasOpportunitiesSection()
        {
            SectionLabel($"Atlas Opportunities — {_atlasOpportunities.Count} folder group(s)");
            EditorGUILayout.HelpBox(
                "Sprites in these folders aren't packed into a Sprite Atlas yet. Atlasing trims " +
                "per-texture padding/overhead and cuts draw calls. Pick the groups worth bundling — " +
                "this only creates new .spriteatlas assets; existing textures and references are untouched.",
                MessageType.None);

            if (_atlasOpportunities.Count == 0)
            {
                EditorGUILayout.HelpBox("No atlas opportunities found.", MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool allSel = _atlasOpportunities.All(g => g.selected);
            if (GUILayout.Button(allSel ? "Deselect All" : "Select All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                foreach (var g in _atlasOpportunities) g.selected = !allSel;
            GUILayout.FlexibleSpace();

            int selCount = _atlasOpportunities.Count(g => g.selected);
            using (new EditorGUI.DisabledScope(selCount == 0))
            {
                if (GUILayout.Button($"Create Atlas(es) ({selCount})", EditorStyles.toolbarButton, GUILayout.Width(160)))
                {
                    if (EditorUtility.DisplayDialog("Create Sprite Atlases",
                        $"Create {selCount} new .spriteatlas asset(s) and add the listed sprites as packables?\n\n" +
                        "Existing texture files and references are left untouched.",
                        "Create", "Cancel"))
                    {
                        CreateAtlasesBatch(_atlasOpportunities.Where(g => g.selected).ToList());
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            foreach (var g in _atlasOpportunities)
                DrawAtlasGroupRow(g);
        }

        void DrawAtlasGroupRow(AtlasOpportunity g)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            g.selected = GUILayout.Toggle(g.selected, "", GUILayout.Width(18));
            g.expanded = EditorGUILayout.Foldout(g.expanded,
                $"{g.folder}  —  {g.paths.Count} sprites, {FormatBytes(g.totalBytes)}", true);
            EditorGUILayout.EndHorizontal();
            if (!g.expanded) return;

            EditorGUI.indentLevel++;
            foreach (var p in g.paths)
                if (GUILayout.Button(p, EditorStyles.linkLabel)) PingAsset(p);
            EditorGUI.indentLevel--;
        }

        // ── Procedural replacement candidates ───────────────────────

        void DrawProceduralCandidatesSection()
        {
            SectionLabel($"Procedural Replacement Candidates — {_proceduralCandidates.Count} flagged");
            EditorGUILayout.HelpBox(
                "Filename heuristic (glow / shadow / vignette / gradient / blur / halo / AO, etc.). " +
                "These shapes are often cheaper as a tiny procedural shader than a baked texture. " +
                "\"Generate Starter\" drops a shader + material scaffold next to the original for you " +
                "to tune and swap in manually — it never touches the original asset or its references.",
                MessageType.None);

            if (_proceduralCandidates.Count == 0)
            {
                EditorGUILayout.HelpBox("No filename matches found.", MessageType.None);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool allSel = _proceduralCandidates.All(c => c.selected);
            if (GUILayout.Button(allSel ? "Deselect All" : "Select All", EditorStyles.toolbarButton, GUILayout.Width(90)))
                foreach (var c in _proceduralCandidates) c.selected = !allSel;
            GUILayout.FlexibleSpace();

            int selCount = _proceduralCandidates.Count(c => c.selected);
            using (new EditorGUI.DisabledScope(selCount == 0))
            {
                if (GUILayout.Button($"Generate Starter(s) ({selCount})", EditorStyles.toolbarButton, GUILayout.Width(170)))
                    GenerateProceduralStartersBatch(_proceduralCandidates.Where(c => c.selected).ToList());
            }
            EditorGUILayout.EndHorizontal();

            foreach (var c in _proceduralCandidates)
            {
                EditorGUILayout.BeginHorizontal();
                c.selected = GUILayout.Toggle(c.selected, "", GUILayout.Width(18));
                if (GUILayout.Button(c.name, _styleLink, GUILayout.Width(220))) PingAsset(c.path);
                GUILayout.Label(FormatBytes(c.sizeBytes), EditorStyles.miniLabel, GUILayout.Width(70));
                GUILayout.Label(c.reason, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SCAN
        // ═══════════════════════════════════════════════════════════

        void RunScan()
        {
            _scanning = true;
            _progress = 0f;
            _allAssets.Clear(); _dupTextures.Clear(); _dupMeshes.Clear();
            _dupMaterials.Clear(); _unused.Clear();
            _resources.Clear(); _streaming.Clear(); _addressables.Clear();
            _textureIssues.Clear(); _audioIssues.Clear();
            _atlasOpportunities.Clear(); _proceduralCandidates.Clear();
            Repaint();

            // ── 1. Addressable GUIDs ──────────────────────────────
            var addrGuids = CollectAddressableGuids();

            var allPaths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/") && !AssetDatabase.IsValidFolder(p))
                .ToArray();

            if (_buildAssetsOnly)
            {
                _progressLabel = "Resolving build footprint…";
                Repaint();
                var buildSet = CollectBuildAssetPaths(allPaths, addrGuids);
                allPaths = allPaths
                    .Where(p => buildSet.Contains(p) && !p.Contains("/Editor/"))
                    .ToArray();
            }
            int total = allPaths.Length;

            // ── 2. Reverse dependency map: path → set of direct referrers ──
            _progressLabel = "Building dependency map…";
            var reverseMap = new Dictionary<string, HashSet<string>>();
            for (int i = 0; i < total; i++)
            {
                if (i % 60 == 0) { _progress = 0.05f + 0.30f * i / total; _progressLabel = $"Dependencies… {i}/{total}"; Repaint(); }
                foreach (var dep in AssetDatabase.GetDependencies(allPaths[i], false))
                {
                    if (dep == allPaths[i]) continue;
                    if (!reverseMap.ContainsKey(dep)) reverseMap[dep] = new HashSet<string>();
                    reverseMap[dep].Add(allPaths[i]);
                }
            }

            // ── 3. Scene references ───────────────────────────────
            _progressLabel = "Scanning scenes…";
            var sceneRefs = new Dictionary<string, List<string>>();
            foreach (var scene in allPaths.Where(p => p.EndsWith(".unity")))
                foreach (var dep in AssetDatabase.GetDependencies(scene, true))
                {
                    if (!sceneRefs.ContainsKey(dep)) sceneRefs[dep] = new List<string>();
                    sceneRefs[dep].Add(Path.GetFileNameWithoutExtension(scene));
                }

            // ── 4. Build records + hash maps ──────────────────────
            var texHashes = new Dictionary<string, List<string>>();
            var meshHashes = new Dictionary<string, List<string>>();
            var matSigs = new Dictionary<string, List<string>>();

            for (int i = 0; i < total; i++)
            {
                if (i % 80 == 0) { _progress = 0.35f + 0.55f * i / total; _progressLabel = $"Processing assets… {i}/{total}"; Repaint(); }

                string path = allPaths[i];
                var fi = new FileInfo(path);
                long sz = fi.Exists ? fi.Length : 0;
                string guid = AssetDatabase.AssetPathToGUID(path);
                var type = ClassifyType(path);

                var rec = new AssetRecord
                {
                    path = path,
                    name = Path.GetFileName(path),
                    type = type,
                    sizeBytes = sz,
                    sizeLabel = FormatBytes(sz),
                    inResources = path.Contains("/Resources/"),
                    inStreamingAssets = path.Contains("/StreamingAssets/"),
                    isAddressable = addrGuids.Contains(guid),
                };

                if (sceneRefs.TryGetValue(path, out var scenes))
                    rec.usedInScenes.AddRange(scenes);

                if (reverseMap.TryGetValue(path, out var directRefs))
                    rec.directRefs.AddRange(directRefs);

                // Indirect = 2nd-hop referrers (assets that reference assets that reference this)
                if (reverseMap.TryGetValue(path, out var direct))
                {
                    var indirect = new HashSet<string>();
                    foreach (var dr in direct)
                        if (reverseMap.TryGetValue(dr, out var secondHop))
                            foreach (var s in secondHop)
                                if (s != path) indirect.Add(s);
                    rec.indirectRefs.AddRange(indirect);
                }

                rec.isUnused = rec.usedInScenes.Count == 0
                            && rec.directRefs.Count == 0
                            && !rec.inResources
                            && !rec.isAddressable;

                if (rec.isUnused) _unused.Add(rec);
                if (rec.inResources) _resources.Add(rec);
                if (rec.inStreamingAssets) _streaming.Add(rec);
                if (rec.isAddressable) _addressables.Add(rec);

                _allAssets.Add(rec);

                // Hashes for duplicate detection
                if (type == "Texture" && fi.Exists)
                {
                    var h = ComputeFileHash(path);
                    if (!texHashes.ContainsKey(h)) texHashes[h] = new List<string>();
                    texHashes[h].Add(path);

                    AnalyzeTextureIssue(path, sz, _textureIssues);
                }
                if (type == "Mesh" && fi.Exists)
                {
                    var h = ComputeFileHash(path);
                    if (!meshHashes.ContainsKey(h)) meshHashes[h] = new List<string>();
                    meshHashes[h].Add(path);
                }
                if (type == "Audio" && fi.Exists)
                {
                    AnalyzeAudioIssue(path, sz, _audioIssues);
                }
                if (path.EndsWith(".mat"))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat != null)
                    {
                        var sig = GetMaterialTextureSig(mat);
                        if (sig != null)
                        {
                            if (!matSigs.ContainsKey(sig)) matSigs[sig] = new List<string>();
                            matSigs[sig].Add(path);
                        }
                    }
                }
            }

            _allAssets.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));
            _unused.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));

            // ── 5. Duplicate groups ───────────────────────────────
            foreach (var kv in texHashes.Where(x => x.Value.Count > 1))
            {
                long sz = new FileInfo(kv.Value[0]).Length;
                _dupTextures.Add(new DuplicateGroup
                {
                    hash = kv.Key,
                    assetType = "Texture",
                    sizeBytes = sz,
                    paths = kv.Value,
                    wastedLabel = FormatBytes(sz * (kv.Value.Count - 1)) + " wasted"
                });
            }
            _dupTextures.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));

            foreach (var kv in meshHashes.Where(x => x.Value.Count > 1))
            {
                long sz = new FileInfo(kv.Value[0]).Length;
                _dupMeshes.Add(new DuplicateGroup
                {
                    hash = kv.Key,
                    assetType = "Mesh",
                    sizeBytes = sz,
                    paths = kv.Value,
                    wastedLabel = FormatBytes(sz * (kv.Value.Count - 1)) + " wasted"
                });
            }
            _dupMeshes.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));

            foreach (var kv in matSigs.Where(x => x.Value.Count > 1))
                _dupMaterials.Add(new MaterialDupGroup { textureSignature = kv.Key, materialPaths = kv.Value });

            // ── 6. Optimization opportunities ─────────────────────
            _progressLabel = "Looking for optimization opportunities…";
            Repaint();
            FindAtlasOpportunities(allPaths);
            FindProceduralCandidates(_allAssets.Where(a => a.type == "Texture"));
            _textureIssues.Sort((a, b) => b.estSavingsBytes.CompareTo(a.estSavingsBytes));
            _audioIssues.Sort((a, b) => b.estSavingsBytes.CompareTo(a.estSavingsBytes));

            _progressLabel = "Done";
            _progress = 1f;
            _scanning = false;
            Repaint();
        }

        // ═══════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════

        static void SectionLabel(string text)
        {
            var s = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            GUILayout.Space(4);
            GUILayout.Label(text, s);
            GUILayout.Space(2);
        }

        static void PingAsset(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
        }

        static string ClassifyType(string path)
        {
            return Path.GetExtension(path).ToLower() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd"
                    or ".bmp" or ".gif" or ".hdr" or ".exr" => "Texture",
                ".fbx" or ".obj" or ".blend" or ".dae" or ".3ds" => "Mesh",
                ".wav" or ".mp3" or ".ogg" or ".aiff" or ".flac" => "Audio",
                ".mat" => "Material",
                ".prefab" => "Prefab",
                ".cs" => "Script",
                _ => "Other"
            };
        }

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024) return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }

        static string ComputeFileHash(string path)
        {
            using var md5 = MD5.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
        }

        static string GetMaterialTextureSig(Material mat)
        {
            var parts = new List<string>();
            foreach (var id in mat.GetTexturePropertyNameIDs())
            {
                var tex = mat.GetTexture(id);
                if (tex != null) parts.Add(AssetDatabase.GetAssetPath(tex));
            }
            if (parts.Count == 0) return null;
            parts.Sort();
            return string.Join("|", parts);
        }

        static HashSet<string> CollectAddressableGuids()
        {
            var guids = new HashSet<string>();
#if UNITY_ADDRESSABLES
        try
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null)
                foreach (var group in settings.groups)
                    if (group != null)
                        foreach (var entry in group.entries)
                            guids.Add(entry.guid);
        }
        catch { }
#endif
            return guids;
        }

        // Resolves the set of asset paths that actually ship in a Player build:
        //   • dependencies of scenes that are *enabled* in Build Settings
        //   • Resources/ content (force-included by Unity regardless of refs)
        //   • StreamingAssets/ content (copied verbatim, not a tracked dependency)
        //   • Addressable entries (built into their own bundles, shipped alongside the player)
        static HashSet<string> CollectBuildAssetPaths(string[] allPaths, HashSet<string> addrGuids)
        {
            var result = new HashSet<string>();

            var buildScenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            foreach (var scene in buildScenes)
            {
                result.Add(scene);
                foreach (var dep in AssetDatabase.GetDependencies(scene, true))
                    result.Add(dep);
            }

            foreach (var p in allPaths)
            {
                if (!p.Contains("/Resources/")) continue;
                result.Add(p);
                foreach (var dep in AssetDatabase.GetDependencies(p, true))
                    result.Add(dep);
            }

            foreach (var p in allPaths)
                if (p.Contains("/StreamingAssets/"))
                    result.Add(p);

            foreach (var p in allPaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(p);
                if (!addrGuids.Contains(guid)) continue;
                result.Add(p);
                foreach (var dep in AssetDatabase.GetDependencies(p, true))
                    result.Add(dep);
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  OPTIMIZE — TEXTURE IMPORT ANALYSIS & FIXES
        // ═══════════════════════════════════════════════════════════

        static void AnalyzeTextureIssue(string path, long sizeBytes, List<TextureIssue> outList)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            var problems = new List<string>();
            long estSavings = 0;
            bool isSprite = importer.textureType == TextureImporterType.Sprite;

            if (importer.textureCompression == TextureImporterCompression.Uncompressed)
            {
                problems.Add("Uncompressed");
                estSavings += (long)(sizeBytes * 0.6f);   // rough heuristic — actual ratio depends on content/platform
            }

            if (importer.mipmapEnabled && isSprite)
            {
                problems.Add("Mipmaps on UI sprite");
                estSavings += (long)(sizeBytes * 0.33f);  // mip chain adds ~33% on top of the base level
            }

            if (importer.isReadable)
                problems.Add("Read/Write enabled (2× runtime memory, no disk cost)");

            int w = 0, h = 0;
            try { importer.GetSourceTextureWidthAndHeight(out w, out h); } catch { }
            if (Mathf.Max(w, h) >= 2048)
                problems.Add($"Large source ({w}×{h}) — confirm it needs to be this big");

            if (problems.Count == 0) return;

            outList.Add(new TextureIssue
            {
                path = path,
                name = Path.GetFileName(path),
                sizeBytes = sizeBytes,
                sizeLabel = FormatBytes(sizeBytes),
                width = w,
                height = h,
                problems = problems,
                estSavingsBytes = estSavings
            });
        }

        void FixTextureIssuesBatch(List<TextureIssue> items, bool enableCompression, bool disableSpriteMipmaps, bool clearReadWrite)
        {
            int fixedCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var item in items)
                {
                    var importer = AssetImporter.GetAtPath(item.path) as TextureImporter;
                    if (importer == null) continue;
                    bool changed = false;

                    if (enableCompression && importer.textureCompression == TextureImporterCompression.Uncompressed)
                    {
                        importer.textureCompression = TextureImporterCompression.Compressed;
                        changed = true;
                    }
                    if (disableSpriteMipmaps && importer.textureType == TextureImporterType.Sprite && importer.mipmapEnabled)
                    {
                        importer.mipmapEnabled = false;
                        changed = true;
                    }
                    if (clearReadWrite && importer.isReadable)
                    {
                        importer.isReadable = false;
                        changed = true;
                    }

                    if (changed)
                    {
                        importer.SaveAndReimport();
                        fixedCount++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log($"[AssetSizeAnalyzer] Applied texture import fixes to {fixedCount}/{items.Count} asset(s).");
            RunScan();
        }

        // ═══════════════════════════════════════════════════════════
        //  OPTIMIZE — AUDIO IMPORT ANALYSIS & FIXES
        // ═══════════════════════════════════════════════════════════

        static void AnalyzeAudioIssue(string path, long sizeBytes, List<AudioIssue> outList)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return;

            var settings = importer.defaultSampleSettings;
            if (settings.compressionFormat != AudioCompressionFormat.PCM) return;
            if (sizeBytes < 100_000) return; // not worth flagging tiny blips

            outList.Add(new AudioIssue
            {
                path = path,
                name = Path.GetFileName(path),
                sizeBytes = sizeBytes,
                sizeLabel = FormatBytes(sizeBytes),
                problem = "Uncompressed PCM",
                estSavingsBytes = (long)(sizeBytes * 0.65f) // Vorbis typically lands well below raw PCM
            });
        }

        void FixAudioIssuesBatch(List<AudioIssue> items, float quality)
        {
            int fixedCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var item in items)
                {
                    var importer = AssetImporter.GetAtPath(item.path) as AudioImporter;
                    if (importer == null) continue;

                    var s = importer.defaultSampleSettings;
                    s.compressionFormat = AudioCompressionFormat.Vorbis;
                    s.quality = quality;
                    importer.defaultSampleSettings = s;
                    importer.SaveAndReimport();
                    fixedCount++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log($"[AssetSizeAnalyzer] Switched {fixedCount}/{items.Count} audio clip(s) to Vorbis @ {quality:P0}.");
            RunScan();
        }

        // ═══════════════════════════════════════════════════════════
        //  OPTIMIZE — SPRITE ATLAS OPPORTUNITIES
        // ═══════════════════════════════════════════════════════════

        void FindAtlasOpportunities(string[] allPaths)
        {
            var atlased = CollectAtlasedTexturePaths(allPaths);

            var candidates = allPaths.Where(p =>
            {
                if (atlased.Contains(p)) return false;
                var importer = AssetImporter.GetAtPath(p) as TextureImporter;
                return importer != null && importer.textureType == TextureImporterType.Sprite;
            });

            foreach (var group in candidates.GroupBy(Path.GetDirectoryName))
            {
                var paths = group.ToList();
                if (paths.Count < 3) continue; // not worth a dedicated atlas

                long total = paths.Sum(p => { var fi = new FileInfo(p); return fi.Exists ? fi.Length : 0; });
                _atlasOpportunities.Add(new AtlasOpportunity { folder = group.Key, paths = paths, totalBytes = total });
            }

            _atlasOpportunities.Sort((a, b) => b.totalBytes.CompareTo(a.totalBytes));
        }

        static HashSet<string> CollectAtlasedTexturePaths(string[] allPaths)
        {
            var result = new HashSet<string>();
            foreach (var p in allPaths.Where(p => p.EndsWith(".spriteatlas")))
            {
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(p);
                if (atlas == null) continue;
                try
                {
                    foreach (var obj in atlas.GetPackables())
                    {
                        if (obj == null) continue;
                        string op = AssetDatabase.GetAssetPath(obj);
                        if (!string.IsNullOrEmpty(op)) result.Add(op);
                    }
                }
                catch { }
            }
            return result;
        }

        void CreateAtlasesBatch(List<AtlasOpportunity> groups)
        {
            int created = 0;
            foreach (var g in groups)
            {
                try
                {
                    string atlasPath = AssetDatabase.GenerateUniqueAssetPath(
                        $"{g.folder}/{Path.GetFileName(g.folder)}_Atlas.spriteatlas");

                    var atlas = new SpriteAtlas();
                    var objs = g.paths
                        .Select(p => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p))
                        .Where(o => o != null)
                        .ToArray();

                    SpriteAtlasExtensions.Add(atlas, objs);
                    AssetDatabase.CreateAsset(atlas, atlasPath);
                    created++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AssetSizeAnalyzer] Could not create atlas for {g.folder}: {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AssetSizeAnalyzer] Created {created}/{groups.Count} Sprite Atlas asset(s). " +
                      "Review packing settings, then rebuild the atlas cache to measure actual savings.");
            RunScan();
        }

        // ═══════════════════════════════════════════════════════════
        //  OPTIMIZE — PROCEDURAL REPLACEMENT CANDIDATES
        // ═══════════════════════════════════════════════════════════

        static readonly string[] ProceduralKeywords =
        {
        "glow", "shadow", "vignette", "gradient", "blur", "halo", "ring",
        "softbox", "soft_box", "ao", "ambientocclusion", "radial", "falloff", "fog", "haze"
    };

        void FindProceduralCandidates(IEnumerable<AssetRecord> textures)
        {
            foreach (var t in textures)
            {
                string lower = Path.GetFileNameWithoutExtension(t.name).ToLowerInvariant();
                string hit = ProceduralKeywords.FirstOrDefault(k => lower.Contains(k));
                if (hit == null) continue;

                _proceduralCandidates.Add(new ProceduralCandidate
                {
                    path = t.path,
                    name = t.name,
                    sizeBytes = t.sizeBytes,
                    reason = $"Filename matches \"{hit}\" — often cheaper as a procedural shader than a baked texture"
                });
            }
        }

        void GenerateProceduralStartersBatch(List<ProceduralCandidate> items)
        {
            int created = 0;
            foreach (var item in items)
            {
                try
                {
                    string parentDir = Path.GetDirectoryName(item.path);
                    string proceduralDir = parentDir + "/Procedural";
                    if (!AssetDatabase.IsValidFolder(proceduralDir))
                        AssetDatabase.CreateFolder(parentDir, "Procedural");

                    string baseName = Path.GetFileNameWithoutExtension(item.path);
                    string shaderPath = AssetDatabase.GenerateUniqueAssetPath($"{proceduralDir}/{baseName}_Procedural.shader");
                    string matPath = Path.ChangeExtension(shaderPath, ".mat");

                    File.WriteAllText(Path.GetFullPath(shaderPath), ProceduralShaderTemplate(baseName));
                    AssetDatabase.ImportAsset(shaderPath);

                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                    if (shader != null)
                    {
                        var mat = new Material(shader);
                        AssetDatabase.CreateAsset(mat, matPath);
                        created++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AssetSizeAnalyzer] Could not generate procedural starter for {item.path}: {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AssetSizeAnalyzer] Generated {created}/{items.Count} procedural starter shader/material pair(s). " +
                      "These are scaffolding only — tune the parameters and swap references in manually; " +
                      "the original textures were left untouched.");
        }

        static string ProceduralShaderTemplate(string baseName) =>
    $@"Shader ""Custom/Procedural_{baseName}""
{{
    // Generated starter — a generic soft radial falloff. Good base for glow, vignette,
    // soft shadow blobs, AO blots, etc. Tune _Radius/_Softness/_Intensity per use case,
    // or swap the falloff function entirely if the shape needs to be a box, ring, etc.
    Properties
    {{
        _Color (""Color"", Color) = (1,1,1,1)
        _Radius (""Radius"", Range(0,1)) = 0.5
        _Softness (""Softness"", Range(0.001,1)) = 0.4
        _Intensity (""Intensity"", Range(0,4)) = 1
    }}
    SubShader
    {{
        Tags {{ ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }}
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata {{ float4 vertex : POSITION; float2 uv : TEXCOORD0; }};
            struct v2f {{ float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; }};

            fixed4 _Color;
            float _Radius, _Softness, _Intensity;

            v2f vert (appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }}

            fixed4 frag (v2f i) : SV_Target
            {{
                float d = distance(i.uv, float2(0.5, 0.5)) * 2;
                float a = 1 - smoothstep(_Radius, _Radius + _Softness, d);
                fixed4 c = _Color;
                c.a *= saturate(a * _Intensity);
                return c;
            }}
            ENDCG
        }}
    }}
}}
";

        // ── CSV ───────────────────────────────────────────────────

        void ExportCSV()
        {
            string savePath = EditorUtility.SaveFilePanel("Export CSV", "", "AssetReport.csv", "csv");
            if (string.IsNullOrEmpty(savePath)) return;
            using var w = new StreamWriter(savePath);
            w.WriteLine("Path,Type,SizeBytes,SizeLabel,Scenes,DirectRefs,IndirectRefs,InResources,InStreaming,Addressable,Unused");
            foreach (var a in _allAssets)
                w.WriteLine($"\"{a.path}\",{a.type},{a.sizeBytes},{a.sizeLabel}," +
                            $"\"{string.Join(";", a.usedInScenes)}\"," +
                            $"{a.directRefs.Count},{a.indirectRefs.Count}," +
                            $"{a.inResources},{a.inStreamingAssets},{a.isAddressable},{a.isUnused}");
            Debug.Log($"[AssetSizeAnalyzer] Exported → {savePath}");
        }
    }
}
