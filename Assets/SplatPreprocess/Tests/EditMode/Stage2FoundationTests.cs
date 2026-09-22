using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using Gsplat;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace SplatPreprocess.Tests
{
    public sealed class Stage2FoundationTests
    {
        const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string Chunk = "{\"id\":\"r0\",\"group\":\"machine\",\"count\":2,\"offset\":0,\"partition_min\":[-1,-1,-1],\"partition_max\":[1,1,1],\"render_min\":[-2,-2,-2],\"render_max\":[2,2,2],\"available_lods\":[0]}";
        static string Json(string chunks = Chunk, int count = 2) => "{\"schema_version\":1,\"layout_id\":\"layout-fixture\",\"input_id\":\"accepted-fixture\",\"input_ply_sha256\":\"" + Hash + "\",\"coordinate_space\":\"renderer_local_RUF\",\"count\":" + count + ",\"available_lods\":[0],\"chunks\":["+chunks+"]}";
        static Type Required(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "Stage2 implementation missing: " + name);
            return type;
        }
        static object Parse(string json, string expected = Hash, int count = 2)
            => Required("SplatLOD.Stage2Layout").GetMethod("Parse").Invoke(null, new object[] { json, expected, count });

        [Test]
        public void Stage2AcceptsLayoutOnlyForTheExactExport()
        {
            Assert.That(Parse(Json()), Is.Not.Null);
            var error = Assert.Throws<TargetInvocationException>(() => Parse(Json(), new string('b', 64)));
            Assert.That(error.InnerException, Is.InstanceOf<ArgumentException>());
            Assert.Throws<TargetInvocationException>(() => Parse(Json(), Hash, 3));
        }

        [TestCase("duplicate")]
        [TestCase("count")]
        [TestCase("bounds")]
        [TestCase("offset")]
        [TestCase("level")]
        public void Stage2RejectsInvalidChunkCoverageBeforeBuilding(string problem)
        {
            string value = problem switch
            {
                "duplicate" => Json(Chunk + "," + Chunk, 4),
                "count" => Json(Chunk, 3),
                "bounds" => Json().Replace("[-1,-1,-1]", "[2,-1,-1]"),
                "offset" => Json().Replace("\"offset\":0", "\"offset\":1"),
                _ => Json().Replace("\"available_lods\":[0]", "\"available_lods\":[0,1]")
            };
            Required("SplatLOD.Stage2Layout");
            Assert.Throws<TargetInvocationException>(() => Parse(value, Hash, problem == "duplicate" ? 4 : 2));
        }

        [Test]
        public void Stage2APressTogglesOnceWhileHeldAndCanRearm()
        {
            var type = Required("SplatLOD.Stage2ButtonEdges");
            var edges = Activator.CreateInstance(type);
            var sample = type.GetMethod("Sample");
            bool Step(bool a) => (bool)sample.Invoke(edges, new object[] { a });
            Assert.That(Step(false), Is.False);
            Assert.That(Step(true), Is.True);
            Assert.That(Step(true), Is.False);
            Assert.That(Step(false), Is.False);
            Assert.That(Step(true), Is.True);
        }

        [Test]
        public void Stage2ImportPersistsLosslessSettingsAcrossReimport()
        {
            var builder = Required("SplatLOD.Editor.Stage2SceneBuilder");
            string path = "Assets/stage2-import-test-" + Guid.NewGuid().ToString("N") + ".ply";
            string[] fields = { "x","y","z","rot_0","rot_1","rot_2","rot_3","scale_0","scale_1","scale_2","opacity","f_dc_0","f_dc_1","f_dc_2" };
            try
            {
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement vertex 1\n" + string.Concat(fields.Select(f => "property float " + f + "\n")) + "end_header\n"));
                    foreach (float v in new float[] { 1.234567f, 2, 3, 1, 0, 0, 0, -2, -2, -2, -20, .23f, .5f, .71f }) writer.Write(v);
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var asset = (GsplatAssetUncompressed)builder.GetMethod("ImportAcceptedAsset").Invoke(null, new object[] { path, 1 });
                Assert.That(asset.Positions[0].x, Is.EqualTo(1.234567f));
                Assert.That(asset.Positions[0].z, Is.EqualTo(-3f));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.TypeOf<GsplatAssetUncompressed>());
                Assert.That(AssetDatabase.LoadAssetAtPath<GsplatAssetUncompressed>(path).SplatCount, Is.EqualTo(1));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void Stage2RebuildPreservesAuthoredObjectsAndOneSharedRenderer()
        {
            var builder = Required("SplatLOD.Editor.Stage2SceneBuilder");
            var model = new GameObject("stage2 model test");
            model.SetActive(false);
            var original = new GameObject("authored attachment");
            original.transform.SetParent(model.transform, false);
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 2; asset.SHBands = 0; asset.Allocate();
            var renderer = model.AddComponent<GsplatRenderer>(); renderer.GsplatAsset = asset;
            var text = new TextAsset(Json());
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            try
            {
                var method = builder.GetMethod("UpdateGenerated");
                var args = new object[] { renderer, text, Hash, material };
                var first = (Component)method.Invoke(null, args);
                var second = (Component)method.Invoke(null, args);
                Assert.That(second, Is.SameAs(first));
                Assert.That(original.transform.parent, Is.EqualTo(model.transform));
                Assert.That(model.GetComponentsInChildren<GsplatRenderer>(true).Length, Is.EqualTo(1));
                var chunkType = Required("SplatLOD.Stage2Chunk");
                Assert.That(model.GetComponentsInChildren(chunkType, true).Length, Is.EqualTo(1));
                var mesh = first.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh.vertexCount, Is.EqualTo(8));
                Assert.That(mesh.GetTopology(0), Is.EqualTo(MeshTopology.Lines));
                Assert.That(mesh.GetIndexCount(0), Is.EqualTo(24));
                first.GetType().GetMethod("SetBoxesVisible").Invoke(first, new object[] { true });
                Assert.That(first.GetComponent<MeshRenderer>().enabled, Is.True);
                first.GetType().GetMethod("SetBoxesVisible").Invoke(first, new object[] { false });
                Assert.That(first.GetComponent<MeshRenderer>().enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(model);
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEngine.Object.DestroyImmediate(text);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
