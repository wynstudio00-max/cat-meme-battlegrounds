using UnityEditor;

namespace CatBattleground
{
    /// <summary>
    /// Import rules for Assets/Resources/Vehicles (the drop plane and the two parachutes from Blender; 2026-10-28 WebGL size pass:
    /// the plane's mesh alone was 2.1 MB of the data file). Medium mesh compression, no cameras / lights / animation.
    /// </summary>
    public class VehicleImportRules : AssetPostprocessor
    {
        public override uint GetVersion() => 1;

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith("Assets/Resources/Vehicles/") || !(assetImporter is ModelImporter mi)) return;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importBlendShapes = false;
        }
    }
}
