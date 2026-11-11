using System.Collections.Generic;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Sound playback for the whole game. Clips live in Resources/Audio (placeholders from Tools/Audio/make_sfx.py; drop in a
    /// real recording with the same file name to replace one). A name with numbered variants (gun_rifle_1, _2, _3) plays a
    /// random one. One-shots use a pool of 3D AudioSources with distance muffling (a low-pass that closes with distance, so far
    /// gunfire sounds like it is across the island). Loops get their own sources. Master volume = AudioListener.volume.
    /// Nothing plays on a batch-mode server.
    /// </summary>
    public static class GameAudio
    {
        const int PoolSize = 48;

        static readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
        static AudioSource[] pool;
        static AudioLowPassFilter[] filters;
        static int next;
        static Transform host;

        // ------------------------------------------------------------------ clips
        public static AudioClip Clip(string name)
        {
            if (!clips.TryGetValue(name, out var set))
            {
                var list = new List<AudioClip>();
                var single = Resources.Load<AudioClip>("Audio/" + name);
                if (single != null) list.Add(single);
                for (int i = 1; i < 16; i++)
                {
                    var c = Resources.Load<AudioClip>("Audio/" + name + "_" + i);
                    if (c == null) break;
                    list.Add(c);
                }
                set = list.ToArray();
                clips[name] = set;
                if (set.Length == 0) Debug.LogWarning("[GameAudio] no clip Resources/Audio/" + name);
            }
            return set.Length == 0 ? null : set[Random.Range(0, set.Length)];
        }

        static bool Ready()
        {
            if (Application.isBatchMode) return false;
            if (pool != null && host != null) return true;
            var go = new GameObject("GameAudio");
            Object.DontDestroyOnLoad(go);
            host = go.transform;
            pool = new AudioSource[PoolSize];
            filters = new AudioLowPassFilter[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var s = new GameObject("Voice" + i);
                s.transform.SetParent(host, false);
                pool[i] = s.AddComponent<AudioSource>();
                pool[i].playOnAwake = false;
                pool[i].dopplerLevel = 0f;
                pool[i].rolloffMode = AudioRolloffMode.Logarithmic;
                filters[i] = s.AddComponent<AudioLowPassFilter>();
            }
            EnsureListener();
            return true;
        }

        /// <summary>Makes sure the main camera can hear (a scene built from code may have no AudioListener).</summary>
        public static void EnsureListener()
        {
            if (Object.FindAnyObjectByType<AudioListener>() != null) return;
            var cam = Camera.main;
            if (cam != null) cam.gameObject.AddComponent<AudioListener>();
        }

        static Vector3 ListenerPos()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        // ------------------------------------------------------------------ one-shots
        /// <summary>3D one-shot at a point. <paramref name="spatial"/> 1 = fully 3D, 0 = 2D (in the player's head).</summary>
        public static AudioSource Play(string name, Vector3 position, float volume = 1f, float minDistance = 3f, float maxDistance = 80f,
                                       float pitch = 1f, float pitchVariation = 0.06f, float spatial = 1f, float delay = 0f, bool muffle = true)
        {
            if (!Ready()) return null;
            var clip = Clip(name);
            if (clip == null) return null;
            float dist = Vector3.Distance(position, ListenerPos());
            if (spatial > 0.5f && dist > maxDistance * 1.05f) return null;   // inaudible anyway

            int i = next; next = (next + 1) % PoolSize;
            // prefer a free voice; otherwise steal the round-robin one
            for (int k = 0; k < PoolSize; k++)
            {
                int j = (next + k) % PoolSize;
                if (!pool[j].isPlaying) { i = j; next = (j + 1) % PoolSize; break; }
            }
            var s = pool[i];
            s.Stop();
            s.transform.position = position;
            s.clip = clip;
            s.volume = volume;
            s.pitch = pitch * (1f + Random.Range(-pitchVariation, pitchVariation));
            s.spatialBlend = spatial;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            filters[i].enabled = muffle && spatial > 0.3f;
            filters[i].cutoffFrequency = Mathf.Lerp(22000f, 1600f, Mathf.Clamp01((dist - minDistance * 2f) / (maxDistance * 0.7f)));
            if (delay > 0f) s.PlayDelayed(delay); else s.Play();
            return s;
        }

        /// <summary>2D one-shot (UI, hit marker, own damage).</summary>
        public static AudioSource Play2D(string name, float volume = 1f, float pitch = 1f, float pitchVariation = 0f, float delay = 0f)
            => Play(name, ListenerPos(), volume, 1f, 10000f, pitch, pitchVariation, 0f, delay, false);

        // ------------------------------------------------------------------ loops
        /// <summary>A looping source of its own (plane, wind, ambience). Parent null = 2D at the listener.</summary>
        public static AudioSource Loop(string name, Transform parent, float volume, float minDistance = 5f, float maxDistance = 200f)
        {
            if (Application.isBatchMode) return null;
            var clip = Clip(name);
            if (clip == null) return null;
            var go = new GameObject("Loop_" + name);
            if (parent != null) go.transform.SetParent(parent, false);
            else Object.DontDestroyOnLoad(go);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.playOnAwake = false; s.dopplerLevel = 0f;
            s.volume = volume;
            s.spatialBlend = parent != null ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = minDistance; s.maxDistance = maxDistance;
            s.time = Random.Range(0f, clip.length * 0.9f);
            s.Play();
            EnsureListener();
            return s;
        }

        // ------------------------------------------------------------------ game helpers
        /// <summary>Weapon sound by the weapon table index (see Weapons): rifle, smg, heavy, sniper, marksman, shotgun, carbine, pistol, lmg.</summary>
        public static string GunSound(WeaponStats w) => string.IsNullOrEmpty(w.sound) ? "gun_rifle" : w.sound;

        /// <summary>Reload sounds timed to the reload clip (magazine out / in / charging handle at 11% / 63% / 80% of the duration).</summary>
        public static void Reload(Vector3 position, float duration, bool local)
        {
            float spatial = local ? 0.4f : 1f, vol = local ? 0.75f : 0.9f;
            Play("reload_out", position, vol, 2f, 30f, 1f, 0.04f, spatial, duration * 0.11f);
            Play("reload_in", position, vol, 2f, 30f, 1f, 0.04f, spatial, duration * 0.63f);
            Play("reload_charge", position, vol, 2f, 30f, 1f, 0.04f, spatial, duration * 0.8f);
        }

        public static string ImpactSound(int surface) =>
            surface == MuzzleFlash.Dirt ? "impact_dirt" : surface == MuzzleFlash.Wood ? "impact_wood" : surface == MuzzleFlash.Flesh ? "impact_flesh" : "impact_stone";
    }
}
