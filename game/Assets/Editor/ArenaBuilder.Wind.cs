using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    // Wind for the nature pack: every scattered plant / tree whose material uses the CatBattleground/Foliage shader gets a copy
    // of its mesh with a wind weight in the vertex colour R (0 at the root, 1 at the tip). The weight lives in the mesh, so it
    // survives static batching (which bakes the grass tufts into world-space meshes and loses the object pivots).
    public static partial class ArenaBuilder
    {
        const string WindMeshDir = "Assets/Art/Forest/WindMeshes";
        static readonly Dictionary<Mesh, Mesh> windMeshes = new Dictionary<Mesh, Mesh>();
        static Shader foliageShader;

        /// <summary>Clears the per-build cache and the generated meshes (called before the nature is scattered).</summary>
        static void ResetWindMeshes()
        {
            windMeshes.Clear();
            foliageShader = Shader.Find("CatBattleground/Foliage");
            if (AssetDatabase.IsValidFolder(WindMeshDir)) AssetDatabase.DeleteAsset(WindMeshDir);
            AssetDatabase.CreateFolder("Assets/Art/Forest", "WindMeshes");
        }

        static void ApplyWind(GameObject go, string model)
        {
            if (foliageShader == null) return;
            bool tree = model.Contains("Tree") || model.StartsWith("Pine");
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                var r = mf.GetComponent<Renderer>();
                if (r == null || mf.sharedMesh == null) continue;
                bool foliage = false;
                foreach (var m in r.sharedMaterials) if (m != null && m.shader == foliageShader) { foliage = true; break; }
                if (!foliage) continue;
                mf.sharedMesh = WindMesh(mf.sharedMesh, tree);
            }
        }

        static Mesh WindMesh(Mesh source, bool tree)
        {
            if (windMeshes.TryGetValue(source, out var cached)) return cached;
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_wind";
            var v = mesh.vertices;
            var b = mesh.bounds;
            float minY = b.min.y, h = Mathf.Max(0.001f, b.size.y);
            float radius = Mathf.Max(0.001f, Mathf.Max(b.extents.x, b.extents.z));
            var colors = new Color[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float y01 = Mathf.Clamp01((v[i].y - minY) / h);
                float w;
                if (tree)
                {
                    // the canopy sways, more at the top and at the outer edge; trunk and low branches stay still
                    float side = Mathf.Clamp01(new Vector2(v[i].x - b.center.x, v[i].z - b.center.z).magnitude / radius);
                    w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 1f, y01)) * (0.6f + 0.4f * side);
                }
                else w = Mathf.Pow(y01, 1.4f);   // grass / plants: root fixed, tip moves most
                colors[i] = new Color(w, 0f, 0f, 1f);
            }
            mesh.colors = colors;
            AssetDatabase.CreateAsset(mesh, $"{WindMeshDir}/{Sanitize(mesh.name)}_{windMeshes.Count}.asset");
            windMeshes[source] = mesh;
            return mesh;
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
