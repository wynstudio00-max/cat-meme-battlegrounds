using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Hitscan weapon. The shot is aimed from the camera centre (crosshair), tracer is drawn from the muzzle.
    /// Hold left mouse to fire, R to reload. Stats come from <see cref="Weapons"/>; every character has one fixed weapon
    /// (no pickups) and the weapon id is owned by the server, so a client cannot give itself a better gun.
    /// Online: the owner shows its own tracer immediately and sends the aim ray to the server; only the server
    /// raycasts and applies damage. Offline (no spawned NetworkObject) it behaves as before.
    /// </summary>
    public class PlayerShooter : NetworkBehaviour
    {
        [Header("Weapon")]
        [SerializeField] LayerMask hitMask = ~0;

        [Header("Refs")]
        [SerializeField] Transform muzzle;           // optional; falls back to chest height
        [SerializeField] Camera aimCamera;

        readonly NetworkVariable<int> weaponId = new NetworkVariable<int>(Weapons.Starter);
        int offlineWeaponId = Weapons.Starter;

        public int WeaponId => Online ? weaponId.Value : offlineWeaponId;
        public WeaponStats Weapon => Weapons.Get(WeaponId);
        public int Ammo { get; private set; }
        public int Reserve { get; private set; }
        public int MagazineSize => Weapon.magazine;
        public bool IsReloading { get; private set; }
        /// <summary>Owner: last time one of our shots hit something that can be hurt (HUD hit marker).</summary>
        public float LastHitTime { get; private set; } = -10f;

        PlayerController player;
        PlayerAnimator anim;
        Renderer rifle;
        MuzzleFlash fx;
        PlayerDrop drop;
        BombThrower bombs;
        Health ownHealth;
        float nextShotTime;
        float nextServerShotTime;
        LineRenderer tracer;
        Material tracerMaterial;
        Coroutine reloadRoutine;

        bool Online => IsSpawned;
        bool isBot;
        int botAmmo;
        float botReloadUntil;

        void Awake()
        {
            player = GetComponent<PlayerController>();
            anim = GetComponent<PlayerAnimator>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.name.ToLowerInvariant().Contains("rifl")) { rifle = r; break; }
            ownHealth = GetComponent<Health>();
            Ammo = Weapons.Get(Weapons.Starter).magazine;
            Reserve = Weapons.Get(Weapons.Starter).reserve;
            drop = GetComponent<PlayerDrop>();
            bombs = GetComponent<BombThrower>();
            CreateTracer();
        }

        void Start()
        {
            if (aimCamera == null) aimCamera = Camera.main;
        }

        public override void OnNetworkSpawn()
        {
            isBot = IsServer && GetComponent<BotBrain>() != null;
            botAmmo = Weapons.Get(weaponId.Value).magazine;
            Ammo = Weapon.magazine;
            Reserve = Weapon.reserve;
            weaponId.OnValueChanged += OnWeaponChanged;
        }

        public override void OnNetworkDespawn() => weaponId.OnValueChanged -= OnWeaponChanged;

        void OnWeaponChanged(int previous, int current)
        {
            // New gun comes with a full magazine and its spare rounds.
            Ammo = Weapons.Get(current).magazine;
            Reserve = Weapons.Get(current).reserve;
            CancelReload();
        }

        void CancelReload()
        {
            if (reloadRoutine != null) StopCoroutine(reloadRoutine);
            reloadRoutine = null;
            IsReloading = false;
        }

        // ---------------------------------------------------------------- scope (sniper weapons, 2026-11-07)
        /// <summary>Owner: looking through the scope right now (right mouse / AIM held with a weapon that has scopeFov).</summary>
        public bool IsScoped { get; private set; }
        /// <summary>Owner: 0 = normal view, 1 = fully through the scope (eased; camera, overlay and model hiding follow it).</summary>
        public float ScopeBlend { get; private set; }
        /// <summary>The local player's scope (null when nobody is scoping): read by ThirdPersonCamera, ScopeOverlay, GameUI.</summary>
        public static PlayerShooter LocalScope { get; private set; }
        float scopeHeld;
        Renderer[] ownRenderers;
        UnityEngine.Rendering.ShadowCastingMode[] ownShadowModes;
        bool modelHidden;

        void UpdateScope(bool canFight)
        {
            var w = Weapon;
            bool want = canFight && w.scopeFov > 0f && GameInput.AimHeld && !IsReloading;
            scopeHeld = want ? scopeHeld + Time.deltaTime : 0f;
            bool was = IsScoped;
            IsScoped = want && scopeHeld > 0.1f;
            if (IsScoped && !was) GameAudio.Play2D("ui_click", 0.35f, 0.7f);
            ScopeBlend = Mathf.MoveTowards(ScopeBlend, IsScoped ? 1f : 0f, Time.deltaTime * (IsScoped ? 7f : 10f));
            if (ScopeBlend > 0f) LocalScope = this; else if (LocalScope == this) LocalScope = null;
            // the camera sits inside the head while scoped: draw the own cat as shadow only (it still casts its shadow)
            bool hide = ScopeBlend > 0.6f;
            if (hide == modelHidden) return;
            modelHidden = hide;
            if (hide)
            {
                ownRenderers = GetComponentsInChildren<Renderer>();
                ownShadowModes = new UnityEngine.Rendering.ShadowCastingMode[ownRenderers.Length];
                for (int i = 0; i < ownRenderers.Length; i++)
                {
                    if (ownRenderers[i] is ParticleSystemRenderer || ownRenderers[i] is LineRenderer || ownRenderers[i] is TrailRenderer) continue;
                    ownShadowModes[i] = ownRenderers[i].shadowCastingMode;
                    ownRenderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
            }
            else if (ownRenderers != null)
            {
                for (int i = 0; i < ownRenderers.Length; i++)
                {
                    var r = ownRenderers[i];
                    if (r == null || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                    r.shadowCastingMode = ownShadowModes[i];
                }
                ownRenderers = null;
            }
        }

        public override void OnDestroy()
        {
            if (LocalScope == this) LocalScope = null;
            base.OnDestroy();
        }

        void Update()
        {
            if (isBot || (Online && !IsOwner)) return;
            if (aimCamera == null) aimCamera = Camera.main;
            bool canFight = aimCamera != null && player.InputEnabled && GameInput.Locked
                && !(MatchManager.Instance != null && MatchManager.Instance.InputLocked)
                && !(ownHealth != null && ownHealth.IsDead)
                && !(anim != null && anim.IsKnocked)
                && !(drop != null && !drop.CanFight)        // on the ship or in the air
                && !(bombs != null && bombs.IsHolding);     // aiming a bomb (G held)
            UpdateScope(canFight);
            if (!canFight) return;

            if (GameInput.ReloadPressed && !IsReloading && Ammo < MagazineSize && Reserve > 0) reloadRoutine = StartCoroutine(Reload());

            if (GameInput.FireHeld && !IsReloading && Time.time >= nextShotTime)
            {
                if (Ammo <= 0)
                {
                    if (Reserve > 0) reloadRoutine = StartCoroutine(Reload());
                    return;
                }
                Fire();
            }
        }

        void Fire()
        {
            var weapon = Weapon;
            nextShotTime = Time.time + 1f / weapon.fireRate;
            Ammo--;
            if (anim != null) anim.TriggerFire();
            CombatFeedback.OnShot(weapon);   // recoil, shake, crosshair bloom (local only)

            Transform cam = aimCamera.transform;
            // scoped weapons are precise through the scope only; from the hip they scatter
            float spread = weapon.scopeFov > 0f && ScopeBlend < 0.95f ? Mathf.Lerp(weapon.hipSpreadDegrees, weapon.spreadDegrees, ScopeBlend) : weapon.spreadDegrees;
            Vector3 dir = Spread(cam.forward, cam.up, spread);

            Vector3 target = cam.position + dir * weapon.range;
            bool landed = RaycastIgnoringSelf(cam.position, dir, weapon.range, out var hit);
            if (landed)
            {
                target = hit.point;
                var hurt = hit.collider.GetComponentInParent<Health>();
                if (hurt != null && !hurt.IsDead) LastHitTime = Time.time;
                if (!Online)
                {
                    var health = hit.collider.GetComponentInParent<Health>();
                    if (health != null && !health.IsDead)
                    {
                        bool head = IsHeadshot(health, hit.point);
                        float before = health.Current + health.Armor, armorBefore = health.Armor;
                        health.TakeDamage(weapon.damage * weapon.pellets * (head ? weapon.headshotMultiplier : 1f), gameObject);
                        CombatFeedback.OnHitConfirmed(hit.point, before - health.Current - health.Armor, health.Armor < armorBefore,
                                                      health.IsDead, (ulong)(uint)health.GetHashCode(), head);
                    }
                }
            }

            ShotEffects(target, weapon, landed, landed ? hit.normal : Vector3.zero, landed ? MuzzleFlash.SurfaceOf(hit.collider) : 0);
            if (Online) FireServerRpc(cam.position, dir);
        }

        // ---------------------------------------------------------------- server
        [Rpc(SendTo.Server)]
        void FireServerRpc(Vector3 origin, Vector3 dir)
        {
            var weapon = Weapons.Get(weaponId.Value);
            // Basic sanity checks; real anti-cheat / lag compensation comes later.
            if (ownHealth != null && ownHealth.IsDead) return;
            if (MatchManager.Instance != null && MatchManager.Instance.InputLocked) return;
            if (drop != null && !drop.CanFight) return;
            if (Time.time < nextServerShotTime) return;
            if (dir.sqrMagnitude < 0.5f || dir.sqrMagnitude > 1.5f) return;
            if ((origin - (transform.position + Vector3.up * 1.5f)).sqrMagnitude > 36f) return;
            nextServerShotTime = Time.time + 0.8f / weapon.fireRate;
            ServerShoot(origin, dir.normalized, weapon);
        }

        void ServerShoot(Vector3 origin, Vector3 dir, WeaponStats weapon)
        {
            Vector3 target = origin + dir * weapon.range;
            bool landed = false;
            Vector3 normal = Vector3.zero; int surface = 0;
            // A shotgun fires several pellets spread around the aim ray; the tracer follows the first one.
            var side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            var up = Vector3.Cross(side, dir).normalized;
            hitsThisShot.Clear();
            for (int i = 0; i < weapon.pellets; i++)
            {
                Vector3 d = i == 0 ? dir : Spread(dir, up, weapon.spreadDegrees);
                if (!RaycastIgnoringSelf(origin, d, weapon.range, out var hit)) continue;
                if (i == 0) { target = hit.point; landed = true; normal = hit.normal; surface = MuzzleFlash.SurfaceOf(hit.collider); }
                var health = hit.collider.GetComponentInParent<Health>();
                if (health == null || health.IsDead || IsFriendly(hit.collider)) continue;
                bool head = IsHeadshot(health, hit.point);
                float before = health.Current, armorBefore = health.Armor;
                health.TakeDamage(weapon.damage * (head ? weapon.headshotMultiplier : 1f), gameObject);
                // add up the pellets per cat: damage dealt to health + armor, did armor take some, was it the killing blow
                int k = hitsThisShot.FindIndex(h => h.health == health);
                var rec = k >= 0 ? hitsThisShot[k] : new ShotHit { health = health, point = hit.point };
                rec.damage += (before - health.Current) + (armorBefore - health.Armor);
                rec.armor |= health.Armor < armorBefore;
                rec.kill |= health.IsDead;
                rec.head |= head;
                if (k >= 0) hitsThisShot[k] = rec; else hitsThisShot.Add(rec);
            }
            ShotFxRpc(target, weaponId.Value, landed, normal, (byte)surface);
            foreach (var h in hitsThisShot)
                if (h.head && h.damage > 0.01f) { var np = GetComponent<NetworkPlayer>(); if (np != null) np.ServerAddHeadshot(); break; }   // one per shot
            if (!isBot)
                foreach (var h in hitsThisShot)
                {
                    if (h.damage <= 0.01f) continue;
                    var no = h.health.GetComponent<NetworkObject>();
                    HitConfirmRpc(h.point, h.damage, h.armor, h.kill, no != null ? no.NetworkObjectId : (ulong)(uint)h.health.GetHashCode(), h.head);
                }
        }

        struct ShotHit { public Health health; public Vector3 point; public float damage; public bool armor, kill, head; }

        /// <summary>Damage multiplier for a hit on the head.</summary>
        public const float HeadshotMultiplier = 1.5f;
        /// <summary>The top 38% of the target counts as the head (the chibi cats are mostly head).</summary>
        const float HeadFrom = 0.62f;

        static bool IsHeadshot(Health target, Vector3 point)
        {
            float height = 1.75f;
            var cc = target.GetComponent<CharacterController>();
            if (cc != null) height = cc.height * target.transform.lossyScale.y;
            else
            {
                var cap = target.GetComponent<CapsuleCollider>();
                if (cap != null) height = cap.height * target.transform.lossyScale.y;
            }
            return point.y - target.transform.position.y > height * HeadFrom;
        }
        readonly System.Collections.Generic.List<ShotHit> hitsThisShot = new System.Collections.Generic.List<ShotHit>();

        // Server -> the shooter only: hit marker, damage number, elimination banner (CombatFeedback).
        [Rpc(SendTo.Owner)]
        void HitConfirmRpc(Vector3 point, float damage, bool armor, bool kill, ulong targetId, bool head)
        {
            if (isBot) return;
            CombatFeedback.OnHitConfirmed(point, damage, armor, kill, targetId, head);
        }

        /// <summary>Server only: AI shot. Applies the same weapon stats, ammo and fire rate a person would have.</summary>
        public bool ServerBotFire(Vector3 origin, Vector3 dir)
        {
            if (!IsServer || !isBot || (ownHealth != null && ownHealth.IsDead)) return false;
            var weapon = Weapons.Get(weaponId.Value);
            if (Time.time < nextServerShotTime || Time.time < botReloadUntil) return false;
            if (botAmmo <= 0)
            {
                botReloadUntil = Time.time + weapon.reloadTime;
                botAmmo = weapon.magazine;
                if (anim != null) anim.TriggerReload(weapon.reloadTime);
                return false;
            }
            botAmmo--;
            nextServerShotTime = Time.time + 1.15f / weapon.fireRate;   // bots are a little slower than a perfect trigger finger
            if (anim != null) anim.TriggerFire();
            ServerShoot(origin, dir.normalized, weapon);
            return true;
        }

        // No friendly fire once the match is running.
        bool IsFriendly(Collider target)
        {
            if (MatchManager.Instance == null || MatchManager.Instance.State != MatchState.Playing) return false;
            var other = target.GetComponentInParent<NetworkPlayer>();
            var self = GetComponent<NetworkPlayer>();
            return other != null && self != null && other != self && other.TeamId == self.TeamId;
        }

        // Everyone except a human shooter (who already drew their own tracer). Bots are owned by the server,
        // so for them every machine, including the host, draws the tracer.
        [Rpc(SendTo.Everyone)]
        void ShotFxRpc(Vector3 target, int weapon, bool landed, Vector3 normal, byte surface)
        {
            if (IsOwner && !isBot) return;
            ShotEffects(target, Weapons.Get(weapon), landed, normal, surface);
        }

        public void ServerAddAmmo(int amount)
        {
            if (!IsServer) return;
            AddAmmoRpc(amount);
        }

        /// <summary>Gives the character its own weapon with a full magazine and fresh reserve (spawn, match start, reset).</summary>
        public void ServerResetLoadout()
        {
            if (!IsServer) return;
            var netPlayer = GetComponent<NetworkPlayer>();
            int id = Weapons.ForCharacter(netPlayer != null ? netPlayer.CharacterId : null);
            weaponId.Value = id;
            botAmmo = Weapons.Get(id).magazine;
            ResetAmmoRpc(id);
        }

        [Rpc(SendTo.Owner)]
        void AddAmmoRpc(int amount) => Reserve += amount;

        [Rpc(SendTo.Owner)]
        void ResetAmmoRpc(int id)
        {
            CancelReload();
            Ammo = Weapons.Get(id).magazine;
            Reserve = Weapons.Get(id).reserve;
        }

        static Vector3 Spread(Vector3 forward, Vector3 up, float degrees)
        {
            return Quaternion.AngleAxis(Random.Range(0f, 360f), forward) * Quaternion.AngleAxis(Random.Range(0f, degrees), up) * forward;
        }

        // Camera sits behind the player, so skip anything belonging to the shooter.
        // Triggers are included because remote players are represented by a trigger capsule; loot triggers are skipped.
        bool RaycastIgnoringSelf(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit best)
        {
            best = default;
            bool found = false;
            float bestDist = float.MaxValue;
            foreach (var h in Physics.RaycastAll(origin, dir, maxDist, hitMask, QueryTriggerInteraction.Collide))
            {
                if (h.transform.IsChildOf(transform)) continue;
                if (h.collider.GetComponentInParent<LootItem>() != null) continue;
                if (h.distance < bestDist) { best = h; bestDist = h.distance; found = true; }
            }
            return found;
        }

        // The rifle is a bone-parented mesh: its muzzle is the end of its bounds in the direction the cat faces.
        Vector3 MuzzlePosition()
        {
            if (muzzle != null) return muzzle.position;
            if (rifle != null && rifle.enabled)
            {
                var b = rifle.bounds; var d = transform.forward; var e = b.extents;
                return b.center + d * (Mathf.Abs(e.x * d.x) + Mathf.Abs(e.y * d.y) + Mathf.Abs(e.z * d.z));
            }
            return transform.position + Vector3.up * 1.3f + transform.forward * 0.6f;
        }

        // Flash, smoke, shell and a travelling tracer (MuzzleFlash); the old instant line only when the FX assets are missing.
        // Size by weapon: shotgun and sniper bark louder, SMG / pistol / carbine are smaller.
        void ShotEffects(Vector3 target, WeaponStats w, bool landed, Vector3 normal, int surface)
        {
            var from = MuzzlePosition();
            if (fx == null) fx = MuzzleFlash.For(gameObject);
            if (fx == null) { StartCoroutine(ShowTracer(from, target, w.color)); return; }
            float size = w.pellets > 1 ? 1.45f : w.fireRate < 2f ? 1.35f : w.damage < 16f ? 0.8f : 1f;
            fx.Play(from, target - from, size, transform.right);
            fx.Tracer(from, target, w.color, landed, normal, surface);
            // gunshot: own shots half "in the head", everybody else's fully 3D and audible far across the island (muffled with distance)
            bool own = IsOwner && !isBot || !Online;
            GameAudio.Play(GameAudio.GunSound(w), from, own ? 0.85f : 1f, own ? 4f : 7f, 260f, 1f, 0.05f, own ? 0.35f : 1f);
            if (Random.value < 0.55f)
                GameAudio.Play("shell", transform.position + transform.right * 0.3f, 0.3f, 1f, 14f, 1f, 0.12f, 1f, Random.Range(0.32f, 0.55f));
        }

        IEnumerator Reload()
        {
            IsReloading = true;
            if (anim != null) anim.TriggerReload(Weapon.reloadTime);
            GameAudio.Reload(transform.position + Vector3.up, Weapon.reloadTime, true);
            yield return new WaitForSeconds(Weapon.reloadTime);
            int take = Mathf.Min(MagazineSize - Ammo, Reserve);
            Ammo += take;
            Reserve -= take;
            IsReloading = false;
            reloadRoutine = null;
        }

        void CreateTracer()
        {
            var go = new GameObject("Tracer");
            go.transform.SetParent(transform, false);
            tracer = go.AddComponent<LineRenderer>();
            tracer.positionCount = 2;
            tracer.startWidth = tracer.endWidth = 0.03f;
            tracerMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            tracer.material = tracerMaterial;
            tracer.enabled = false;
        }

        IEnumerator ShowTracer(Vector3 from, Vector3 to, Color color)
        {
            tracerMaterial.color = Color.Lerp(color, new Color(1f, 0.9f, 0.4f), 0.4f);
            tracer.SetPosition(0, from);
            tracer.SetPosition(1, to);
            tracer.enabled = true;
            yield return new WaitForSeconds(0.04f);
            tracer.enabled = false;
        }
    }
}
