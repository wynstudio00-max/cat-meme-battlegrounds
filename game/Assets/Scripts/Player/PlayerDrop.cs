using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    public enum DropState { Ground = 0, OnShip = 1, Freefall = 2, Parachute = 3 }

    /// <summary>
    /// The way into a match: board the drop ship, jump (Space) while it crosses the island, free-fall, open the parachute
    /// (Space again, or automatically near the ground) and land with your own weapon.
    /// Movement stays owner-authoritative like the rest of the game: the owning client flies its own cat while airborne
    /// (the CharacterController is switched off), the server owns the <see cref="State"/> and validates the transitions.
    /// Bots have no client, so the server flies them with the same numbers.
    /// </summary>
    public class PlayerDrop : NetworkBehaviour
    {
        const float FreefallSpeed = 22f, FreefallForward = 14f;
        const float ChuteSpeed = 5.5f, ChuteForward = 7f;
        const float DeployHeight = 35f;       // metres above the ground where the parachute opens by itself
        const float MinFreefallTime = 0.6f;
        // The Parachute_Land clip starts with the cat floating this high (Unity metres, average over the cats): the cat is put on the
        // ground when it is this far up and the clip animates the last bit of the descent.
        const float ChuteLandHeight = 0.75f;

        readonly NetworkVariable<int> state = new NetworkVariable<int>((int)DropState.Ground);

        PlayerController controller;
        CharacterController cc;
        PlayerAnimator anim;
        BotBrain bot;
        bool isBot;

        DropState local;              // owner / bot: what we are actually simulating right now
        bool wasAirborne;
        float verticalSpeed;
        float stateTime;
        Vector3 botTarget;
        float botJumpAt;
        GameObject chute;
        Transform canopy;
        float chuteShownAt = -10f;

        public DropState State => IsSpawned ? (DropState)state.Value : DropState.Ground;
        /// <summary>Owner: metres above the ground while airborne (HUD altimeter).</summary>
        public float Altitude { get; private set; }
        /// <summary>Shooting and normal movement only work on the ground.</summary>
        public bool CanFight => State == DropState.Ground;
        /// <summary>True from boarding until touchdown. PlayerController leaves movement to this component meanwhile.</summary>
        public bool Airborne => IsSpawned && local != DropState.Ground;

        void Awake()
        {
            controller = GetComponent<PlayerController>();
            cc = GetComponent<CharacterController>();
            anim = GetComponent<PlayerAnimator>();
        }

        public override void OnNetworkSpawn()
        {
            isBot = IsServer && GetComponent<BotBrain>() != null;
            bot = GetComponent<BotBrain>();
            state.OnValueChanged += OnStateChanged;
        }

        public override void OnNetworkDespawn()
        {
            state.OnValueChanged -= OnStateChanged;
            if (chute != null) Destroy(chute);
        }

        void OnStateChanged(int previous, int current)
        {
            var v = (DropState)current;
            // Adopt server decisions (forced jump, reset) but never go back in time because of a late update.
            if (v > local || v == DropState.Ground || v == DropState.OnShip)
            {
                if (v != local) stateTime = 0f;
                if (IsOwner && !isBot && local == DropState.OnShip && v == DropState.Freefall) ExitPlane();
                local = v;
            }
            if (v == DropState.OnShip && isBot) botJumpAt = Random.Range(0f, 0.9f);
        }

        // ---------------------------------------------------------------- server API
        public void ServerBoard()
        {
            if (!IsServer) return;
            if (isBot && bot != null) bot.Unplace();
            botJumpAt = Random.Range(0f, 0.9f);
            local = DropState.OnShip;
            state.Value = (int)DropState.OnShip;
        }

        public void ServerReset()
        {
            if (!IsServer) return;
            local = DropState.Ground;
            state.Value = (int)DropState.Ground;
        }

        /// <summary>The ship has left the island: everyone still aboard is dropped.</summary>
        public void ServerForceJump()
        {
            if (!IsServer || state.Value != (int)DropState.OnShip) return;
            StartFreefall();
        }

        void StartFreefall()
        {
            ExitPlane();
            local = DropState.Freefall;
            stateTime = 0f;
            verticalSpeed = 0f;
            state.Value = (int)DropState.Freefall;
            if (isBot) botTarget = RandomLanding();
        }

        [Rpc(SendTo.Server)]
        void JumpRpc()
        {
            var match = MatchManager.Instance;
            if (state.Value != (int)DropState.OnShip || match == null || !match.CanJump) return;
            state.Value = (int)DropState.Freefall;
        }

        [Rpc(SendTo.Server)]
        void DeployRpc()
        {
            if (state.Value == (int)DropState.Freefall) state.Value = (int)DropState.Parachute;
        }

        [Rpc(SendTo.Server)]
        void LandedRpc()
        {
            if (state.Value == (int)DropState.Freefall || state.Value == (int)DropState.Parachute) state.Value = (int)DropState.Ground;
        }

        // ---------------------------------------------------------------- per frame
        void Update()
        {
            if (!IsSpawned) return;
            UpdateChute();
            var match = MatchManager.Instance;
            if (match == null) return;

            if (isBot) { BotUpdate(match); return; }
            if (!IsOwner) return;

            bool airborne = local != DropState.Ground;
            if (airborne != wasAirborne)
            {
                wasAirborne = airborne;
                if (airborne) cc.enabled = false;
                else
                {
                    controller.Teleport(transform.position, controller.Yaw);   // switches the CharacterController back on
                    if (anim != null) { anim.SetParachute(false); anim.SetSkydive(false); }
                }
            }
            if (CameraFx.IsLocalView(this)) DropFx.Tick(local, -verticalSpeed);   // wind streaks, FOV, buffeting
            if (!airborne) return;
            stateTime += Time.deltaTime;
            OwnerFly(match);
        }

        void OwnerFly(MatchManager match)
        {
            bool space = GameInput.JumpPressed && GameInput.Locked;
            float yaw = controller.Yaw;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);

            switch (local)
            {
                case DropState.OnShip:
                    SeatOnShip(match, yaw);
                    Altitude = transform.position.y - GroundHeight(transform.position);
                    if (space && match.CanJump) { ExitPlane(); local = DropState.Freefall; stateTime = 0f; verticalSpeed = 0f; JumpRpc(); }
                    break;

                case DropState.Freefall:
                case DropState.Parachute:
                {
                    bool chuteOpen = local == DropState.Parachute;
                    Vector2 input = GameInput.Move;
                    float forward = chuteOpen ? ChuteForward : FreefallForward;
                    float fall = chuteOpen ? ChuteSpeed : FreefallSpeed;
                    verticalSpeed = Mathf.MoveTowards(verticalSpeed, -fall, (chuteOpen ? 12f : 40f) * Time.deltaTime);

                    var horizontal = yawRot * new Vector3(input.x * forward * 0.6f, 0f, input.y * forward);
                    var pos = transform.position + (horizontal + Vector3.up * verticalSpeed) * Time.deltaTime;
                    pos = ClampToArena(match, pos);

                    float groundY = GroundHeight(pos);
                    float height = pos.y - groundY;
                    Altitude = height;
                    if (!chuteOpen && stateTime > MinFreefallTime && (space || height <= DeployHeight))
                    {
                        local = DropState.Parachute; stateTime = 0f;
                        DeployRpc();
                    }
                    if (height <= (chuteOpen ? ChuteLandHeight : 0.05f))
                    {
                        pos.y = groundY + 0.05f;
                        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                        local = DropState.Ground;
                        if (anim != null) { anim.SetParachute(false); anim.SetSkydive(false); }
                        // switch the CharacterController back on right now: PlayerController may run before us next frame
                        wasAirborne = false;
                        controller.Teleport(pos, yaw);
                        LandedRpc();
                        CameraFx.Shake(chuteOpen ? 0.32f : 0.6f);   // touch-down thump
                        return;
                    }
                    transform.SetPositionAndRotation(pos, Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, yaw, 0f), 10f * Time.deltaTime));
                    break;
                }
            }

            if (anim != null)
            {
                anim.SetLocomotion(Vector2.zero, 1f);
                anim.SetGrounded(local == DropState.OnShip);
                anim.SetCrouching(false);
                anim.SetParachute(local == DropState.Parachute);
                anim.SetSkydive(local == DropState.Freefall);
            }
        }

        // ---------------------------------------------------------------- bots (server simulates them)
        void BotUpdate(MatchManager match)
        {
            if (local == DropState.Ground) return;
            stateTime += Time.deltaTime;

            switch (local)
            {
                case DropState.OnShip:
                    SeatOnShip(match, Mathf.Atan2(match.ShipDirection.x, match.ShipDirection.z) * Mathf.Rad2Deg);
                    if (match.CanJump && match.JumpWindowFraction >= botJumpAt) StartFreefall();
                    break;

                case DropState.Freefall:
                case DropState.Parachute:
                {
                    bool chuteOpen = local == DropState.Parachute;
                    var toTarget = botTarget - transform.position; toTarget.y = 0f;
                    float forward = chuteOpen ? ChuteForward : FreefallForward;
                    var horizontal = toTarget.magnitude > 1.5f ? toTarget.normalized * forward : Vector3.zero;
                    verticalSpeed = Mathf.MoveTowards(verticalSpeed, chuteOpen ? -ChuteSpeed : -FreefallSpeed, (chuteOpen ? 12f : 40f) * Time.deltaTime);
                    var pos = ClampToArena(match, transform.position + (horizontal + Vector3.up * verticalSpeed) * Time.deltaTime);

                    float groundY = GroundHeight(pos);
                    float height = pos.y - groundY;
                    if (!chuteOpen && stateTime > MinFreefallTime && height <= DeployHeight) { local = DropState.Parachute; stateTime = 0f; state.Value = (int)DropState.Parachute; }
                    if (height <= (chuteOpen ? ChuteLandHeight : 0.05f))
                    {
                        pos.y = groundY;
                        local = DropState.Ground;
                        state.Value = (int)DropState.Ground;
                        if (anim != null) { anim.SetParachute(false); anim.SetSkydive(false); }
                        if (bot != null) bot.Place(pos, transform.eulerAngles.y);
                        return;
                    }
                    var face = horizontal.sqrMagnitude > 0.01f ? Quaternion.LookRotation(horizontal) : transform.rotation;
                    transform.SetPositionAndRotation(pos, Quaternion.Slerp(transform.rotation, face, 6f * Time.deltaTime));
                    break;
                }
            }

            if (anim != null)
            {
                anim.SetLocomotion(Vector2.zero, 1f);
                anim.SetGrounded(local == DropState.OnShip);
                anim.SetCrouching(false);
                anim.SetParachute(local == DropState.Parachute);
                anim.SetSkydive(local == DropState.Freefall);
            }
        }

        Vector3 RandomLanding()
        {
            var match = MatchManager.Instance;
            float radius = (match != null ? match.ArenaRadius : 120f) * 0.9f;
            var p = Random.insideUnitCircle * radius;
            return new Vector3(p.x, 0f, p.y);
        }

        // ---------------------------------------------------------------- helpers
        // Everybody stands in a row across the ship deck, ordered by network id so seats differ and do not overlap.
        // Everybody rides inside the plane (hidden, see UpdateVisibility), at its centre.
        void SeatOnShip(MatchManager match, float yaw)
        {
            transform.SetPositionAndRotation(match.ShipPosition, Quaternion.Euler(0f, yaw, 0f));
            verticalSpeed = 0f;
        }

        // Jumping out: appear at the rear ramp of the plane, facing the way it flies.
        void ExitPlane()
        {
            var match = MatchManager.Instance;
            if (match == null) return;
            var rot = Quaternion.LookRotation(match.ShipDirection, Vector3.up);
            transform.SetPositionAndRotation(match.ShipPosition + rot * DropShipVisual.ExitOffset, rot);
            verticalSpeed = 0f;
        }

        static Vector3 ClampToArena(MatchManager match, Vector3 pos)
        {
            var flat = new Vector2(pos.x, pos.z);
            float max = match.ArenaRadius * 0.98f;
            if (flat.magnitude > max)
            {
                flat = flat.normalized * max;
                pos.x = flat.x; pos.z = flat.y;
            }
            return pos;
        }

        float GroundHeight(Vector3 pos)
        {
            // Triggers (loot, other players' hitboxes) are ignored; trees, roofs and terrain count as ground.
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;
            return 0f;
        }

        // ---------------------------------------------------------------- parachute visual (all clients)
        Renderer[] bodyRenderers;
        bool hidden;

        void UpdateVisibility()
        {
            if (Application.isBatchMode) return;
            bool hide = State == DropState.OnShip || (IsOwner && !isBot && local == DropState.OnShip);
            if (hide == hidden) return;
            hidden = hide;
            if (bodyRenderers == null)
            {
                var list = new System.Collections.Generic.List<Renderer>();
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                    if (!(r is ParticleSystemRenderer) && !(r is LineRenderer)) list.Add(r);
                bodyRenderers = list.ToArray();
            }
            foreach (var r in bodyRenderers) if (r != null) r.enabled = !hide;
        }

        void UpdateChute()
        {
            if (Application.isBatchMode) return;
            UpdateVisibility();
            bool show = State == DropState.Parachute || (IsOwner && !isBot && local == DropState.Parachute);
            if (show && chute == null) chute = BuildChute();
            if (chute != null && chute.activeSelf != show)
            {
                chute.SetActive(show);
                if (show)
                {
                    chuteShownAt = Time.time;
                    GameAudio.Play("parachute_open", transform.position + Vector3.up * 3f, 1f, 6f, 90f, 1f, 0.06f, CameraFx.IsLocalView(this) ? 0.4f : 1f);
                    if (CameraFx.IsLocalView(this)) CameraFx.Shake(0.4f);   // the canopy catches the air: jolt
                }
            }
            if (chute == null || !show) return;
            // the canopy bursts open (small -> overshoot -> settle) and then breathes in the wind
            float t = Time.time - chuteShownAt;
            float open = t < 0.14f ? Mathf.Lerp(0.15f, 1.18f, t / 0.14f) : 1f + 0.18f * Mathf.Exp(-(t - 0.14f) * 5.5f) * Mathf.Cos((t - 0.14f) * 17f);
            chute.transform.localScale = new Vector3(open, Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(t / 0.25f)) * open, open);
            if (canopy != null)
            {
                float b = Mathf.Sin(Time.time * 2.6f + NetworkObjectId) * 0.035f;
                canopy.localScale = Vector3.Scale(canopyScale, new Vector3(1f - b * 0.5f, 1f + b, 1f - b * 0.5f));
                canopy.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 1.3f) * 3f, 0f, Mathf.Sin(Time.time * 1.7f + 1f) * 4f);
            }
        }

        // The user's parachute models (CHARACTER/parachute_model.blend -> Tools/BlenderAnim/export_parachutes.py), origin at the
        // harness where the lines meet. Variants are handed out by object id; scaled to ~2.9 m across.
        static readonly string[] ChuteModels = { "Vehicles/Parachute_CMBG", "Vehicles/Parachute_Robinhood" };
        const float ChuteScale = 1.85f;  // ~2.7 m across for a 1.75 m cat
        const float HarnessHeight = 1.55f;  // the lines meet just above the cat (body top 1.28-1.53 m in the Parachute pose)
        Vector3 canopyScale = Vector3.one;

        GameObject BuildChute()
        {
            var root = new GameObject("Parachute");
            root.transform.SetParent(transform, false);

            var model = Resources.Load<GameObject>(ChuteModels[(int)(NetworkObjectId % (ulong)ChuteModels.Length)]);
            if (model != null)
            {
                var pivot = new GameObject("Canopy").transform;   // swings and breathes around the harness
                pivot.SetParent(root.transform, false);
                pivot.localPosition = new Vector3(0f, HarnessHeight, 0f);
                var m = Instantiate(model, pivot, false);
                m.name = "ParachuteModel";
                foreach (var c in m.GetComponentsInChildren<Collider>()) Destroy(c);
                foreach (var r in m.GetComponentsInChildren<Renderer>())
                {
                    foreach (var cm in r.materials) cm.SetFloat("_Cull", 0f);   // the canopy is seen from below too
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                canopyScale = Vector3.one * ChuteScale;
                pivot.localScale = canopyScale;
                canopy = pivot;
                return root;
            }

            // fallback: the old primitive canopy

            var colors = new[] { new Color(0.95f, 0.35f, 0.3f), new Color(0.3f, 0.65f, 0.95f), new Color(0.98f, 0.8f, 0.25f), new Color(0.45f, 0.8f, 0.4f), new Color(0.8f, 0.45f, 0.9f) };
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = colors[(int)(NetworkObjectId % (ulong)colors.Length)] };
            mat.SetFloat("_Smoothness", 0.15f);

            var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "Canopy";
            dome.transform.SetParent(root.transform, false);
            dome.transform.localPosition = new Vector3(0f, 3.3f, 0f);
            dome.transform.localScale = new Vector3(3.2f, 1.5f, 3.2f);
            dome.GetComponent<Renderer>().sharedMaterial = mat;
            canopy = dome.transform;
            canopyScale = new Vector3(3.2f, 1.5f, 3.2f);
            Destroy(dome.GetComponent<Collider>());

            var lineMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(0.95f, 0.95f, 0.9f) };
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                var line = new GameObject("Line" + i).AddComponent<LineRenderer>();
                line.transform.SetParent(root.transform, false);
                line.useWorldSpace = false;
                line.positionCount = 2;
                line.startWidth = line.endWidth = 0.025f;
                line.sharedMaterial = lineMat;
                line.SetPosition(0, new Vector3(Mathf.Cos(a) * 1.35f, 3.0f, Mathf.Sin(a) * 1.35f));
                line.SetPosition(1, new Vector3(0f, 1.45f, 0f));
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return root;
        }
    }
}
