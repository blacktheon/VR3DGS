using System;
using System.Reflection;
using NUnit.Framework;
using SplatPreprocess.Editor;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1SurfaceBakerTests
    {
        Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            mesh = new Mesh { name = "Mirrored UV surface fixture" };
            mesh.vertices = new[]
            {
                new Vector3(-2, 0, -1), new Vector3(-2, 0, 1),
                new Vector3(2, 0, 1), new Vector3(2, 0, -1)
            };
            mesh.uv = new[] { new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
        }

        [TearDown]
        public void TearDown() { if (mesh) UnityEngine.Object.DestroyImmediate(mesh); }

        static object Build(Mesh value, Matrix4x4 surface, Bounds source, Matrix4x4 sourceTransform, int dimension = 8192)
        {
            var type = typeof(Stage1Round1Validation).Assembly.GetType("SplatPreprocess.Editor.Stage1SurfaceBaker");
            Assert.That(type, Is.Not.Null, "The orthographic surface baker is missing.");
            var method = type.GetMethod("BuildFrame", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "The surface camera geometry API is missing.");
            try { return method.Invoke(null, new object[] { value, surface, source, sourceTransform, dimension }); }
            catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
        }

        static T Read<T>(object value, string field) => (T)value.GetType().GetField(field).GetValue(value);

        static void AssertUv(object frame, Vector3 world, Vector2 expected)
        {
            var offset = world - Read<Vector3>(frame, "Center");
            float u = .5f + Vector3.Dot(offset, Read<Vector3>(frame, "Right")) / Read<float>(frame, "WorldWidth");
            float v = .5f + Vector3.Dot(offset, Read<Vector3>(frame, "Up")) / Read<float>(frame, "WorldHeight");
            if (Read<bool>(frame, "FlipX")) u = 1 - u;
            Assert.That(u, Is.EqualTo(expected.x).Within(2e-5), "Captured horizontal coordinate must address the existing mesh U.");
            Assert.That(v, Is.EqualTo(expected.y).Within(2e-5), "Captured vertical coordinate must address the existing mesh V.");
        }

        [Test]
        public void RotatedMirroredUvPlaneMapsEveryCornerWithoutChangingItsFront()
        {
            var transform = Matrix4x4.TRS(new Vector3(3, 4, 5), Quaternion.Euler(0, 0, 90), new Vector3(2, 1, 3));
            var frame = Build(mesh, transform, new Bounds(Vector3.zero, Vector3.one * 20), Matrix4x4.identity);
            Assert.That(Vector3.Distance(Read<Vector3>(frame, "Forward"), Vector3.right), Is.LessThan(1e-5));
            Assert.That(Vector3.Distance(Read<Vector3>(frame, "Up"), Vector3.forward), Is.LessThan(1e-5));
            Assert.That(Read<bool>(frame, "FlipX"), Is.True);
            for (int i = 0; i < mesh.vertexCount; i++) AssertUv(frame, transform.MultiplyPoint3x4(mesh.vertices[i]), mesh.uv[i]);
            Assert.That(Vector3.Dot(Read<Vector3>(frame, "Eye") - Read<Vector3>(frame, "Center"), Vector3.left), Is.GreaterThan(0));
        }

        [Test]
        public void BuiltInUnityPlaneUsesItsActualUvOrientation()
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            try
            {
                var builtin = plane.GetComponent<MeshFilter>().sharedMesh;
                var frame = Build(builtin, Matrix4x4.identity, new Bounds(Vector3.zero, Vector3.one * 10), Matrix4x4.identity);
                for (int i = 0; i < builtin.vertexCount; i++) AssertUv(frame, builtin.vertices[i], builtin.uv[i]);
                Assert.That(Vector3.Distance(Read<Vector3>(frame, "Forward"), Vector3.down), Is.LessThan(1e-5));
            }
            finally { UnityEngine.Object.DestroyImmediate(plane); }
        }

        [Test]
        public void ResolutionTracksPhysicalAspectWithEightThousandPixelLongEdge()
        {
            var frame = Build(mesh, Matrix4x4.Scale(new Vector3(2, 1, 3)), new Bounds(Vector3.zero, Vector3.one), Matrix4x4.identity);
            Assert.That(Read<int>(frame, "Width"), Is.EqualTo(8192));
            Assert.That(Read<int>(frame, "Height"), Is.EqualTo(6144));
            Assert.That(Read<float>(frame, "OrthoSize"), Is.EqualTo(3).Within(1e-6));
            Assert.That(Read<float>(frame, "Aspect"), Is.EqualTo(4f / 3).Within(1e-6));
            var tall = Build(mesh, Matrix4x4.Scale(new Vector3(1, 1, 4)), new Bounds(Vector3.zero, Vector3.one), Matrix4x4.identity, 1024);
            Assert.That(Read<int>(tall, "Width"), Is.EqualTo(512));
            Assert.That(Read<int>(tall, "Height"), Is.EqualTo(1024));
        }

        [Test]
        public void CameraDepthContainsEveryTransformedSourceBoundCorner()
        {
            var source = new Bounds(new Vector3(2, 3, 4), new Vector3(20, 30, 40));
            var sourceTransform = Matrix4x4.TRS(new Vector3(-6, 8, 9), Quaternion.Euler(20, 40, 60), new Vector3(2, 3, 4));
            var frame = Build(mesh, Matrix4x4.identity, source, sourceTransform);
            for (int i = 0; i < 8; i++)
            {
                var p = source.center + Vector3.Scale(source.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                float depth = Vector3.Dot(sourceTransform.MultiplyPoint3x4(p) - Read<Vector3>(frame, "Eye"), Read<Vector3>(frame, "Forward"));
                Assert.That(depth, Is.GreaterThan(Read<float>(frame, "Near")));
                Assert.That(depth, Is.LessThan(Read<float>(frame, "Far")));
            }
            Assert.That(Read<float>(frame, "Near"), Is.GreaterThan(0));
        }

        [Test]
        public void NonAffineUvLayoutFailsInsteadOfSilentlyDistortingTheScreenshot()
        {
            mesh.uv = new[] { new Vector2(1, 0), new Vector2(1, 1), new Vector2(.2f, 1), new Vector2(0, 0) };
            Assert.Throws<InvalidOperationException>(() => Build(mesh, Matrix4x4.identity, new Bounds(Vector3.zero, Vector3.one), Matrix4x4.identity));
        }

        [Test]
        public void ShearedSurfaceFailsBecauseOneOrthographicCameraCannotMatchBothAxes()
        {
            var sheared = Matrix4x4.identity;
            sheared[0, 2] = .4f;
            Assert.Throws<InvalidOperationException>(() => Build(mesh, sheared, new Bounds(Vector3.zero, Vector3.one), Matrix4x4.identity));
        }
    }
}
