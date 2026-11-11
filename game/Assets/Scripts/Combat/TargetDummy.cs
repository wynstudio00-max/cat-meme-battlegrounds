using System.Collections;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>Shooting-range target: flashes on hit, falls over on death, respawns after a delay.</summary>
    [RequireComponent(typeof(Health))]
    public class TargetDummy : MonoBehaviour
    {
        [SerializeField] float respawnDelay = 3f;

        Health health;
        Renderer rend;
        Color baseColor;
        Quaternion upright;

        void Awake()
        {
            health = GetComponent<Health>();
            rend = GetComponentInChildren<Renderer>();
            if (rend != null) baseColor = rend.material.color;
            upright = transform.rotation;
        }

        void OnEnable()
        {
            health.Damaged += OnDamaged;
            health.Died += OnDied;
        }

        void OnDisable()
        {
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
        }

        void OnDamaged(float amount, GameObject attacker)
        {
            if (rend != null) StartCoroutine(Flash());
        }

        IEnumerator Flash()
        {
            rend.material.color = Color.white;
            yield return new WaitForSeconds(0.06f);
            rend.material.color = health.IsDead ? Color.gray : baseColor;
        }

        void OnDied(GameObject attacker) => StartCoroutine(DieAndRespawn());

        IEnumerator DieAndRespawn()
        {
            transform.rotation = upright * Quaternion.Euler(-90f, 0f, 0f);
            yield return new WaitForSeconds(respawnDelay);
            transform.rotation = upright;
            health.ResetHealth();
            if (rend != null) rend.material.color = baseColor;
        }
    }
}
