using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Gsplat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Oculus.Interaction.Input;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1PreviewRecoveryTests
    {
        const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string SceneHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        GameObject target, controls;
        GsplatAssetUncompressed asset;
        GsplatRenderer renderer;
        Stage1SelectionController controller;

        [SetUp]
        public void SetUp()
        {
            asset = NewAsset();
            target = new GameObject("Stage1 recovery renderer");
            target.SetActive(false);
            renderer = target.AddComponent<GsplatRenderer>();
            renderer.GsplatAsset = asset;
            renderer.AsyncUpload = false;
            target.SetActive(true);
            controls = new GameObject("Stage1 recovery controls");
            controller = controls.AddComponent<Stage1SelectionController>();
            controller.Bind(renderer, "", SourceHash, SceneHash);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(controls);
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(asset);
        }

        static GsplatAssetUncompressed NewAsset()
        {
            var value = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            value.SplatCount = 9;
            value.SHBands = 0;
            value.Bounds = new Bounds(new Vector3(0, 0, -5), Vector3.one * 20);
            value.Allocate();
            for (int i = 0; i < 9; i++)
            {
                value.Positions[i] = new Vector3(0, 0, -i - 1);
                value.Scales[i] = Vector3.one * 0.01f;
                value.Rotations[i] = new Vector4(1, 0, 0, 0);
                value.Colors[i] = new Vector4(0.2f, 0.3f, 0.4f, 0.5f);
            }
            return value;
        }

        static RankManifest Manifest(uint[] order)
        {
            var bytes = new byte[order.Length * 4];
            for (int i = 0; i < order.Length; i++)
            {
                bytes[i * 4] = (byte)order[i];
                bytes[i * 4 + 1] = (byte)(order[i] >> 8);
                bytes[i * 4 + 2] = (byte)(order[i] >> 16);
                bytes[i * 4 + 3] = (byte)(order[i] >> 24);
            }
            using var sha = SHA256.Create();
            return new RankManifest
            {
                schema_version = 1, source_count = 9, eligible_count = order.Length,
                source_hash = SourceHash, scene_hash = SceneHash, rank_id = "recovery-frozen-rank",
                rank_path = "rank_to_source.bin",
                rank_sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
            };
        }

        static bool EnsurePreview(Stage1SelectionController value)
        {
            var method = typeof(Stage1SelectionController).GetMethod("EnsurePreviewSelection");
            Assert.That(method, Is.Not.Null, "The preview recovery entry point is missing.");
            try { return (bool)method.Invoke(value, null); }
            catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
        }

        void EarlyUpdate() => controller.SendMessage("Update", SendMessageOptions.DontRequireReceiver);

        [Test]
        public void MetaRightButtonsSaveOnceAndCompareEvenWhenViewCaptureIsUnavailable()
        {
            controller.LoadRank(Manifest(new uint[] { 8, 2, 5, 1 }), new uint[] { 8, 2, 5, 1 });
            controller.State.SetCandidate(5000);
            var camera = controls.AddComponent<Camera>();
            var views = controls.AddComponent<Stage1SceneBindings>();
            views.Bind(camera, target.transform, controls.transform);
            var actions = controls.AddComponent<Stage1ReviewActions>();
            actions.Bind(controller, views);
            void Tick() => typeof(Stage1ReviewActions).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(actions, null);
            var directory = Path.Combine(Path.GetTempPath(), "stage1-input-" + Guid.NewGuid().ToString("N"));
            var source = new Stage1TestControllerSource();
            var right = controls.AddComponent<Controller>();
            right.InjectAllController(DataSource<ControllerDataAsset>.UpdateModeFlags.Manual, null, source, false);
            var serialized = new SerializedObject(actions);
            var input = serialized.FindProperty("_rightController");
            Assert.That(input, Is.Not.Null, "Review input must use the same Meta controller as the interaction rig.");
            input.objectReferenceValue = right;
            serialized.FindProperty("_sessionRoot").stringValue = directory;
            serialized.FindProperty("_pollRightControllerButtons").boolValue = true;
            serialized.FindProperty("_recordOrdinaryPoses").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            try
            {
                actions.StartSession();
                source.Data.Input.SetButton(ControllerButtonUsage.PrimaryButton, true);
                source.Data.Input.SetButton(ControllerButtonUsage.SecondaryButton, true);
                Tick();
                Tick();
                Assert.That(actions.BookmarkCount, Is.EqualTo(1), "A held button must save only once.");
                Assert.That(controller.State.IsOriginal, Is.True);
                renderer.Update();
                AssertGpuMembership(new uint[] { 1, 2, 5, 8 });
                source.Data.Input.Clear();
                Tick();
                views.Bind(null, target.transform, controls.transform);
                source.Data.Input.SetButton(ControllerButtonUsage.PrimaryButton, true);
                source.Data.Input.SetButton(ControllerButtonUsage.SecondaryButton, true);
                Tick();
                Assert.That(actions.IsSessionOpen, Is.True, "Waiting for an XR view must not shut down review input.");
                Assert.That(actions.BookmarkCount, Is.EqualTo(1));
                Assert.That(controller.State.IsOriginal, Is.False, "B must still work when A cannot capture a view.");
                renderer.Update();
                AssertGpuMembership(new uint[] { 2, 8 });
                var session = actions.SessionDirectory;
                actions.EndSession();
                var mark = JsonUtility.FromJson<MarkRecord>(File.ReadAllLines(Path.Combine(session, "marks.jsonl"))[0]);
                Assert.That(mark.display_mode, Is.EqualTo("candidate"), "Simultaneous A/B saves the view before comparison changes.");
            }
            finally
            {
                actions.EndSession();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReloadUsesSerializedPreviewPercentageAndComparisonMode(bool original)
        {
            var remember = typeof(Stage1SelectionController).GetMethod("RememberEditModePreview");
            Assert.That(remember, Is.Not.Null, "Edit Mode preview settings must survive rank reloads.");
            remember.Invoke(controller, new object[] { 3000, original });
            var serialized = JsonUtility.ToJson(controller);
            Assert.That(serialized, Does.Contain("3000"));
            controller.Bind(renderer, "", SourceHash, SceneHash);
            var order = new uint[] { 8, 2, 5, 1 };
            controller.LoadRank(Manifest(order), order);
            renderer.Update();
            Assert.That(controller.State.CandidateCentiPercent, Is.EqualTo(3000));
            Assert.That(controller.State.IsOriginal, Is.EqualTo(original));
            AssertGpuMembership(order.Take(original ? 4 : 1).OrderBy(id => id).ToArray());
        }

        [Test]
        public void ConfiguredButUnvalidatedPreviewDoesNotFallThroughToTheFullSource()
        {
            controller.Bind(renderer, "awaiting-validation.json", SourceHash, SceneHash);
            EarlyUpdate();
            renderer.Update();
            Assert.That(controller.State, Is.Null);
            Assert.That(renderer.HasStage1Selection, Is.True);
            Assert.That(renderer.RemainingCount, Is.Zero, "Do not show deleted rows while waiting for validation after a reload.");
        }

        [Test]
        public void CameraPreparationHoldsUnvalidatedSelectionBeforeMonoBehaviourUpdate()
        {
            controller.LoadRank(Manifest(new uint[] { 8, 2, 5, 1 }), new uint[] { 8, 2, 5, 1 });
            renderer.Update();
            controller.Bind(renderer, "awaiting-validation.json", SourceHash, SceneHash);
            Assert.That(EnsurePreview(controller), Is.False);
            Assert.That(renderer.Stage1EligibleCount, Is.Zero, "Camera preparation cannot leave a previous or all-source rank drawable before validation.");
        }

        [Test]
        public void ExactSurvivorCountPersistsOnlyForItsConfiguredRank()
        {
            var order = new uint[] { 8, 2, 5, 1, 7, 6, 0 };
            var manifest = Manifest(order);
            controller.LoadRank(manifest, order);
            var setCount = controller.State.GetType().GetMethod("SetCandidateCount");
            var remember = typeof(Stage1SelectionController).GetMethod("RememberEditModeKeepCount");
            Assert.That(setCount, Is.Not.Null);
            Assert.That(remember, Is.Not.Null);
            setCount.Invoke(controller.State, new object[] { 4 });
            remember.Invoke(controller, new object[] { 4, false });
            controller.Bind(renderer, "", SourceHash, SceneHash);
            controller.LoadRank(manifest, order);
            renderer.Update();
            AssertGpuMembership(new uint[] { 1, 2, 5, 8 }, 7);
            Assert.That(controller.State.CandidateCentiPercent, Is.EqualTo(5714));
            manifest.rank_id = "another-rank";
            controller.LoadRank(manifest, order);
            renderer.Update();
            Assert.That(renderer.RemainingCount, Is.EqualTo(3), "A saved exact count must not silently carry over to a different ranking.");
        }

        [Test]
        public void EditorPreviewCommitsTheLatestSelectionAtTheCameraBoundary()
        {
            var order = new uint[] { 8, 2, 5, 1 };
            controller.LoadRank(Manifest(order), order);
            renderer.Update();
            var window = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("SplatPreprocess.Editor.SplatPreprocessorWindow"))
                .First(type => type != null);
            foreach (var percent in new[] { 100f, 0f, 30f })
                window.GetMethod("SetEditModePreview").Invoke(null, new object[] { controller, percent, false });
            Assert.That(renderer.RemainingCount, Is.EqualTo(4), "OnGUI must not alter an already submitted draw.");
            var prepare = typeof(GsplatRenderer).GetMethod("PrepareEditorPreview");
            Assert.That(prepare, Is.Not.Null, "Editor cameras must prepare the latest requested selection without submitting a global native draw.");
            prepare.Invoke(renderer, null);
            AssertGpuMembership(new uint[] { 8 });
            window.GetMethod("SetEditModePreview").Invoke(null, new object[] { controller, 30f, true });
            prepare.Invoke(renderer, null);
            AssertGpuMembership(new uint[] { 1, 2, 5, 8 });
        }

        void AssertGpuMembership(uint[] expected, int eligibleCount = 4)
        {
            Assert.That(renderer.RemainingCount, Is.EqualTo((uint)expected.Length), "The first resumed draw must not use all source rows.");
            Assert.That(renderer.HasStage1Selection, Is.True);
            Assert.That(renderer.Stage1EligibleCount, Is.EqualTo(eligibleCount));
            Assert.That(renderer.Stage1SelectedCount, Is.EqualTo(expected.Length));
            using var commands = new CommandBuffer();
            renderer.ComputeDepth(commands, Matrix4x4.identity);
            Graphics.ExecuteCommandBuffer(commands);
            var actual = new uint[expected.Length];
            renderer.SorterResource.OrderBuffer.GetData(actual, 0, 0, actual.Length);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RendererReenableRestoresExactFrozenRankAndPreservesTheReviewSession(bool original)
        {
            var order = new uint[] { 8, 2, 5, 1 };
            var manifest = Manifest(order);
            controller.LoadRank(manifest, order);
            controller.State.SetCandidate(5000);
            if (original) controller.State.ToggleOriginal();
            controller.BeginSession();
            var state = controller.State;
            var expected = order.Take(original ? 4 : 2).OrderBy(id => id).ToArray();
            renderer.Update();
            AssertGpuMembership(expected);
            var initialSource = renderer.GsplatResource;

            Array.Reverse(order); // Recovery must own a copy of the validated original rank.
            renderer.enabled = false;
            EarlyUpdate();
            Assert.That(renderer.GsplatResource, Is.Null, "A worker-held preview must remain released.");
            Assert.Throws<InvalidOperationException>(() => controller.LoadRank(manifest, new uint[] { 8, 2, 5, 1 }));
            renderer.enabled = true;
            EarlyUpdate(); // Unity calls the negative-order controller before GsplatRenderer.Update.
            renderer.Update();
            AssertGpuMembership(expected);
            Assert.That(renderer.GsplatResource, Is.Not.SameAs(initialSource));
            Assert.That(controller.State, Is.SameAs(state));
            Assert.That(controller.IsSessionActive, Is.True);
            Assert.That(state.CandidateCentiPercent, Is.EqualTo(5000));
            Assert.That(state.IsOriginal, Is.EqualTo(original));

            var stableSource = renderer.GsplatResource;
            var stableOrder = renderer.SorterResource.OrderBuffer;
            Assert.That(EnsurePreview(controller), Is.True);
            Assert.That(renderer.GsplatResource, Is.SameAs(stableSource));
            Assert.That(renderer.SorterResource.OrderBuffer, Is.SameAs(stableOrder));

            renderer.enabled = false;
            Assert.DoesNotThrow(() => state.SetCandidate(2500));
            Assert.That(EnsurePreview(controller), Is.False);
            Assert.That(renderer.GsplatResource, Is.Null);
            renderer.enabled = true;
            Assert.That(EnsurePreview(controller), Is.True);
            renderer.Update();
            AssertGpuMembership(original ? new uint[] { 1, 2, 5, 8 } : new uint[] { 8 });
            Assert.That(controller.State, Is.SameAs(state));
            Assert.That(controller.IsSessionActive, Is.True);
        }

        [Test]
        public void RecoveryRejectsAChangedAssetEvenWhenItsSourceCountMatches()
        {
            controller.LoadRank(Manifest(new uint[] { 8, 2, 5, 1 }), new uint[] { 8, 2, 5, 1 });
            controller.BeginSession();
            var state = controller.State;
            var replacement = NewAsset();
            try
            {
                renderer.enabled = false;
                renderer.GsplatAsset = replacement;
                renderer.enabled = true;
                Assert.Throws<InvalidDataException>(() => EnsurePreview(controller));
                Assert.That(renderer.enabled, Is.False, "An unrelated source must not resume as an unranked preview.");
                Assert.That(renderer.GsplatResource, Is.Null);
                Assert.That(controller.State, Is.SameAs(state));
                Assert.That(controller.IsSessionActive, Is.True);
                Assert.Throws<InvalidOperationException>(() => controller.LoadRank(Manifest(new uint[] { 8, 2, 5, 1 }), new uint[] { 8, 2, 5, 1 }));
            }
            finally { UnityEngine.Object.DestroyImmediate(replacement); }
        }

        [Test]
        public void PreviewRecoveryAndWallBindingRunBeforeTheRendererSubmitsDraws()
        {
            var rendererOrder = typeof(GsplatRenderer).GetCustomAttribute<DefaultExecutionOrder>()?.order ?? 0;
            foreach (var type in new[] { typeof(Stage1SelectionController), typeof(Stage1AuthoredWalls) })
            {
                Assert.That(type.GetCustomAttribute<ExecuteAlways>(), Is.Not.Null, type.Name + " must also recover in Editor preview.");
                var order = type.GetCustomAttribute<DefaultExecutionOrder>();
                Assert.That(order, Is.Not.Null, type.Name + " needs an explicit early execution order.");
                Assert.That(order.order, Is.LessThan(rendererOrder), type.Name + " must restore its state before draw submission.");
            }
        }
    }
}
