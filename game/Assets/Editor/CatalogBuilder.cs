using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Creates / updates Assets/Data/CharacterCatalog.asset from the Player_*.prefab files.
    /// Existing entries keep whatever tier you set by hand; only missing cats are added with a default tier.
    /// Final line-up (2026-11-03, user): Common x3, Rare x3, Legendary x2, Original x1 (free).
    /// </summary>
    public static class CatalogBuilder
    {
        public const string CatalogPath = "Assets/Data/CharacterCatalog.asset";

        static readonly Dictionary<string, CharacterTier> DefaultTiers = new Dictionary<string, CharacterTier>
        {
            { "army", CharacterTier.Original },
            { "gold", CharacterTier.Common }, { "soldier", CharacterTier.Common }, { "baby", CharacterTier.Common },   // 2026-11-07: baby <-> ninja swapped
            { "ninja", CharacterTier.Rare }, { "nobi", CharacterTier.Rare }, { "black", CharacterTier.Rare },
            { "white", CharacterTier.Legendary }, { "knight", CharacterTier.Legendary },
            { "orange", CharacterTier.Common },   // hidden, not in the catalog
        };

        /// <summary>The cats' names as shown in the game = the names printed on the pass cards / NFT metadata
        /// (2026-11-05, user; was Baby Cashcat / Ninja Neko / Sgt Whisker / Cashcat). Applied on every catalog update.</summary>
        public static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            { "army", "Army Cat" }, { "baby", "Baby Cash Cat" }, { "black", "Hmm Cat" }, { "gold", "Golden Cat" },
            { "knight", "Bundle Cat" }, { "ninja", "Ninja Cat" }, { "soldier", "V." }, { "white", "Cash Cat" },
            { "nobi", "Nobi" },
        };

        /// <summary>Cats kept out of the game for now (not selectable, not used by bots). Files and prefabs stay; remove the id to bring one back.</summary>
        static readonly HashSet<string> Hidden = new HashSet<string> { "orange" };

        [MenuItem("CatBattleground/Update Character Catalog")]
        public static CharacterCatalog Build()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Data")) AssetDatabase.CreateFolder("Assets", "Data");
            var catalog = AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<CharacterCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab Player_", new[] { "Assets/Prefabs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(path);          // Player_Orange
                if (!name.StartsWith("Player_")) continue;
                var id = name.Substring("Player_".Length).ToLowerInvariant();   // orange
                if (Hidden.Contains(id)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                var entry = catalog.entries.Find(e => e.id == id);
                if (entry == null)
                {
                    entry = new CharacterCatalog.Entry
                    {
                        id = id,
                        displayName = char.ToUpperInvariant(id[0]) + id.Substring(1),
                        tier = DefaultTiers.TryGetValue(id, out var t) ? t : CharacterTier.Common,
                    };
                    catalog.entries.Add(entry);
                }
                entry.playerPrefab = prefab;   // always refresh the reference (prefabs are rebuilt by the setup menus)
                if (DisplayNames.TryGetValue(id, out var shown)) entry.displayName = shown;
            }

            catalog.entries.RemoveAll(e => Hidden.Contains(e.id));
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return catalog;
        }
    }
}
