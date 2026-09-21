using System;
using System.IO;
using System.Reflection;
using Gsplat;
using NUnit.Framework;
using SplatPreprocess.Editor;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1PreviewLeaseTests
    {
        static Stage1SceneSnapshot Snapshot()
        {
            var identity = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            identity[3] = BitConverter.ToSingle(BitConverter.GetBytes(int.MinValue), 0); // A real Unity -0 matrix coefficient.
            return new Stage1SceneSnapshot
            {
                source_hash = "source", source_path = "source.ply", scene_path = "scene.unity",
                model_local_to_world = identity,
                nav_vertices = new[]
                {
                    new Stage1SceneVertex { p = new double[] { .125f, 0, 0 } },
                    new Stage1SceneVertex { p = new double[] { 1, 0, 0 } },
                    new Stage1SceneVertex { p = new double[] { 0, 0, 1 } }
                },
                nav_indices = new[] { 0, 1, 2 }, nav_areas = new[] { 0 },
                walls = new[] { new Stage1SceneWall
                {
                    name = "Floor", floor = true, world_to_local = identity, local_to_world = identity,
                    normal = new double[] { 0, 1, 0 }, position = new double[] { 0, .155f, 0 },
                    bounds_min = new double[] { -1, 0, -1 }, bounds_max = new double[] { 1, 0, 1 }
                } },
                head_position = new double[] { 0, 0, 0 },
                head_projection = new double[] { .52650386f, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1.0001999f, -.20002f, 0, 0, -1, 0 },
                head_fov = 90, head_near = .1f, head_far = 1000
            };
        }

        [Test]
        public void SnapshotRoundTripPreservesNontrivialUnityFloatMatrices()
        {
            var expected = Snapshot();
            Assert.That(expected.head_projection[10], Is.EqualTo(-1.0001999139785767));
            Assert.DoesNotThrow(() => Stage1Round1Service.ValidateSnapshotJson(expected, JsonUtility.ToJson(expected)));
        }

        [Test]
        public void SnapshotRoundTripRejectsOneFloat32StepOfGeometryChange()
        {
            var expected = Snapshot();
            var changed = Snapshot();
            var bits = BitConverter.ToInt32(BitConverter.GetBytes(.125f), 0);
            changed.nav_vertices[0].p[0] = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            var exception = Assert.Throws<InvalidDataException>(() => Stage1Round1Service.ValidateSnapshotJson(expected, JsonUtility.ToJson(changed)));
            Assert.That(exception.Message, Does.Contain("nav_vertices[0].p[0]"));
        }

        static object Store(string directory, GsplatRenderer renderer)
        {
            var type = typeof(Editor.WorkerJobStore).Assembly.GetType("SplatPreprocess.Editor.Stage1PreviewLeaseStore");
            Assert.That(type, Is.Not.Null, "Stage1PreviewLeaseStore is not implemented");
            return Activator.CreateInstance(type, directory, new Func<string, GsplatRenderer>(id => id == "renderer-one" ? renderer : null));
        }

        static object Call(object value, string method, params object[] arguments)
        {
            try { return value.GetType().GetMethod(method).Invoke(value, arguments); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TerminalJobRestoresTheExactPreviousEnabledStateAfterReload(bool wasEnabled)
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-preview-lease-test-" + Guid.NewGuid().ToString("N"));
            var owner = new GameObject("Stage1 lease test renderer");
            try
            {
                var renderer = owner.AddComponent<GsplatRenderer>();
                renderer.enabled = wasEnabled;
                var first = Store(directory, renderer);
                Call(first, "Acquire", "renderer-one", renderer);
                Assert.That(renderer.enabled, Is.False);
                Call(first, "AttachJob", "job-one");
                var afterReload = Store(directory, renderer);
                Assert.That(Call(afterReload, "TryRestore", new Func<string, bool>(id => false)), Is.True);
                Assert.That(renderer.enabled, Is.EqualTo(wasEnabled));
                Assert.That(File.Exists(Path.Combine(directory, "preview-lease.json")), Is.False);
                Assert.That(Call(afterReload, "TryRestore", new Func<string, bool>(id => false)), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ActiveJobRetainsItsLeaseAndRejectsDuplicateAcquisition()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-preview-lease-test-" + Guid.NewGuid().ToString("N"));
            var owner = new GameObject("Stage1 lease test renderer");
            try
            {
                var renderer = owner.AddComponent<GsplatRenderer>();
                var lease = Store(directory, renderer);
                Call(lease, "Acquire", "renderer-one", renderer);
                Call(lease, "AttachJob", "job-one");
                Assert.Throws<InvalidOperationException>(() => Call(Store(directory, renderer), "Acquire", "renderer-one", renderer));
                Assert.That(Call(lease, "TryRestore", new Func<string, bool>(id => id == "job-one")), Is.False);
                Assert.That(renderer.enabled, Is.False);
                Assert.That(File.Exists(Path.Combine(directory, "preview-lease.json")), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void FailedLaunchWithNoJobRestoresPreviewAndMissingRendererKeepsRecoveryData()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-preview-lease-test-" + Guid.NewGuid().ToString("N"));
            var owner = new GameObject("Stage1 lease test renderer");
            try
            {
                var renderer = owner.AddComponent<GsplatRenderer>();
                var lease = Store(directory, renderer);
                Call(lease, "Acquire", "renderer-one", renderer);
                Assert.That(Call(Store(directory, null), "TryRestore", new Func<string, bool>(id => false)), Is.False);
                Assert.That(File.Exists(Path.Combine(directory, "preview-lease.json")), Is.True);
                Assert.That(Call(lease, "TryRestore", new Func<string, bool>(id => false)), Is.True);
                Assert.That(renderer.enabled, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
