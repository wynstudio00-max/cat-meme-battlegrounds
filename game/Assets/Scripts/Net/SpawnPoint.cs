using UnityEngine;

namespace CatBattleground
{
    /// <summary>Marker for player spawn positions. Players spawn facing the same way as the point's forward axis.</summary>
    public class SpawnPoint : MonoBehaviour
    {
        void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.5f, 0.5f);
            Gizmos.DrawRay(transform.position + Vector3.up * 0.5f, transform.forward * 1.5f);
        }
    }
}
