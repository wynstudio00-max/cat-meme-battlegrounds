using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Camera juice for the local player: trauma-based shake (Perlin noise, amplitude = trauma^2 so small hits stay subtle and
    /// big ones really kick), gun recoil (the view snaps up and eases back down, so sprays climb a little) and a short FOV punch.
    /// <see cref="ThirdPersonCamera"/> calls <see cref="Apply"/> at the end of its LateUpdate. Everything is visual only:
    /// nothing here is networked, every machine only shakes its own camera.
    /// </summary>
    public static class CameraFx
    {
        // shake
        const float TraumaDecay = 1.5f;        // per second
        const float MaxAngle = 3.2f;           // degrees pitch / yaw at trauma 1
        const float MaxRoll = 4.5f;
        const float MaxOffset = 0.12f;         // metres
        const float Frequency = 21f;
        // recoil
        const float RecoilReturn = 10f;        // how fast the kick eases back (1/s)
        const float RecoilSnap = 38f;          // how fast the view reaches the kick

        static float trauma;
        static Vector2 recoilTarget, recoil;   // x = pitch up, y = yaw (degrees)
        static float fovPunch;
        static float seed = Random.value * 100f;

        /// <summary>Add trauma 0..1 (0.1 = gunshot, 0.35 = hit / landing, 0.8 = death / explosion).</summary>
        public static void Shake(float amount) => trauma = Mathf.Clamp01(trauma + amount);

        /// <summary>Recoil: view kicks up by <paramref name="pitch"/> degrees with a random sideways part.</summary>
        public static void Kick(float pitch, float yaw)
        {
            recoilTarget.x = Mathf.Min(recoilTarget.x + pitch, 9f);
            recoilTarget.y = Mathf.Clamp(recoilTarget.y + yaw, -4f, 4f);
        }

        /// <summary>Widen the field of view for a moment (degrees).</summary>
        public static void Punch(float fov) => fovPunch = Mathf.Max(fovPunch, fov);

        /// <summary>Keep the shake at least at this level while it is called every frame (free-fall wind buffeting).</summary>
        public static void Sustain(float level) => trauma = Mathf.Max(trauma, level);

        /// <summary>Extra field of view held while the target is set (e.g. +10 in free fall), eased in and out.</summary>
        public static float FovOffsetTarget { get; set; }
        static float fovOffset;

        /// <summary>True when <paramref name="who"/> is the player the main camera follows (not spectating someone else).</summary>
        public static bool IsLocalView(Component who)
        {
            var cam = ThirdPersonCamera.Current;
            return cam != null && who != null && cam.Target != null && cam.Target.gameObject == who.gameObject && cam.SpectateTarget == null;
        }

        public static void Apply(Transform cam, Camera camera, float dt)
        {
            // recoil: snap towards the target, the target eases back to zero
            recoilTarget = Vector2.Lerp(recoilTarget, Vector2.zero, 1f - Mathf.Exp(-RecoilReturn * dt));
            recoil = Vector2.Lerp(recoil, recoilTarget, 1f - Mathf.Exp(-RecoilSnap * dt));

            trauma = Mathf.MoveTowards(trauma, 0f, TraumaDecay * dt);
            float s = trauma * trauma;
            float t = Time.time * Frequency;
            float n(float o) => Mathf.PerlinNoise(seed + o, t) * 2f - 1f;
            var shakeRot = new Vector3(n(0f) * MaxAngle, n(10f) * MaxAngle, n(20f) * MaxRoll) * s;
            var shakePos = new Vector3(n(30f), n(40f), n(50f)) * (MaxOffset * s);

            cam.rotation = cam.rotation * Quaternion.Euler(-recoil.x + shakeRot.x, recoil.y + shakeRot.y, shakeRot.z);
            cam.position += cam.rotation * shakePos;

            fovPunch = Mathf.MoveTowards(fovPunch, 0f, dt * 18f);
            fovOffset = Mathf.Lerp(fovOffset, FovOffsetTarget, 1f - Mathf.Exp(-3f * dt));
            if (camera != null) camera.fieldOfView += fovPunch + fovOffset;
        }
    }
}
