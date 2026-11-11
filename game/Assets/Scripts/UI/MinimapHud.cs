using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Minimap (top right) and full map (M). The island is a pre-rendered texture; this draws the current safe zone
    /// (blue), the next zone (white) and your own cat on top. Enemies are never shown. IMGUI, no extra camera.
    /// </summary>
    public class MinimapHud : MonoBehaviour
    {
        [SerializeField] Texture2D map;
        [SerializeField] float worldExtent = 75f;
        [SerializeField] float smallSize = 190f;
        [Header("Zombie Survival town (ArenaBuilder.Zombie)")]
        [SerializeField] Texture2D zombieMap;
        [SerializeField] Vector3 zombieCentre;
        [SerializeField] float zombieExtent = 60f;

        bool big;
        Texture2D arrow;

        bool ZombieTown => zombieMap != null && MatchManager.Instance != null && MatchManager.Instance.IsZombies;
        /// <summary>The map texture, the half size of the world it covers and its centre (GameHud draws its round minimap from these).
        /// In Zombie Survival: the zombie town's own top-down picture.</summary>
        public Texture2D Map => ZombieTown ? zombieMap : map;
        public float Extent => ZombieTown ? zombieExtent : worldExtent;
        public Vector3 Centre => ZombieTown ? zombieCentre : Vector3.zero;
        const int Segments = 64;

        void Start() => arrow = MakeArrow();

        void Update()
        {
            if (GameInput.MapPressed) big = !big;
            else if (big && GameInput.TouchMode && GameInput.TapAnywhere) big = false;   // phones: a tap closes the big map
        }

        void OnGUI()
        {
            if (Map == null || Application.isBatchMode) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.LocalClient == null || nm.LocalClient.PlayerObject == null) return;
            var player = nm.LocalClient.PlayerObject;
            var health = player.GetComponent<Health>();
            if (!big && GameHud.Active) return;   // GameHud draws the small round minimap; this keeps the big map (M)

            float size = big ? Mathf.Min(Screen.width, Screen.height) * 0.8f : smallSize;
            var rect = big
                ? new Rect((Screen.width - size) * 0.5f, (Screen.height - size) * 0.5f, size, size)
                : new Rect(Screen.width - size - 14f, 14f, size, size);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(rect.x - 3, rect.y - 3, rect.width + 6, rect.height + 6), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.BeginGroup(rect);   // clips everything drawn inside to the map
            GUI.DrawTexture(new Rect(0, 0, size, size), Map);

            var match = MatchManager.Instance;
            var zone = match != null ? match.Zone : null;
            if (zone != null && zone.Active)
            {
                DrawCircle(ToMap(zone.NextCenter, size), zone.NextRadius / (Extent * 2f) * size, new Color(1f, 1f, 1f, 0.95f), big ? 2.5f : 1.5f);
                DrawCircle(ToMap(zone.Center, size), zone.Radius / (Extent * 2f) * size, new Color(0.3f, 0.6f, 1f, 1f), big ? 3.5f : 2.5f);
            }

            // your cat: an arrow pointing where you look; grey once eliminated
            var pos = ToMap(player.transform.position, size);
            pos.x = Mathf.Clamp(pos.x, 6f, size - 6f);
            pos.y = Mathf.Clamp(pos.y, 6f, size - 6f);
            float marker = big ? 22f : 16f;
            var old = GUI.matrix;
            GUIUtility.RotateAroundPivot(player.transform.eulerAngles.y, pos);
            GUI.color = health != null && health.IsDead ? new Color(0.5f, 0.5f, 0.5f) : Color.white;
            GUI.DrawTexture(new Rect(pos.x - marker * 0.5f, pos.y - marker * 0.5f, marker, marker), arrow);
            GUI.matrix = old;
            GUI.color = Color.white;
            GUI.EndGroup();

            if (!big)
            {
                var small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = 12 };
                GUI.Label(new Rect(rect.x, rect.yMax + 2, rect.width, 18), "[M] big map", small);
            }
        }

        // north (+z) is up, east (+x) is right
        Vector2 ToMap(Vector3 world, float size)
        {
            float e = Extent; var c = Centre;
            return new Vector2((world.x - c.x + e) / (e * 2f) * size, (e - (world.z - c.z)) / (e * 2f) * size);
        }

        static void DrawCircle(Vector2 centre, float radius, Color color, float thickness)
        {
            if (radius < 1f) radius = 1f;
            GUI.color = color;
            var old = GUI.matrix;
            float segment = 2f * Mathf.PI * radius / Segments + 1f;
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                var p = new Vector2(centre.x + Mathf.Cos(a) * radius, centre.y + Mathf.Sin(a) * radius);
                GUI.matrix = old;
                // a thin vertical bar rotated by the angle lies along the circle's tangent (GUI rotation is clockwise, y points down)
                GUIUtility.RotateAroundPivot(a * Mathf.Rad2Deg, p);
                GUI.DrawTexture(new Rect(p.x - thickness * 0.5f, p.y - segment * 0.5f, thickness, segment), Texture2D.whiteTexture);
            }
            GUI.matrix = old;
            GUI.color = Color.white;
        }

        // Small white triangle pointing up.
        static Texture2D MakeArrow()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // triangle: apex at top centre, base at the bottom with a notch
                    float t = y / (n - 1f);                       // 0 bottom .. 1 top
                    float half = (1f - t) * 0.5f * n * 0.9f;       // half width at this height
                    bool inside = Mathf.Abs(x - (n - 1) * 0.5f) <= half && y >= 2 && y <= n - 3;
                    bool notch = y < n * 0.38f && Mathf.Abs(x - (n - 1) * 0.5f) < (n * 0.38f - y) * 0.45f;
                    tex.SetPixel(x, y, inside && !notch ? Color.white : clear);
                }
            tex.Apply();
            return tex;
        }
    }
}
