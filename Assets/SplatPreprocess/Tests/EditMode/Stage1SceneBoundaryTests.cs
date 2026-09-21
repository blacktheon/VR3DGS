using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SplatPreprocess.Editor;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1SceneBoundaryTests
    {
        const string SourceHash = "7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d";
        const string AssetGuid = "f26c73b9806ba5444a6a3c6c4bb68a1d";
        const string Report = "{" +
            "\"schema_version\":1,\"status\":\"passed\"," +
            "\"source_sha256\":\"" + SourceHash + "\",\"imported_file_sha256\":\"" + SourceHash + "\"," +
            "\"asset_guid\":\"" + AssetGuid + "\",\"source_count\":2,\"asset_count\":2," +
            "\"asset_pruned_count\":0,\"sh_degree\":0,\"compression\":\"Uncompressed\",\"source_coordinates\":\"RUB\"," +
            "\"opacity_prune_threshold\":0,\"checked_rows\":2,\"checked_scalars\":28,\"mismatch_rows\":0,\"mismatch_scalars\":0," +
            "\"bitwise_float32_comparison\":true,\"source_and_imported_file_hashes_match\":true," +
            "\"duplicate_position_fixture_passed\":true,\"shuffled_fixture_rejected\":true," +
            "\"id_rule\":\"zero_based_vertex_row\",\"mapping_encoding\":\"little_endian_uint32\",\"storage_to_source_bytes\":8," +
            "\"error\":\"\"}";

        string directory;
        readonly List<UnityEngine.Object> owned = new();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "stage1-scene-boundary-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var value in owned) if (value) UnityEngine.Object.DestroyImmediate(value);
            owned.Clear();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void CompleteAuditBindsTheExactInspectedSourceAndRendererAsset()
        {
            var validate = Method("ValidateIdentityReport");
            var path = WriteReport(Report);
            Assert.DoesNotThrow(() => Invoke(validate, path, SourceHash, AssetGuid, 2));
            Assert.Throws<InvalidDataException>(() => Invoke(validate, path, new string('a', 64), AssetGuid, 2));
            Assert.Throws<InvalidDataException>(() => Invoke(validate, path, SourceHash, new string('b', 32), 2));
            Assert.Throws<InvalidDataException>(() => Invoke(validate, path, SourceHash, AssetGuid, 3));
        }

        [TestCase("\"status\":\"passed\"", "\"status\":\"failed\"")]
        [TestCase("\"source_coordinates\":\"RUB\"", "\"source_coordinates\":\"RUF\"")]
        [TestCase("\"compression\":\"Uncompressed\"", "\"compression\":\"Spark\"")]
        [TestCase("\"asset_pruned_count\":0", "\"asset_pruned_count\":1")]
        [TestCase("\"opacity_prune_threshold\":0", "\"opacity_prune_threshold\":0.1")]
        [TestCase("\"sh_degree\":0", "\"sh_degree\":1")]
        [TestCase("\"checked_rows\":2", "\"checked_rows\":1")]
        [TestCase("\"checked_scalars\":28", "\"checked_scalars\":27")]
        [TestCase("\"asset_count\":2", "\"asset_count\":1")]
        [TestCase("\"mismatch_scalars\":0", "\"mismatch_scalars\":1")]
        [TestCase("\"bitwise_float32_comparison\":true", "\"bitwise_float32_comparison\":false")]
        [TestCase("\"source_and_imported_file_hashes_match\":true", "\"source_and_imported_file_hashes_match\":false")]
        [TestCase("\"imported_file_sha256\":\"" + SourceHash + "\"", "\"imported_file_sha256\":\"other\"")]
        public void IncompleteOrIncompatibleAuditCannotAuthorizeTheScene(string before, string after)
        {
            var validate = Method("ValidateIdentityReport");
            Assert.Throws<InvalidDataException>(() => Invoke(validate, WriteReport(Report.Replace(before, after)), SourceHash, AssetGuid, 2));
        }

        [Test]
        public void WallProfileAcceptsTheWorkersFixedRearDepth()
        {
            var validate = Method("ValidateWallProfile");
            Assert.DoesNotThrow(() => Invoke(validate, .1f));
        }

        [TestCase(0f)]
        [TestCase(.05f)]
        [TestCase(.100001f)]
        [TestCase(.2f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void APreviewDepthDifferentFromTheFrozenWorkerProfileIsRejected(float rearDepth)
        {
            var validate = Method("ValidateWallProfile");
            Assert.Throws<InvalidDataException>(() => Invoke(validate, rearDepth));
        }

        [Test]
        public void SceneSnapshotIncludesOnlyActiveWallGeometryAndOneFloor()
        {
            var capture = Method("CaptureActiveWalls");
            var root = Root();
            Plane(root, "Floor");
            var wall = Plane(root, "Wall");
            wall.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(90, 0, 0));
            var hidden = new GameObject("Inactive malformed wall");
            hidden.transform.SetParent(root, false);
            hidden.AddComponent<MeshFilter>();
            hidden.SetActive(false);
            var walls = (Stage1SceneWall[])Invoke(capture, root);
            Assert.That(walls.Select(value => value.name), Is.EqualTo(new[] { "Floor", "Wall" }));
            Assert.That(walls.Count(value => value.floor), Is.EqualTo(1));
            Assert.That(walls[1].position, Is.EqualTo(new double[] { 2, 3, 4 }));
            Assert.That(walls[1].local_to_world[3], Is.EqualTo(2));
            Assert.That(walls[1].local_to_world[7], Is.EqualTo(3));
            Assert.That(walls[1].local_to_world[11], Is.EqualTo(4));
            Assert.That(wall.GetComponent<MeshRenderer>().enabled, Is.True);
            Assert.That(wall.GetComponent<MeshRenderer>().forceRenderingOff, Is.False);
        }

        [TestCase("nonflat")]
        [TestCase("singular")]
        [TestCase("sheared")]
        [TestCase("missing-renderer")]
        public void UnsupportedWallGeometryCannotEnterTheScoringSnapshot(string error)
        {
            var capture = Method("CaptureActiveWalls");
            var root = Root();
            Plane(root, "Floor");
            var wall = Plane(root, "Wall");
            if (error == "nonflat") wall.sharedMesh.bounds = new Bounds(Vector3.zero, new Vector3(2, .02f, 2));
            if (error == "singular") wall.transform.localScale = new Vector3(1, 0, 1);
            if (error == "sheared")
            {
                root.localScale = new Vector3(2, 1, 1);
                wall.transform.localRotation = Quaternion.Euler(0, 0, 45);
            }
            if (error == "missing-renderer") UnityEngine.Object.DestroyImmediate(wall.GetComponent<MeshRenderer>());
            Assert.Throws<InvalidDataException>(() => Invoke(capture, root));
        }

        [Test]
        public void SceneSnapshotRejectsInactiveFloorAndMoreWallsThanThePreviewSupports()
        {
            var capture = Method("CaptureActiveWalls");
            var root = Root();
            var floor = Plane(root, "Floor");
            floor.gameObject.SetActive(false);
            Assert.Throws<InvalidDataException>(() => Invoke(capture, root));
            floor.gameObject.SetActive(true);
            for (var i = 0; i < 16; i++) Plane(root, "Wall " + i);
            Assert.That(((Stage1SceneWall[])Invoke(capture, root)).Length, Is.EqualTo(17));
            Plane(root, "Wall overflow");
            Assert.Throws<InvalidDataException>(() => Invoke(capture, root));
        }

        Transform Root()
        {
            var root = new GameObject("Stage 1 scene boundary test");
            owned.Add(root);
            return root.transform;
        }

        MeshFilter Plane(Transform parent, string name)
        {
            var owner = new GameObject(name);
            owner.transform.SetParent(parent, false);
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(-1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, -1) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            mesh.RecalculateBounds();
            owned.Add(mesh);
            var filter = owner.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            owner.AddComponent<MeshRenderer>();
            return filter;
        }

        string WriteReport(string json)
        {
            var path = Path.Combine(directory, "identity_report.json");
            File.WriteAllText(path, json);
            return path;
        }

        static MethodInfo Method(string name)
        {
            var method = typeof(Stage1Round1Service).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, name + " is not implemented");
            return method;
        }

        static object Invoke(MethodInfo method, params object[] arguments)
        {
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }
    }
}
