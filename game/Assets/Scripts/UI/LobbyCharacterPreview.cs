using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// The selected cat standing in the lobby's centre frame: a visual-only copy of its Player_* prefab (every script, network
    /// and physics component is stripped before it wakes up, so nothing of the game runs) on a little stage far above the
    /// island, filmed by its own camera into a transparent RenderTexture that LobbyUI shows. Idle animation from the cat's own
    /// animator, a slow turn that pauses while the player drags it, key + rim light.
    /// </summary>
    public class LobbyCharacterPreview : MonoBehaviour
    {
        static readonly Vector3 StagePos = new Vector3(0f, 900f, 0f);   // above the clouds (150-240 m), nothing around
        public RenderTexture Texture { get; private set; }
        Transform pivot;
        GameObject model;
        float yaw = 200f, autoSpin = 18f, lastDrag = -10f;

        public static LobbyCharacterPreview Create()
        {
            var go = new GameObject("LobbyCharacterPreview");
            go.transform.position = StagePos;
            return go.AddComponent<LobbyCharacterPreview>();
        }

        void Awake()
        {
// same aspect as LobbyUI's CatPreview frame (900 x 700 ref px); vertical FOV unchanged, so the cat keeps its size and
            // the wider view (about +-1.35 m at the cat) leaves room for long guns while it turns (2026-11-07)
            Texture = new RenderTexture(1800, 1400, 24, RenderTextureFormat.ARGB32) { name = "LobbyCat", antiAliasing = 4 };
            pivot = new GameObject("Pivot").transform;
            pivot.SetParent(transform, false);

            var camGo = new GameObject("LobbyCamera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.05f, 4.3f);
            camGo.transform.LookAt(transform.position + Vector3.up * 0.85f);
            var cam = camGo.AddComponent<Camera>();
            cam.targetTexture = Texture;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.fieldOfView = 30f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 30f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;   // keeps the alpha channel
            data.renderShadows = true;

            // key (warm, front-left) and rim (cool, behind) lights only reach the stage
            AddLight("Key", new Vector3(-2.2f, 3f, 3f), new Color(1f, 0.93f, 0.82f), 3.5f, 10f);
            AddLight("Rim", new Vector3(1.8f, 2.4f, -2.5f), new Color(0.55f, 0.7f, 1f), 5f, 9f);
            AddLight("Fill", new Vector3(2.5f, 1f, 2.5f), new Color(0.75f, 0.7f, 1f), 1.4f, 9f);
        }

        void AddLight(string name, Vector3 local, Color color, float intensity, float range)
        {
            var l = new GameObject(name).AddComponent<Light>();
            l.transform.SetParent(transform, false);
            l.transform.localPosition = local;
            l.type = LightType.Point; l.color = color; l.intensity = intensity; l.range = range; l.shadows = LightShadows.None;
        }

        /// <summary>Shows this player prefab (null = nothing).</summary>
        public void Show(GameObject playerPrefab)
        {
            if (model != null) Destroy(model);
            model = null;
            if (playerPrefab == null) return;
            // instantiate under an inactive holder so no Awake / Start of the game scripts ever runs, then strip them
            var holder = new GameObject("Holder"); holder.SetActive(false);
            var copy = Instantiate(playerPrefab, holder.transform);
            // newest first: components that depend on others (NetworkTransform -> NetworkObject, PlayerController ->
            // CharacterController) were added after them, so removing in reverse never hits a [RequireComponent]
            var comps = copy.GetComponentsInChildren<Component>(true);
            for (int i = comps.Length - 1; i >= 0; i--)
            {
                var c = comps[i];
                if (c == null || c is Transform || c is Animator || c is Renderer || c is MeshFilter) continue;
                DestroyImmediate(c);
            }
            copy.transform.SetParent(pivot, false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            Destroy(holder);
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            var anim = copy.GetComponent<Animator>();
            if (anim != null)
            {
                anim.applyRootMotion = false; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                // the hand-made lobby pose loop (AnimatorBuilder "Lobby_Idle"; cats without one show their Idle_Rifle)
                foreach (var p in anim.parameters)
                    if (p.name == "Lobby" && p.type == AnimatorControllerParameterType.Bool) { anim.SetBool("Lobby", true); break; }
            }
            model = copy;
            ApplyLobbyWeaponFit(copy);
        }

        // ---------------------------------------------------------------- lobby-only gun fit (2026-11-07)
        // Resources/LobbyWeaponFit.json comes from Tools/BlenderAnim/install_weapon_fit.py -- lobby (the user moves the guns in
        // CHARACTER/weapon_fit_work.blend while the cats play their lobby idle). Each entry = change in the gun's own local space,
        // already in Unity axes. Only this preview copy is changed: the game prefabs / clips keep the master placement.
        [System.Serializable] class Fit { public string id; public bool hide; public Vector3 size; public Vector3 pos; public Quaternion rot; public Vector3 scale = Vector3.one; }
        [System.Serializable] class FitFile { public Fit[] cats; }
        static FitFile fits;
        Transform fitGun, fitMag;
        Vector3 gunPos, gunScale, magPos, magScale;
        Quaternion gunRot, magRot;

        static Fit FindFit(string suffix)
        {
            if (fits == null)
            {
                var ta = Resources.Load<TextAsset>("LobbyWeaponFit");
                try { fits = ta != null ? JsonUtility.FromJson<FitFile>(ta.text) : null; } catch (System.Exception) { fits = null; }
                if (fits == null) fits = new FitFile { cats = new Fit[0] };
            }
            if (fits.cats != null) foreach (var f in fits.cats) if (f != null && f.id == suffix) return f;
            return null;
        }

        static void SetLocal(Transform t, Matrix4x4 m, out Vector3 p, out Quaternion r, out Vector3 s)
        {
            p = m.GetColumn(3); r = m.rotation; s = m.lossyScale;
            t.localPosition = p; t.localRotation = r; t.localScale = s;
        }

        static Matrix4x4 Local(Transform t) => Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);

        void ApplyLobbyWeaponFit(GameObject copy)
        {
            fitGun = fitMag = null;
            foreach (var t in copy.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("assaultRiflle_")) continue;
                var fit = FindFit(t.name.Substring("assaultRiflle_".Length));
                if (fit == null) return;
                if (fit.hide)
                {
                    // no weapon in the lobby (e.g. Ninja Cat): hide the gun and its magazine
                    t.gameObject.SetActive(false);
                    var hiddenMag = FindChild(copy.transform, "Magazine_" + t.name.Substring("assaultRiflle_".Length));
                    if (hiddenMag != null) hiddenMag.gameObject.SetActive(false);
                    return;
                }
                var g = Local(t);
                // the offset is in the gun's mesh space measured in Blender: rescale it if Unity stored the mesh in other units
                Vector3 pos = fit.pos;
                var mf = t.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : (t.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer smr ? smr.sharedMesh : null);
                if (mesh != null && fit.size.x > 1e-5f && fit.size.y > 1e-5f && fit.size.z > 1e-5f)
                {
                    var u = mesh.bounds.size;
                    var k = new Vector3(u.x / fit.size.x, u.y / fit.size.y, u.z / fit.size.z);
                    float avg = (k.x + k.y + k.z) / 3f;
                    if (Mathf.Abs(k.x - avg) > 0.05f * avg || Mathf.Abs(k.y - avg) > 0.05f * avg || Mathf.Abs(k.z - avg) > 0.05f * avg)
                        Debug.LogWarning("LobbyWeaponFit: " + t.name + " mesh axes differ from Blender (ratio " + k + "); the lobby offset may be off.");
                    pos *= avg;
                }
                var gn = g * Matrix4x4.TRS(pos, fit.rot, fit.scale);
                SetLocal(t, gn, out gunPos, out gunRot, out gunScale);
                fitGun = t;
                // the magazine hangs on its own 'Magazine' bone (sibling of the gun under the hand): move it along
                var mag = FindChild(copy.transform, "Magazine_" + t.name.Substring("assaultRiflle_".Length));
                if (mag != null && mag.parent != null && mag.parent.parent == t.parent)
                {
                    var mb = Local(mag.parent);
                    var e = gn * g.inverse;
                    SetLocal(mag, mb.inverse * e * mb * Local(mag), out magPos, out magRot, out magScale);
                    fitMag = mag;
                }
                return;
            }
        }

        static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        void LateUpdate()
        {
            // keep the fit even if a clip carries curves for these objects
            if (fitGun != null) { fitGun.localPosition = gunPos; fitGun.localRotation = gunRot; fitGun.localScale = gunScale; }
            if (fitMag != null) { fitMag.localPosition = magPos; fitMag.localRotation = magRot; fitMag.localScale = magScale; }
        }

        public void Spin(float dx) { yaw -= dx * 0.4f; lastDrag = Time.unscaledTime; }

        void Update()
        {
            if (Time.unscaledTime - lastDrag > 2f) yaw += autoSpin * Time.unscaledDeltaTime;
            pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        void OnDestroy() { if (Texture != null) Texture.Release(); }
    }
}
