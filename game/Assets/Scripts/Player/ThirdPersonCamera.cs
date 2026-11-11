using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Over-the-shoulder orbit camera with wall collision. Normally driven by the local PlayerController's yaw/pitch;
    /// while spectating it orbits another cat with the mouse instead.
    /// </summary>
    public class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] PlayerController target;
        [SerializeField] Vector3 pivotOffset = new Vector3(0.45f, 1.55f, 0f);
        [SerializeField] float distance = 3.2f;
        [SerializeField] float collisionRadius = 0.25f;
        [SerializeField] LayerMask collisionMask = ~0;
        [SerializeField] float spectateSensitivity = 0.12f;
        [Header("Aim (right mouse): closer over the shoulder")]
        [SerializeField] Vector3 aimPivotOffset = new Vector3(0.55f, 1.5f, 0f);
        [SerializeField] float aimDistance = 2.0f;
        [SerializeField] float aimFov = 50f;
        [Header("Victory shot (VictorySequence): 0 = still camera, > 0 = degrees per second around the winner")]
        [SerializeField] float victoryOrbitSpeed = 0f;
        [Header("Sniper scope: eye point (the model is hidden while scoped)")]
        [SerializeField] float scopeEyeHeight = 1.3f;
        [SerializeField] float scopeForward = 0.55f;   // in front of the big chibi face, so the near plane never cuts into it
        float aimBlend;
        float planeBlend;      // 0 = following the cat, 1 = orbiting the drop plane
        PlayerDrop drop;
        float baseFov = -1f;
        Camera cam;

        Transform spectateTarget;
        float spectateYaw;
        float spectatePitch = 15f;

        public Transform SpectateTarget => spectateTarget;
        public PlayerController Target => target;
        /// <summary>The main gameplay camera (CameraFx shakes only this one, for the player it follows).</summary>
        public static ThirdPersonCamera Current { get; private set; }

        void OnEnable() => Current = this;
        void OnDisable() { if (Current == this) Current = null; }

        public void SetTarget(PlayerController player) => target = player;

        /// <summary>Follow another cat (pass null to go back to the local player).</summary>
        public void SetSpectateTarget(Transform other)
        {
            if (other != null && other != spectateTarget) spectateYaw = other.eulerAngles.y;
            spectateTarget = other;
        }

        void LateUpdate()
        {
            Transform follow;
            float yaw, pitch;
            if (spectateTarget != null)
            {
                if (GameInput.Locked)
                {
                    var delta = GameInput.Look * spectateSensitivity;
                    spectateYaw += delta.x;
                    spectatePitch = Mathf.Clamp(spectatePitch - delta.y, -20f, 70f);
                }
                follow = spectateTarget;
                yaw = spectateYaw;
                pitch = spectatePitch;
            }
            else if (target != null)
            {
                follow = target.transform;
                yaw = target.Yaw;
                pitch = target.Pitch;
            }
            else return;

            // winner's moment (VictorySequence): one still shot of the cat from the front, low and heroic, eased in from the current
            // view (2026-11-07, user: no circling camera for now; victoryOrbitSpeed > 0 brings the slow circle back)
            var orbit = VictorySequence.OrbitTarget;
            if (orbit != null && spectateTarget == null)
            {
                float t = Time.unscaledTime - VictorySequence.OrbitStart;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.9f));
                Vector3 centre = orbit.position + Vector3.up * 1.05f;
                float ang = orbit.eulerAngles.y + 180f - 25f + t * victoryOrbitSpeed;   // in front, a little to the cat's left
                Vector3 want = orbit.position + Vector3.up * 0.75f + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * 3.3f;
                Vector3 from = centre, to = want - centre;
                if (Physics.SphereCast(from, collisionRadius, to.normalized, out var oh, to.magnitude, collisionMask, QueryTriggerInteraction.Ignore)
                    && !oh.transform.IsChildOf(orbit))
                    want = from + to.normalized * Mathf.Max(0.8f, oh.distance);
                var look = Quaternion.LookRotation(centre + Vector3.up * 0.15f - want, Vector3.up);
                if (cam == null) cam = GetComponent<Camera>();
                if (cam != null) { if (baseFov < 0f) baseFov = cam.fieldOfView; cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, 48f, e); }
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, want, e), Quaternion.Slerp(transform.rotation, look, e));
                return;
            }

            // inside the drop plane: orbit the plane from outside (the cat is hidden in it), ease back to the cat after the jump
            if (target != null && spectateTarget == null)
            {
                if (drop == null || drop.gameObject != target.gameObject) drop = target.GetComponent<PlayerDrop>();
                var match = MatchManager.Instance;
                bool onPlane = drop != null && match != null && drop.State == DropState.OnShip;
                planeBlend = Mathf.MoveTowards(planeBlend, onPlane ? 1f : 0f, Time.deltaTime * (onPlane ? 4f : 1.5f));
                if (planeBlend > 0.001f && match != null)
                {
                    float e = planeBlend * planeBlend * (3f - 2f * planeBlend);
                    var prot = Quaternion.Euler(Mathf.Max(pitch, 8f), yaw, 0f);
                    Vector3 planePivot = match.ShipPosition + Vector3.up * 3f;
                    Vector3 planeCam = planePivot + prot * Vector3.back * 34f;
                    Vector3 catPivot = follow.position + Quaternion.Euler(0f, yaw, 0f) * pivotOffset;
                    Vector3 catCam = catPivot + Quaternion.Euler(pitch, yaw, 0f) * Vector3.back * distance;
                    var look = Quaternion.LookRotation(Vector3.Lerp(catPivot, planePivot, e) - Vector3.Lerp(catCam, planeCam, e), Vector3.up);
                    transform.SetPositionAndRotation(Vector3.Lerp(catCam, planeCam, e), look);
                    CameraFx.Apply(transform, null, Time.deltaTime);   // no FOV changes here: this branch does not reset the FOV
                    return;
                }
            }

            // smooth zoom to the shoulder while aiming (never while spectating)
            bool aiming = spectateTarget == null && target != null && target.IsAiming;
            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, Time.deltaTime * 6f);
            float k = aimBlend * aimBlend * (3f - 2f * aimBlend);
            if (cam == null) cam = GetComponent<Camera>();
            if (cam != null)
            {
                if (baseFov < 0f) baseFov = cam.fieldOfView;
                cam.fieldOfView = Mathf.Lerp(baseFov, aimFov, k);
            }

            var rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = follow.position + Quaternion.Euler(0f, yaw, 0f) * Vector3.Lerp(pivotOffset, aimPivotOffset, k);
            Vector3 dir = rot * Vector3.back;

            float dist = Mathf.Lerp(distance, aimDistance, k);
            if (Physics.SphereCast(pivot, collisionRadius, dir, out var hit, dist, collisionMask, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(follow))
                dist = Mathf.Max(0.4f, hit.distance);

            Vector3 camPos = pivot + dir * dist;

            // sniper scope: the camera moves into the cat's head (the model is drawn as shadow only, PlayerShooter) and zooms in
            var scope = PlayerShooter.LocalScope;
            if (scope != null && spectateTarget == null && target != null && scope.gameObject == target.gameObject && scope.ScopeBlend > 0f)
            {
                float s = scope.ScopeBlend * scope.ScopeBlend * (3f - 2f * scope.ScopeBlend);
                Vector3 eye = follow.position + Vector3.up * scopeEyeHeight + rot * Vector3.forward * scopeForward;
                camPos = Vector3.Lerp(camPos, eye, s);
                if (cam != null) cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, scope.Weapon.scopeFov, s);
            }

            transform.SetPositionAndRotation(camPos, rot);
            CameraFx.Apply(transform, cam, Time.deltaTime);   // shake, recoil, FOV punch (visual only)
        }
    }
}
