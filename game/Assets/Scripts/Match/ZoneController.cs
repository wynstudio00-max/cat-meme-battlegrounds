using System;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// The shrinking safe zone. The server runs the phases and damages cats outside the circle; clients only draw it.
    /// Each phase: wait (the next circle is shown on the map), then shrink to that circle while taking damage outside.
    /// </summary>
    public class ZoneController : NetworkBehaviour
    {
        [Serializable]
        public struct Phase
        {
            public float waitSeconds;
            public float shrinkSeconds;
            public float endRadius;
            public float damagePerSecond;
        }

        [SerializeField] Vector3 arenaCenter = Vector3.zero;
        [SerializeField] float startRadius = 110f;
        [SerializeField]
        Phase[] phases =
        {
            new Phase { waitSeconds = 60, shrinkSeconds = 60, endRadius = 76f, damagePerSecond = 1f },
            new Phase { waitSeconds = 45, shrinkSeconds = 50, endRadius = 48f, damagePerSecond = 2f },
            new Phase { waitSeconds = 35, shrinkSeconds = 40, endRadius = 24f, damagePerSecond = 4f },
            new Phase { waitSeconds = 30, shrinkSeconds = 30, endRadius = 8f,  damagePerSecond = 8f },
            new Phase { waitSeconds = 20, shrinkSeconds = 20, endRadius = 2f,  damagePerSecond = 15f },
        };
        [SerializeField] Material wallMaterial;
        [SerializeField] Material ringMaterial;

        readonly NetworkVariable<bool> active = new NetworkVariable<bool>(false);
        readonly NetworkVariable<Vector3> center = new NetworkVariable<Vector3>();
        readonly NetworkVariable<float> radius = new NetworkVariable<float>();
        readonly NetworkVariable<Vector3> nextCenter = new NetworkVariable<Vector3>();
        readonly NetworkVariable<float> nextRadius = new NetworkVariable<float>();
        readonly NetworkVariable<int> phaseIndex = new NetworkVariable<int>(-1);
        readonly NetworkVariable<bool> shrinking = new NetworkVariable<bool>();
        readonly NetworkVariable<float> secondsLeft = new NetworkVariable<float>();   // until the shrink starts / ends

        // server
        float phaseTimer;
        float damageTimer;
        float syncTimer;
        Vector3 shrinkFromCenter;
        float shrinkFromRadius;

        // client visuals
        GameObject wall;
        LineRenderer ring;
        Vector3 shownCenter;
        float shownRadius;

        public bool Active => active.Value;
        public Vector3 Center => center.Value;
        public float Radius => radius.Value;
        public int PhaseNumber => phaseIndex.Value + 1;
        public bool Shrinking => shrinking.Value;
        public float SecondsLeft => secondsLeft.Value;
        public float NextRadius => nextRadius.Value;
        public Vector3 NextCenter => nextCenter.Value;

        /// <summary>True when a world position is outside the current safe circle (XZ plane).</summary>
        public bool IsOutside(Vector3 position)
        {
            if (!active.Value) return false;
            var flat = position - center.Value;
            flat.y = 0f;
            return flat.magnitude > radius.Value;
        }

        // ---------------------------------------------------------------- server
        public void StartZone()
        {
            if (!IsServer) return;
            center.Value = arenaCenter;
            radius.Value = startRadius;
            phaseIndex.Value = -1;
            active.Value = true;
            BeginPhase(0);
        }

        public void StopZone()
        {
            if (!IsServer) return;
            active.Value = false;
            phaseIndex.Value = -1;
        }

        void BeginPhase(int index)
        {
            phaseIndex.Value = index;
            var phase = phases[index];
            shrinking.Value = false;
            phaseTimer = phase.waitSeconds;
            secondsLeft.Value = phaseTimer;

            // The next circle lies inside the current one.
            float room = Mathf.Max(0f, radius.Value - phase.endRadius);
            var offset = UnityEngine.Random.insideUnitCircle * room * 0.85f;
            nextCenter.Value = center.Value + new Vector3(offset.x, 0f, offset.y);
            nextRadius.Value = phase.endRadius;
        }

        void ServerUpdate()
        {
            if (!active.Value) return;
            int index = phaseIndex.Value;
            if (index < 0 || index >= phases.Length) return;
            var phase = phases[index];

            phaseTimer -= Time.deltaTime;
            syncTimer += Time.deltaTime;

            if (!shrinking.Value)
            {
                if (syncTimer >= 0.25f) { syncTimer = 0f; secondsLeft.Value = Mathf.Max(0f, phaseTimer); }
                if (phaseTimer <= 0f)
                {
                    shrinking.Value = true;
                    phaseTimer = phase.shrinkSeconds;
                    shrinkFromCenter = center.Value;
                    shrinkFromRadius = radius.Value;
                    secondsLeft.Value = phaseTimer;
                }
            }
            else
            {
                float t = 1f - Mathf.Clamp01(phaseTimer / Mathf.Max(0.01f, phase.shrinkSeconds));
                if (syncTimer >= 0.1f)
                {
                    syncTimer = 0f;
                    center.Value = Vector3.Lerp(shrinkFromCenter, nextCenter.Value, t);
                    radius.Value = Mathf.Lerp(shrinkFromRadius, nextRadius.Value, t);
                    secondsLeft.Value = Mathf.Max(0f, phaseTimer);
                }
                if (phaseTimer <= 0f)
                {
                    center.Value = nextCenter.Value;
                    radius.Value = nextRadius.Value;
                    if (index + 1 < phases.Length) BeginPhase(index + 1);
                    else { shrinking.Value = false; secondsLeft.Value = 0f; }   // final circle stays
                }
            }

            // Damage once per second to every living cat outside the circle.
            damageTimer += Time.deltaTime;
            if (damageTimer >= 1f)
            {
                damageTimer = 0f;
                float dps = phase.damagePerSecond;
                var match = MatchManager.Instance;
                if (match != null)
                {
                    foreach (var p in match.Players)
                        if (p != null && !p.Health.IsDead && IsOutside(p.transform.position)) p.Health.TakeDamage(dps, null);
                }
            }
        }

        // ---------------------------------------------------------------- client visuals
        public override void OnNetworkSpawn()
        {
            shownCenter = center.Value;
            shownRadius = radius.Value;
        }

        public override void OnNetworkDespawn()
        {
            if (wall != null) Destroy(wall);
            if (ring != null) Destroy(ring.gameObject);
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsServer) ServerUpdate();
            UpdateVisuals();
        }

        void UpdateVisuals()
        {
            bool show = active.Value && !Application.isBatchMode;
            if (wall != null) wall.SetActive(show);
            if (ring != null) ring.gameObject.SetActive(show);
            if (!show) return;

            if (wall == null) CreateVisuals();
            float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
            shownCenter = Vector3.Lerp(shownCenter, center.Value, k);
            shownRadius = Mathf.Lerp(shownRadius, radius.Value, k);

            wall.transform.position = new Vector3(shownCenter.x, 0f, shownCenter.z);
            wall.transform.localScale = new Vector3(shownRadius * 2f, 60f, shownRadius * 2f);
            DrawRing(nextCenter.Value, nextRadius.Value);
            if (ring.sharedMaterial != null)
            {
                // the next circle pulses so it is easy to find on the ground
                float p = 0.75f + 0.25f * Mathf.Sin(Time.time * 4f);
                ring.startColor = ring.endColor = new Color(1f, 1f, 1f, p);
            }
        }

        /// <summary>Client: the smoothed circle that is drawn (for effects that stick to the wall).</summary>
        public Vector3 ShownCenter => shownCenter;
        public float ShownRadius => shownRadius;

        void CreateVisuals()
        {
            wall = new GameObject("ZoneWall");
            wall.AddComponent<MeshFilter>().sharedMesh = BuildWallMesh(96);
            var renderer = wall.AddComponent<MeshRenderer>();
            // animated storm wall (FxAssets -> Resources/FX/ZoneWall.mat, shader CatBattleground/ZoneWall); old flat material as fallback
            var storm = Resources.Load<Material>("FX/ZoneWall");
            renderer.sharedMaterial = storm != null ? storm : wallMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var ringObject = new GameObject("ZoneNextRing");
            ring = ringObject.AddComponent<LineRenderer>();
            ring.loop = true;
            ring.useWorldSpace = true;
            ring.positionCount = 96;
            ring.startWidth = ring.endWidth = 0.4f;
            ring.sharedMaterial = ringMaterial != null ? ringMaterial : wallMaterial;
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void DrawRing(Vector3 c, float r)
        {
            for (int i = 0; i < 96; i++)
            {
                float a = i * Mathf.PI * 2f / 96f;
                var p = new Vector3(c.x + Mathf.Cos(a) * r, 0.3f, c.z + Mathf.Sin(a) * r);
                // follow the ground so the line is visible over hills
                if (Physics.Raycast(p + Vector3.up * 40f, Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y + 0.3f;
                ring.SetPosition(i, p);
            }
        }

        // Unit-radius open cylinder (diameter 1 -> scaled by the radius), visible from both sides via the material.
        static Mesh BuildWallMesh(int segments)
        {
            var vertices = new Vector3[(segments + 1) * 2];
            var triangles = new int[segments * 6];
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.5f;
                vertices[i * 2] = dir;
                vertices[i * 2 + 1] = dir + Vector3.up;
            }
            for (int i = 0; i < segments; i++)
            {
                int b = i * 2, t = i * 6;
                triangles[t] = b; triangles[t + 1] = b + 1; triangles[t + 2] = b + 2;
                triangles[t + 3] = b + 2; triangles[t + 4] = b + 1; triangles[t + 5] = b + 3;
            }
            var mesh = new Mesh { name = "ZoneWall", vertices = vertices, triangles = triangles };
            mesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f) * 4f);
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
