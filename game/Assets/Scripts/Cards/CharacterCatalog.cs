using System;
using System.Collections.Generic;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Data-driven list of every playable cat. A pass card only carries a tier; the game picks one of the
    /// characters in that tier. Edit the asset (Assets/Data/CharacterCatalog.asset) to move cats between tiers.
    /// </summary>
    [CreateAssetMenu(fileName = "CharacterCatalog", menuName = "CatBattleground/Character Catalog")]
    public class CharacterCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string id;                 // stable key stored with the card reveal, e.g. "orange"
            public string displayName;
            public CharacterTier tier;
            public GameObject playerPrefab;   // networked Player_* prefab
        }

        public List<Entry> entries = new List<Entry>();

        public Entry Original => entries.Find(e => e.tier == CharacterTier.Original);

        public Entry Get(string id) => entries.Find(e => e.id == id);

        public List<Entry> InTier(CharacterTier tier) => entries.FindAll(e => e.tier == tier && e.playerPrefab != null);
    }
}
