using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Server side: scatters pickups on a random subset of LootSpawnPoints at match start and removes them at the end.
    /// The prefabs must be registered as network prefabs (CatBattleground > Build Phase 2 Multiplayer Scene does that).
    /// </summary>
    public class LootSpawner : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public GameObject prefab;
            public float weight = 1f;
        }

        [SerializeField] List<Entry> table = new List<Entry>();
        [SerializeField, Range(0f, 1f)] float fillFraction = 0.75f;   // share of spawn points that get an item

        readonly List<NetworkObject> spawned = new List<NetworkObject>();

        public void SpawnAll()
        {
            DespawnAll();
            float total = 0f;
            foreach (var e in table) if (e.prefab != null) total += e.weight;
            if (total <= 0f) return;

            var points = new List<LootSpawnPoint>(FindObjectsByType<LootSpawnPoint>());
            Shuffle(points);
            int count = Mathf.RoundToInt(points.Count * fillFraction);
            for (int i = 0; i < count; i++)
            {
                var prefab = Pick(total);
                var obj = Instantiate(prefab, points[i].transform.position, Quaternion.identity);
                var net = obj.GetComponent<NetworkObject>();
                net.Spawn(true);
                spawned.Add(net);
            }
        }

        /// <summary>One pickup at a position (zombie drops). type null = picked by the table's weights.</summary>
        public void SpawnDrop(Vector3 position, LootType? type = null)
        {
            float total = 0f;
            foreach (var e in table) if (e.prefab != null) total += e.weight;
            if (total <= 0f) return;
            GameObject prefab = null;
            if (type.HasValue)
                foreach (var e in table)
                {
                    var item = e.prefab != null ? e.prefab.GetComponent<LootItem>() : null;
                    if (item != null && item.Type == type.Value) { prefab = e.prefab; break; }
                }
            if (prefab == null) prefab = Pick(total);
            var net = Instantiate(prefab, position, Quaternion.identity).GetComponent<NetworkObject>();
            net.Spawn(true);
            spawned.Add(net);
        }

        public void DespawnAll()
        {
            foreach (var net in spawned)
                if (net != null && net.IsSpawned) net.Despawn(true);
            spawned.Clear();
        }

        GameObject Pick(float total)
        {
            float r = UnityEngine.Random.value * total;
            foreach (var e in table)
            {
                if (e.prefab == null) continue;
                r -= e.weight;
                if (r <= 0f) return e.prefab;
            }
            return table[table.Count - 1].prefab;
        }

        static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
