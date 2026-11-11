using UnityEngine;

namespace CatBattleground
{
    /// <summary>Marker: a place where loot may appear when a match starts.</summary>
    public class LootSpawnPoint : MonoBehaviour
    {
        void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.3f, new Vector3(0.5f, 0.5f, 0.5f));
        }
    }
}
