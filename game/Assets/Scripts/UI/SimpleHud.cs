using UnityEngine;

namespace CatBattleground
{
    /// <summary>Placeholder IMGUI HUD: crosshair, health and ammo. Replaced by real UI later.</summary>
    public class SimpleHud : MonoBehaviour
    {
        [SerializeField] Health health;
        [SerializeField] PlayerShooter shooter;
        NetworkPlayer netPlayer;

        void Awake() => netPlayer = GetComponent<NetworkPlayer>();

        void OnGUI()
        {
            if (GameUI.Active) return;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - 1, cy - 8, 2, 16), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 8, cy - 1, 16, 2), Texture2D.whiteTexture);

            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            if (netPlayer != null && Time.time < netPlayer.BannerUntil)
            {
                var banner = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 28 };
                GUI.Label(new Rect(0, 90, Screen.width, 50), netPlayer.Banner, banner);
            }
            if (health != null)
            {
                GUI.Label(new Rect(24, Screen.height - 56, 400, 40), $"HP {Mathf.CeilToInt(health.Current)} / {Mathf.CeilToInt(health.Max)}", style);
                if (health.Armor > 0.5f)
                {
                    var armorStyle = new GUIStyle(style) { fontSize = 18 };
                    armorStyle.normal.textColor = new Color(0.45f, 0.7f, 1f);
                    GUI.Label(new Rect(24, Screen.height - 86, 400, 30), $"Armor {Mathf.CeilToInt(health.Armor)}", armorStyle);
                }
            }
            if (shooter != null)
            {
                string ammo = shooter.IsReloading ? "RELOADING..." : $"{shooter.Ammo} / {shooter.MagazineSize}   |   {shooter.Reserve}";
                var right = new GUIStyle(style) { alignment = TextAnchor.MiddleRight };
                GUI.Label(new Rect(Screen.width - 424, Screen.height - 56, 400, 40), ammo, right);
                var weaponStyle = new GUIStyle(right) { fontSize = 18 };
                weaponStyle.normal.textColor = shooter.Weapon.color;
                GUI.Label(new Rect(Screen.width - 424, Screen.height - 86, 400, 30), shooter.Weapon.name, weaponStyle);
            }
        }
    }
}
