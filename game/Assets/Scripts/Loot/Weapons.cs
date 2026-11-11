using UnityEngine;

namespace CatBattleground
{
    /// <summary>Stats of one weapon. All ranged weapons are hitscan and share the rifle model in the cat's hands for now.</summary>
    public class WeaponStats
    {
        public string name;
        public float damage;
        public float fireRate;       // shots per second
        public float range;
        public float spreadDegrees;
        public int magazine;
        public int reserve;          // spare rounds at spawn
        public int pellets = 1;      // rays per shot (shotgun)
        public float reloadTime;
        public Color color;          // tracer / HUD colour
        public string sound;         // Resources/Audio clip (variants _1.._n), see GameAudio
        public float headshotMultiplier = 1.5f;   // damage x on hits above 62% of the target's height (PlayerShooter.IsHeadshot)
        // Scope (2026-11-07): > 0 = hold the right mouse / AIM to look through the scope with this field of view (PlayerShooter.IsScoped,
        // ThirdPersonCamera, ScopeOverlay). Unscoped shots of a scoped weapon use hipSpreadDegrees instead of spreadDegrees.
        public float scopeFov;
        public float hipSpreadDegrees;
        public Color scopeTint = new Color(0.08f, 0.08f, 0.08f);   // reticle colour
    }

    /// <summary>
    /// Every cat carries its own signature weapon from the moment it leaves the ship; nothing is picked up from the ground.
    /// Weapon table indexed by weapon id, plus the character -> weapon mapping.
    /// </summary>
    public static class Weapons
    {
        public const int Starter = 0;   // fallback for an unknown character

        static readonly WeaponStats[] all =
        {
            /* 0 */ new WeaponStats { sound = "gun_rifle", name = "Assault Rifle",   damage = 18f, fireRate = 9f,   range = 120f, spreadDegrees = 0.6f,  magazine = 30, reserve = 120, reloadTime = 1.8f, color = new Color(0.85f, 0.85f, 0.85f) },
            /* 1 */ new WeaponStats { sound = "gun_smg", name = "Rapid SMG",       damage = 12f, fireRate = 14f,  range = 80f,  spreadDegrees = 1.2f,  magazine = 40, reserve = 160, reloadTime = 1.6f, color = new Color(0.3f, 0.85f, 0.3f) },
            /* 2 */ new WeaponStats { sound = "gun_heavy", name = "Heavy Rifle",     damage = 28f, fireRate = 6f,   range = 140f, spreadDegrees = 0.4f,  magazine = 20, reserve = 80,  reloadTime = 2.2f, color = new Color(0.3f, 0.5f, 1f) },
            /* 3 */ new WeaponStats { sound = "gun_sniper", name = "Sniper",          damage = 70f, fireRate = 1.2f, range = 220f, spreadDegrees = 0.05f, magazine = 5,  reserve = 25,  reloadTime = 2.8f, scopeFov = 16f, hipSpreadDegrees = 3f, color = new Color(1f, 0.6f, 0.1f) },
            /* 4 */ new WeaponStats { sound = "gun_marksman", name = "Marksman Rifle",  damage = 40f, fireRate = 2.8f, range = 180f, spreadDegrees = 0.15f, magazine = 10, reserve = 50,  reloadTime = 2.4f, color = new Color(0.95f, 0.85f, 0.3f) },
            /* 5 */ new WeaponStats { sound = "gun_shotgun", name = "Combat Shotgun",  pellets = 8, damage = 9f,  fireRate = 1.6f, range = 38f,  spreadDegrees = 4.5f,  magazine = 8,  reserve = 32,  reloadTime = 2.6f, color = new Color(1f, 0.4f, 0.25f) },
            /* 6 */ new WeaponStats { sound = "gun_carbine", name = "Silent Carbine",  damage = 15f, fireRate = 11f,  range = 95f,  spreadDegrees = 0.8f,  magazine = 30, reserve = 120, reloadTime = 1.7f, color = new Color(0.6f, 0.4f, 0.9f) },
            /* 7 */ new WeaponStats { sound = "gun_pistol", name = "Burst Pistol",    damage = 22f, fireRate = 5f,   range = 70f,  spreadDegrees = 0.9f,  magazine = 15, reserve = 75,  reloadTime = 1.3f, color = new Color(1f, 0.6f, 0.8f) },
            /* 8 */ new WeaponStats { sound = "gun_lmg", name = "Light Machine Gun", damage = 14f, fireRate = 12f, range = 100f, spreadDegrees = 1.6f, magazine = 60, reserve = 180, reloadTime = 3.4f, color = new Color(0.9f, 0.3f, 0.3f) },
            // 9: Cash Cat's legendary sniper (2026-11-07, user): Ninja's sniper model in black & gold. A sidegrade, not a stronger gun
            // (pass cards are NFTs, keep it fair): a little less body damage, quicker follow-up, one more round, faster reload,
            // and its signature - headshots x2 (62 -> 124) - plus a gold tracer, a stronger scope (13 vs 16 deg) and a gold reticle.
            /* 9 */ new WeaponStats { sound = "gun_sniper", name = "Golden Cash Sniper", damage = 62f, fireRate = 1.4f, range = 230f, spreadDegrees = 0.05f, magazine = 6, reserve = 30, reloadTime = 2.5f, headshotMultiplier = 2f, scopeFov = 13f, hipSpreadDegrees = 3f, scopeTint = new Color(1f, 0.78f, 0.2f), color = new Color(1f, 0.82f, 0.25f) },
        };

        public static int Count => all.Length;

        public static WeaponStats Get(int id) => all[Mathf.Clamp(id, 0, all.Length - 1)];

        /// <summary>The fixed weapon of a character (CharacterCatalog id, lower case).</summary>
        public static int ForCharacter(string characterId)
        {
            switch ((characterId ?? "").ToLowerInvariant())
            {
                case "army": return 0;      // balanced all-rounder (free character)
                case "orange": return 1;    // SMG, close and mid range (Orange is hidden for now, see CatalogBuilder)
                case "nobi": return 1;      // SMG - NobiCat took Orange's place (2026-10-19)
                case "black": return 6;     // quiet carbine
                case "white": return 9;     // Golden Cash Sniper (legendary; was the marksman rifle, id 4, unused now)
                case "baby": return 7;      // small, quick pistol
                case "soldier": return 2;   // heavy rifle
                case "gold": return 8;      // light machine gun
                case "knight": return 5;    // shotgun, brawler
                case "ninja": return 3;     // sniper
                default: return Starter;
            }
        }
    }
}
