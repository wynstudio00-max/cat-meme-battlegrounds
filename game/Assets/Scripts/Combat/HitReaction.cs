using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Visual reaction of anything with <see cref="Health"/> (added by Health itself, never on a batch-mode server):
    /// the model flashes bright red for a moment on every hit and plays the cartoon <see cref="KillFx"/> burst when it dies.
    /// Runs on every machine from the replicated Health events, so everybody sees it. Uses a MaterialPropertyBlock on
    /// _BaseColor (URP Lit), no material copies.
    /// </summary>
    public class HitReaction : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color FlashColor = new Color(2.4f, 0.75f, 0.65f);   // >1 brightens the texture: a hot red-pink flash
        const float FlashTime = 0.16f;

        Health health;
        Renderer[] renderers;
        MaterialPropertyBlock block;
        float flashStart = -10f;
        bool flashing;

        void Awake()
        {
            health = GetComponent<Health>();
            block = new MaterialPropertyBlock();
        }

        void OnEnable()
        {
            if (health == null) return;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
        }

        void OnDisable()
        {
            if (health == null) return;
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
        }

        void OnDamaged(float amount, GameObject attacker)
        {
            flashStart = Time.time;
            flashing = true;
        }

        void OnDied(GameObject attacker) => KillFx.Play(Chest());

        Vector3 Chest()
        {
            Collect();
            foreach (var r in renderers)
                if (r is SkinnedMeshRenderer && r.enabled) return r.bounds.center;
            return transform.position + Vector3.up * 0.6f;
        }

        void Collect()
        {
            if (renderers != null) return;
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (!(r is ParticleSystemRenderer) && !(r is LineRenderer) && !(r is TrailRenderer)) list.Add(r);
            renderers = list.ToArray();
        }

        void LateUpdate()
        {
            if (!flashing) return;
            Collect();
            float t = (Time.time - flashStart) / FlashTime;
            if (t >= 1f)
            {
                flashing = false;
                foreach (var r in renderers) if (r != null) r.SetPropertyBlock(null);
                return;
            }
            var c = Color.Lerp(FlashColor, Color.white, t * t);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetColor(BaseColor, c);
                r.SetPropertyBlock(block);
            }
        }
    }
}
