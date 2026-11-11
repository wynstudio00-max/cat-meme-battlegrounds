using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Bullet holes: small dark quads stuck flat on the surface that was hit, pooled (oldest reused first), visible for a
    /// few seconds and then faded out. Material from Resources/FX/BulletHole (editor script FxAssets). Visual only.
    /// </summary>
    public class BulletHoles : MonoBehaviour
    {
        const int PoolSize = 64;
        const float Life = 9f, FadeTime = 2f;

        static BulletHoles instance;
        static Material material;
        static bool loaded;

        Transform[] quads;
        Renderer[] renderers;
        float[] born;
        int next;
        MaterialPropertyBlock block;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public static void Add(Vector3 point, Vector3 normal, float size)
        {
            if (Application.isBatchMode) return;
            if (!loaded) { loaded = true; material = Resources.Load<Material>("FX/BulletHole"); }
            if (material == null) return;
            if (instance == null) instance = new GameObject("BulletHoles").AddComponent<BulletHoles>();
            instance.Place(point, normal, size);
        }

        void Awake()
        {
            quads = new Transform[PoolSize]; renderers = new Renderer[PoolSize]; born = new float[PoolSize];
            block = new MaterialPropertyBlock();
            for (int i = 0; i < PoolSize; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.name = "Hole"; q.transform.SetParent(transform, false);
                var r = q.GetComponent<Renderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                q.SetActive(false);
                quads[i] = q.transform; renderers[i] = r; born[i] = -100f;
            }
        }

        void Place(Vector3 point, Vector3 normal, float size)
        {
            int i = next; next = (next + 1) % PoolSize;
            var q = quads[i];
            q.position = point + normal * 0.012f;                       // just above the surface, no z-fighting
            q.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            q.localScale = Vector3.one * size * Random.Range(0.85f, 1.15f);
            born[i] = Time.time;
            SetAlpha(i, 1f);
            q.gameObject.SetActive(true);
        }

        void Update()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (!quads[i].gameObject.activeSelf) continue;
                float age = Time.time - born[i];
                if (age > Life) { quads[i].gameObject.SetActive(false); continue; }
                if (age > Life - FadeTime) SetAlpha(i, (Life - age) / FadeTime);
            }
        }

        void SetAlpha(int i, float a)
        {
            renderers[i].GetPropertyBlock(block);
            block.SetColor(BaseColor, new Color(1f, 1f, 1f, a));
            renderers[i].SetPropertyBlock(block);
        }
    }
}
