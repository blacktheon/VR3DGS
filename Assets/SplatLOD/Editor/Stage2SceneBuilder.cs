using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Gsplat;
using Gsplat.Editor;
using SplatPreprocess;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplatLOD.Editor
{
    public static class Stage2SceneBuilder
    {
        public static string LastPrefabPath { get; private set; }

        [Serializable] public sealed class SurfaceInput
        {
            public string name, material_path, material_sha256, texture, texture_sha256;
            public float[] local_to_world;
        }
        [Serializable] public sealed class AcceptedInput
        {
            public int schema_version, accepted_count;
            public string input_id, ply_path, ply_sha256;
            public float[] model_local_to_world;
            public SurfaceInput[] surfaces;
        }

        public static Stage2ReviewModel UpdateGenerated(GsplatRenderer renderer, TextAsset layoutAsset, string expectedHash, Material lineMaterial)
        {
            if (!renderer || !renderer.GsplatAsset || !layoutAsset) throw new ArgumentException("Assign an accepted asset and layout");
            var layout = Stage2Layout.Parse(layoutAsset.text, expectedHash, checked((int)renderer.GsplatAsset.SplatCount));
            var existing = renderer.transform.Find("Stage2 Generated");
            if (existing && !existing.GetComponent<Stage2ReviewModel>())
                throw new InvalidOperationException("An authored object already uses the generated hierarchy name");
            var root = existing ? existing.gameObject : new GameObject("Stage2 Generated");
            if (!existing) { Undo.RegisterCreatedObjectUndo(root, "Create Stage2 review hierarchy"); root.transform.SetParent(renderer.transform, false); }
            var review = root.GetComponent<Stage2ReviewModel>() ?? Undo.AddComponent<Stage2ReviewModel>(root);
            Undo.RecordObject(review, "Update Stage2 layout");
            review.Configure(renderer, layoutAsset, expectedHash, lineMaterial);
            var previous = root.GetComponentsInChildren<Stage2Chunk>(true).ToDictionary(c => c.ChunkId, StringComparer.Ordinal);
            foreach (var chunk in layout.chunks)
            {
                if (!previous.Remove(chunk.id, out var item))
                {
                    var go = new GameObject(chunk.id);
                    Undo.RegisterCreatedObjectUndo(go, "Create Stage2 chunk metadata");
                    go.transform.SetParent(root.transform, false);
                    item = Undo.AddComponent<Stage2Chunk>(go);
                }
                Undo.RecordObject(item, "Update Stage2 chunk metadata");
                item.Configure(layout, chunk);
                EditorUtility.SetDirty(item);
            }
            foreach (var stale in previous.Values) Undo.DestroyObjectImmediate(stale.gameObject);
            EditorUtility.SetDirty(review);
            return review;
        }

        public static Stage2ReviewModel BuildCurrentScene(string inputManifestPath, string layoutPath)
        {
            var scene = SceneManager.GetActiveScene();
            if (Application.isPlaying || scene.name != "Stage2" || !scene.isLoaded)
                throw new InvalidOperationException("Open the duplicated Stage2 scene in Edit Mode first");
            inputManifestPath = Path.GetFullPath(inputManifestPath);
            layoutPath = Path.GetFullPath(layoutPath);
            var input = JsonUtility.FromJson<AcceptedInput>(File.ReadAllText(inputManifestPath));
            if (input == null || input.schema_version != 1 || input.accepted_count <= 0 || !Stage2Layout.IsHash(input.ply_sha256))
                throw new InvalidDataException("Invalid accepted Stage1 input");
            var layout = Stage2Layout.Parse(File.ReadAllText(layoutPath), input.ply_sha256, input.accepted_count);
            if (layout.input_id != input.input_id || Hash(inputManifestPath) != layout.input_manifest_sha256)
                throw new InvalidDataException("Chunk layout is from another accepted input version");
            ValidateOwnership(Path.GetDirectoryName(layoutPath), layout);
            var sourcePly = Path.Combine(Path.GetDirectoryName(inputManifestPath), input.ply_path);
            if (Hash(sourcePly) != input.ply_sha256)
                throw new InvalidDataException("Accepted PLY differs from the original exported bytes");
            var renderers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<GsplatRenderer>(true)).ToArray();
            if (renderers.Length != 1) throw new InvalidDataException("Stage2 requires one shared accepted-model renderer");
            var renderer = renderers[0];
            CheckTransform(renderer.transform, input.model_local_to_world, "accepted model");
            var surfaces = scene.GetRootGameObjects().SingleOrDefault(g => g.name == "Surfaces");
            if (!surfaces || input.surfaces == null || input.surfaces.Length == 0) throw new InvalidDataException("Accepted surfaces are missing");
            var planeRenderers = surfaces.GetComponentsInChildren<MeshRenderer>(true);
            if (planeRenderers.Length != input.surfaces.Length) throw new InvalidDataException("Authored and accepted surface counts differ");
            var assignments = new List<(MeshRenderer renderer, Material material)>();
            foreach (var surface in input.surfaces)
            {
                var plane = planeRenderers.SingleOrDefault(r => "Surfaces/" + r.name == surface.name);
                if (!plane) throw new InvalidDataException("Surface is missing: " + surface.name);
                CheckTransform(plane.transform, surface.local_to_world, surface.name);
                var material = AssetDatabase.LoadAssetAtPath<Material>(surface.material_path);
                if (!material || Hash(surface.material_path) != surface.material_sha256 ||
                    Hash(AssetDatabase.GetAssetPath(material.mainTexture)) != surface.texture_sha256)
                    throw new InvalidDataException("Surface material/texture changed: " + surface.name);
                assignments.Add((plane, material));
            }
            if (string.IsNullOrEmpty(input.input_id) || input.input_id.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
                throw new InvalidDataException("Invalid accepted input identity");
            string generatedFolder = "Assets/SplatLOD/Generated/" + input.input_id;
            Directory.CreateDirectory(generatedFolder);
            string acceptedAssetPath = generatedFolder + "/accepted.ply";
            if (!File.Exists(acceptedAssetPath)) File.Copy(sourcePly, acceptedAssetPath);
            if (Hash(acceptedAssetPath) != input.ply_sha256) throw new InvalidDataException("Existing imported version has different bytes");
            var asset = ImportAcceptedAsset(acceptedAssetPath, input.accepted_count);
            // All immutable inputs and authored transforms have passed before scene mutation.
            foreach (var behaviour in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)))
            {
                if (!(behaviour is Stage1SelectionController || behaviour is Stage1ReviewActions || behaviour is Stage1PhysicalSliderBinding ||
                      behaviour is Stage1PreviewPanel || behaviour is Stage1AuthoredWalls)) continue;
                if (behaviour is Stage1ReviewActions actions) actions.EndSession();
                Undo.RecordObject(behaviour, "Use Stage2 review controls");
                behaviour.enabled = false;
                if (behaviour is Stage1PreviewPanel)
                {
                    var serialized = new SerializedObject(behaviour);
                    var text = serialized.FindProperty("_statusText").objectReferenceValue as TextMesh;
                    if (text) { Undo.RecordObject(text.GetComponent<Renderer>(), "Hide Stage1 status"); text.GetComponent<Renderer>().enabled = false; }
                }
            }
            Undo.RecordObject(renderer, "Use accepted Stage2 LOD0");
            renderer.ClearStage1Selection(); renderer.ClearStage1Walls();
            var rendererSettings = new SerializedObject(renderer);
            rendererSettings.FindProperty("GsplatAsset").objectReferenceValue = asset;
            rendererSettings.ApplyModifiedPropertiesWithoutUndo();
            renderer.SHDegree = 0;
            renderer.AsyncUpload = false; renderer.GammaToLinear = true;
            renderer.ReloadAsset(); renderer.name = "Accepted LOD0 - " + input.accepted_count;
            foreach (var pair in assignments) { Undo.RecordObject(pair.renderer, "Keep accepted surface material"); pair.renderer.sharedMaterial = pair.material; }
            string layoutAssetPath = generatedFolder + "/" + layout.layout_id + ".json";
            if (Path.GetFullPath(layoutAssetPath) != layoutPath) File.Copy(layoutPath, layoutAssetPath, true);
            AssetDatabase.ImportAsset(layoutAssetPath, ImportAssetOptions.ForceSynchronousImport);
            var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(layoutAssetPath);
            string materialPath = generatedFolder + "/ChunkLines.mat";
            var lines = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (!lines)
            {
                var shader = Shader.Find("SplatLOD/Chunk Lines");
                if (!shader) throw new InvalidOperationException("Chunk line shader did not compile");
                lines = new Material(shader) { name = "Stage2 Red Chunk Bounds" };
                AssetDatabase.CreateAsset(lines, materialPath);
            }
            var result = UpdateGenerated(renderer, textAsset, input.ply_sha256, lines);
            LastPrefabPath = generatedFolder + "/AcceptedMachine.prefab";
            SaveAcceptedPrefab(renderer, surfaces.transform, LastPrefabPath);
            EditorSceneManager.MarkSceneDirty(scene);
            return result;
        }

        public static GsplatAssetUncompressed ImportAcceptedAsset(string assetPath, int count)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(assetPath) as GsplatImporter;
            if (!importer) throw new InvalidDataException("The exported PLY importer is unavailable");
            if (importer.Compression != CompressionMode.Uncompressed || importer.SourceCoordinates != SourceCoordinates.RUB || importer.OpacityPruneThreshold != 0)
            {
                importer.Compression = CompressionMode.Uncompressed; importer.SourceCoordinates = SourceCoordinates.RUB; importer.OpacityPruneThreshold = 0;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            var asset = AssetDatabase.LoadAssetAtPath<GsplatAssetUncompressed>(assetPath);
            if (!asset || asset.SplatCount != count || asset.PrunedSplatCount != 0)
                throw new InvalidDataException("Imported LOD0 row count differs from the accepted export");
            return asset;
        }

        static void SaveAcceptedPrefab(GsplatRenderer source, Transform surfaces, string prefabPath)
        {
            var root = new GameObject("Accepted Machine - " + source.GsplatAsset.SplatCount);
            root.SetActive(false);
            try
            {
                var model = new GameObject("Accepted LOD0");
                model.transform.SetParent(root.transform, false);
                model.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                model.transform.localScale = source.transform.lossyScale;
                var renderer = model.AddComponent<GsplatRenderer>();
                EditorUtility.CopySerialized(source, renderer);
                var planes = UnityEngine.Object.Instantiate(surfaces.gameObject, root.transform);
                planes.name = "Surfaces";
                planes.transform.SetPositionAndRotation(surfaces.position, surfaces.rotation);
                planes.transform.localScale = surfaces.lossyScale;
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (!prefab) throw new IOException("Could not save the accepted machine prefab");
                // Save an active prefab without activating another renderer in the review scene.
                var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                try { contents.SetActive(true); PrefabUtility.SaveAsPrefabAsset(contents, prefabPath); }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void CheckTransform(Transform transform, float[] recorded, string label)
        {
            if (recorded == null || recorded.Length != 16) throw new InvalidDataException("Missing transform for " + label);
            var matrix = transform.localToWorldMatrix;
            for (int row = 0; row < 4; ++row) for (int column = 0; column < 4; ++column)
                if (!float.IsFinite(recorded[row*4+column]) || Mathf.Abs(matrix[row,column]-recorded[row*4+column]) > .00002f)
                    throw new InvalidDataException("Transform changed for " + label);
        }

        static void ValidateOwnership(string directory, Stage2Layout layout)
        {
            string membersPath = Inside(directory, layout.members_path), ownershipPath = Inside(directory, layout.ownership_path);
            if (Hash(membersPath) != layout.members_sha256 || Hash(ownershipPath) != layout.ownership_sha256)
                throw new InvalidDataException("Chunk ownership hash mismatch");
            var members = File.ReadAllBytes(membersPath); var owners = File.ReadAllBytes(ownershipPath);
            if (members.LongLength != layout.count*4L || owners.LongLength != layout.count*4L)
                throw new InvalidDataException("Chunk ownership array length mismatch");
            var seen = new bool[layout.count];
            for (int chunk = 0; chunk < layout.chunks.Length; ++chunk)
            {
                var part = layout.chunks[chunk];
                for (int slot = part.offset; slot < part.offset + part.count; ++slot)
                {
                    uint row = BitConverter.ToUInt32(members, slot*4);
                    if (row >= layout.count || seen[row] || BitConverter.ToUInt32(owners, (int)row*4) != chunk)
                        throw new InvalidDataException("Chunk ownership is missing, duplicated or inconsistent");
                    seen[row] = true;
                }
            }
        }
        static string Inside(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) throw new InvalidDataException("Expected a local ownership filename");
            return Path.Combine(directory, name);
        }
        public static string Hash(string path)
        {
            using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
