using System;
using System.Collections.Generic;
using System.Linq;

namespace Creatush.FilesProfiler
{
    public enum AssetCategory
    {
        Texture, Audio, Model, Material, Prefab,
        Animation, Shader, Font, ScriptableObject,
        Scene, Script, Other
    }

    public class CategoryDef
    {
        public AssetCategory Category;
        public string DisplayName;
        public string Icon;              // short glyph used in tab label
        public HashSet<string> Extensions;

        /// <summary>
        /// True if files of this type commonly embed GUID references to other
        /// assets (materials, prefabs, scenes, controllers, etc). These are the
        /// files scanned when building the "who references what" index.
        /// Pure binary leaf types (textures, audio, fonts) are skipped for speed —
        /// they never contain readable GUID text themselves.
        /// </summary>
        public bool IsReferenceBearing;
    }

    public static class AssetCategories
    {
        public static readonly List<CategoryDef> All = new()
        {
            new CategoryDef
            {
                Category = AssetCategory.Texture, DisplayName = "Textures", Icon = "🖼",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".png",".jpg",".jpeg",".tga",".psd",".tiff",".tif",
                      ".bmp",".exr",".hdr",".dds",".gif",".webp" },
                IsReferenceBearing = false,
            },
            new CategoryDef
            {
                Category = AssetCategory.Audio, DisplayName = "Audio", Icon = "🔊",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".wav",".mp3",".ogg",".aiff",".aif",".flac",".mod",".it",".s3m",".xm" },
                IsReferenceBearing = false,
            },
            new CategoryDef
            {
                Category = AssetCategory.Model, DisplayName = "Models", Icon = "◆",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".fbx",".obj",".blend",".dae",".3ds",".dxf",".c4d",".max",".ma",".mb" },
                IsReferenceBearing = false,
            },
            new CategoryDef
            {
                Category = AssetCategory.Material, DisplayName = "Materials", Icon = "◐",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mat" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Prefab, DisplayName = "Prefabs", Icon = "▣",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".prefab" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Animation, DisplayName = "Animation", Icon = "▶",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".anim",".controller",".overridecontroller",".mask",
                      ".playable",".signal" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Shader, DisplayName = "Shaders", Icon = "✦",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".shader",".shadergraph",".shadersubgraph",".compute",
                      ".cginc",".hlsl",".glsl" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Font, DisplayName = "Fonts", Icon = "A",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf",".otf",".fon",".fnt" },
                IsReferenceBearing = false,
            },
            new CategoryDef
            {
                Category = AssetCategory.ScriptableObject, DisplayName = "Data Assets", Icon = "▤",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".asset",".preset" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Scene, DisplayName = "Scenes", Icon = "▢",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".unity" },
                IsReferenceBearing = true,
            },
            new CategoryDef
            {
                Category = AssetCategory.Script, DisplayName = "Scripts", Icon = "{ }",
                Extensions = new(StringComparer.OrdinalIgnoreCase) { ".cs",".js",".boo" },
                IsReferenceBearing = false,
            },
            new CategoryDef
            {
                Category = AssetCategory.Other, DisplayName = "Other", Icon = "•",
                Extensions = new(StringComparer.OrdinalIgnoreCase)
                    { ".json",".xml",".txt",".csv",".pdf",".md" },
                IsReferenceBearing = true, // text-ish, may embed guids worth scanning
            },
        };

        static readonly Dictionary<string, CategoryDef> _extToCategory = BuildExtMap();

        static Dictionary<string, CategoryDef> BuildExtMap()
        {
            var map = new Dictionary<string, CategoryDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in All)
                foreach (var ext in def.Extensions)
                    map[ext] = def;
            return map;
        }

        /// <summary>Resolve a category for a given extension. Falls back to Other.</summary>
        public static CategoryDef Resolve(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return Get(AssetCategory.Other);
            return _extToCategory.TryGetValue(extension, out var def) ? def : Get(AssetCategory.Other);
        }

        public static CategoryDef Get(AssetCategory cat) => All.First(d => d.Category == cat);

        public static HashSet<string> AllKnownExtensions() =>
            new(All.SelectMany(d => d.Extensions), StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> ReferenceBearingExtensions() =>
            new(All.Where(d => d.IsReferenceBearing).SelectMany(d => d.Extensions),
                StringComparer.OrdinalIgnoreCase);
    }
}
