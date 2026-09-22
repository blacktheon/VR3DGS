using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1ReviewTests
    {
        const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string SceneHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        // Resolve new contracts at runtime so RED is an assertion, not a broken Editor compilation.
        static Type Contract(string name)
        {
            var type = typeof(Stage1Counts).Assembly.GetType("SplatPreprocess." + name);
            Assert.That(type, Is.Not.Null, name + " is not implemented");
            return type;
        }

        static object NewState() => Activator.CreateInstance(Contract("Stage1ReviewState"), 100, 80, SourceHash, SceneHash, "round-one");
        static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethod(name).Invoke(value, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException; }
        }
        static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
        static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        static void Set(object value, string name, object data) => value.GetType().GetField(name).SetValue(value, data);
        static object Json(string name, string json) => JsonUtility.FromJson(json, Contract(name));

        static object Sample(double seconds = 1)
        {
            return Json("ViewSample", "{\"timestamp_seconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"head_position\":[1,2,3],\"head_rotation\":[0,0,0,1]," +
                "\"head_world_to_camera\":[1,0,0,-1,0,1,0,-2,0,0,-1,3,0,0,0,1]," +
                "\"head_projection\":[2,0,0,0,0,2,0,0,0,0,-1,-1,0,0,-1,0]," +
                "\"source_to_world\":[1,0,0,4,0,1,0,5,0,0,1,6,0,0,0,1]," +
                "\"tracking_origin_to_world\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]," +
                "\"tracking_origin_id\":\"rig\",\"views\":[{\"eye\":\"mono\",\"width\":640,\"height\":480," +
                "\"world_to_camera\":[1,0,0,-1,0,1,0,-2,0,0,-1,3,0,0,0,1]," +
                "\"projection\":[2,0,0,0,0,2,0,0,0,0,-1,-1,0,0,-1,0]}]}");
        }

        [Test]
        public void OriginalRemembersCandidateAndUsesEligibleDenominator()
        {
            var state = NewState();
            Call(state, "SetCandidate", 4000);
            Assert.That(Property(state, "CandidateCount"), Is.EqualTo(32));
            Call(state, "ToggleOriginal");
            Call(state, "SetCandidate", 2500);
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(80));
            Assert.That(Property(state, "CandidateCentiPercent"), Is.EqualTo(2500));
            Assert.That(Property(state, "SourceCount"), Is.EqualTo(100));
            Call(state, "ToggleOriginal");
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(20));
        }

        [Test]
        public void NormalizedSliderHasExactEndpointsAndRejectsNonfiniteValues()
        {
            var state = NewState();
            Call(state, "SetCandidateNormalized", 0f);
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(0));
            Call(state, "SetCandidateNormalized", 1f);
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(80));
            Call(state, "SetCandidateNormalized", 0.4f);
            Assert.That(Property(state, "CandidateCentiPercent"), Is.EqualTo(4000));
            Assert.Throws<ArgumentOutOfRangeException>(() => Call(state, "SetCandidateNormalized", float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => Call(state, "SetCandidate", 10001));
        }

        [Test]
        public void PostDeletionExactCountSurvivesComparisonAndSameSliderBucket()
        {
            var state = Activator.CreateInstance(Contract("Stage1ReviewState"), 6011316, 4837536, SourceHash, SceneHash, "after-cube3");
            Assert.That(state.GetType().GetMethod("SetCandidateCount"), Is.Not.Null, "Deletion survivors need an exact count independent of rounded slider display.");
            Call(state, "SetCandidateCount", 988866);
            Assert.That(Property(state, "CandidateCentiPercent"), Is.EqualTo(2044));
            Assert.That(Property(state, "CandidateCount"), Is.EqualTo(988866));
            Call(state, "SetCandidate", 2044);
            Call(state, "ToggleOriginal");
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(4837536));
            Call(state, "ToggleOriginal");
            Assert.That(Property(state, "DisplayedCount"), Is.EqualTo(988866));
            Call(state, "SetCandidate", 2000);
            Assert.That(Property(state, "CandidateCount"), Is.EqualTo(967507));
            Assert.Throws<ArgumentOutOfRangeException>(() => Call(state, "SetCandidateCount", 4837537));
        }

        [Test]
        public void ExactCandidateBookmarkRecordsTheCountAndRejectsInvalidBounds()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-exact-mark-" + Guid.NewGuid().ToString("N"));
            object recorder = null;
            try
            {
                var state = Activator.CreateInstance(Contract("Stage1ReviewState"), 6011316, 4837536, SourceHash, SceneHash, "after-cube3");
                Assert.That(state.GetType().GetMethod("SetCandidateCount"), Is.Not.Null);
                Call(state, "SetCandidateCount", 988866);
                recorder = Activator.CreateInstance(Contract("Stage1SessionRecorder"), directory, state);
                var mark = Call(state, "GetBookmark", Sample());
                Call(recorder, "AppendMark", mark);
                Set(mark, "candidate_count", 4837537);
                Assert.Throws<InvalidDataException>(() => Call(recorder, "AppendMark", mark));
                Call(recorder, "Stop");
                var saved = Json("MarkRecord", File.ReadAllLines(Path.Combine(directory, "marks.jsonl"))[0]);
                Assert.That(Field(saved, "candidate_count"), Is.EqualTo(988866));
                Assert.That(Field(saved, "exact_candidate_count"), Is.EqualTo(true));
            }
            finally { (recorder as IDisposable)?.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void OriginalBookmarkFreezesExactViewAndRememberedCandidate()
        {
            var state = NewState();
            Call(state, "SetCandidate", 2500);
            Call(state, "ToggleOriginal");
            var sample = Sample();
            var mark = Call(state, "GetBookmark", sample);
            ((float[])Field(sample, "head_projection"))[0] = 99;
            ((float[])Field(sample, "source_to_world"))[3] = 99;
            var eye = ((Array)Field(sample, "views")).GetValue(0);
            ((float[])Field(eye, "projection"))[0] = 99;
            Call(state, "SetCandidate", 7500);
            Assert.That(Field(mark, "candidate_centi_percent"), Is.EqualTo(2500));
            Assert.That(Field(mark, "display_mode"), Is.EqualTo("original"));
            Assert.That(Field(mark, "source_hash"), Is.EqualTo(SourceHash));
            Assert.That(Field(mark, "scene_hash"), Is.EqualTo(SceneHash));
            Assert.That(Field(mark, "rank_id"), Is.EqualTo("round-one"));
            var frozen = Field(mark, "view");
            Assert.That(((float[])Field(frozen, "head_projection"))[0], Is.EqualTo(2));
            Assert.That(((float[])Field(frozen, "source_to_world"))[3], Is.EqualTo(4));
            Assert.That(((float[])Field(((Array)Field(frozen, "views")).GetValue(0), "projection"))[0], Is.EqualTo(2));
        }

        [Test]
        public void HeldButtonsProduceOnlyOneActionPerPress()
        {
            var edges = Activator.CreateInstance(Contract("Stage1ButtonEdges"));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", false, false)), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", true, false)), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", true, false)), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", true, true)), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", false, false)), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Call(edges, "Sample", true, true)), Is.EqualTo(3));
        }

        [Test]
        public void PoseSamplingIsBoundedButRetainsProjectionAndModelChanges()
        {
            var filter = Activator.CreateInstance(Contract("Stage1PoseFilter"));
            Assert.That(Call(filter, "ShouldRecord", Sample(1)), Is.True);
            Assert.That(Call(filter, "ShouldRecord", Sample(1.2)), Is.False);
            var changed = Sample(1.3);
            ((float[])Field(changed, "head_projection"))[0] = 3;
            Assert.That(Call(filter, "ShouldRecord", changed), Is.True);
            Set(changed, "timestamp_seconds", 1.31);
            ((float[])Field(changed, "source_to_world"))[3] = 7;
            Assert.That(Call(filter, "ShouldRecord", changed), Is.False);
            Set(changed, "timestamp_seconds", 1.5);
            Assert.That(Call(filter, "ShouldRecord", changed), Is.True);
        }

        [Test]
        public void SessionWritesPoseOnlyLogAndSeparateExactMarks()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-review-test-" + Guid.NewGuid().ToString("N"));
            object recorder = null;
            try
            {
                var state = NewState();
                Call(state, "SetCandidate", 3333);
                Call(state, "ToggleOriginal");
                recorder = Activator.CreateInstance(Contract("Stage1SessionRecorder"), directory, state);
                Call(recorder, "AppendPose", Sample());
                Call(recorder, "AppendMark", Call(state, "GetBookmark", Sample()));
                Call(recorder, "Stop");
                var ordinary = File.ReadAllText(Path.Combine(directory, "session.jsonl"));
                Assert.That(ordinary, Does.Not.Contain("percent"));
                Assert.That(ordinary, Does.Contain("head_projection"));
                var marks = File.ReadAllLines(Path.Combine(directory, "marks.jsonl"));
                Assert.That(marks, Has.Length.EqualTo(1));
                var mark = Json("MarkRecord", marks[0]);
                Assert.That(Field(mark, "candidate_centi_percent"), Is.EqualTo(3333));
                Assert.That(Field(mark, "display_mode"), Is.EqualTo("original"));
                var metadata = File.ReadAllText(Path.Combine(directory, "metadata.json"));
                Assert.That(metadata, Does.Contain(SourceHash));
                Assert.That(metadata, Does.Contain("round-one"));
                Assert.That(metadata, Does.Not.Contain("percent"));
            }
            finally
            {
                (recorder as IDisposable)?.Dispose();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void RankLoaderValidatesHashUniqueOriginalIdsAndSceneIdentity()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-rank-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "rank_manifest.json");
                WriteRank(directory, new uint[] { 3, 0, 4 });
                var loader = Contract("Stage1RankLoader").GetMethod("Load");
                var result = loader.Invoke(null, new object[] { path, SourceHash, SceneHash });
                CollectionAssert.AreEqual(new uint[] { 3, 0, 4 }, (uint[])Property(result, "Order"));
                Assert.That(Property(Property(result, "Manifest"), "EligibleCount"), Is.EqualTo(3));
                var mismatch = Assert.Throws<TargetInvocationException>(() => loader.Invoke(null, new object[] { path, SourceHash, SourceHash }));
                Assert.That(mismatch.InnerException, Is.TypeOf<InvalidDataException>());
                WriteRank(directory, new uint[] { 3, 3, 4 });
                var duplicate = Assert.Throws<TargetInvocationException>(() => loader.Invoke(null, new object[] { path, SourceHash, SceneHash }));
                Assert.That(duplicate.InnerException, Is.TypeOf<InvalidDataException>());
                WriteRank(directory, new uint[] { 3, 0, 5 });
                var outOfRange = Assert.Throws<TargetInvocationException>(() => loader.Invoke(null, new object[] { path, SourceHash, SceneHash }));
                Assert.That(outOfRange.InnerException, Is.TypeOf<InvalidDataException>());
                WriteRank(directory, new uint[] { 3, 0, 4 });
                File.WriteAllBytes(Path.Combine(directory, "rank.bin"), new byte[12]);
                var wrongHash = Assert.Throws<TargetInvocationException>(() => loader.Invoke(null, new object[] { path, SourceHash, SceneHash }));
                Assert.That(wrongHash.InnerException, Is.TypeOf<InvalidDataException>());
            }
            finally { Directory.Delete(directory, true); }
        }

        static void WriteRank(string directory, uint[] ids)
        {
            var rankPath = Path.Combine(directory, "rank.bin");
            using (var writer = new BinaryWriter(File.Create(rankPath))) foreach (var id in ids) writer.Write(id);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(rankPath))).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(Path.Combine(directory, "rank_manifest.json"),
                "{\"schema_version\":1,\"source_hash\":\"" + SourceHash + "\",\"scene_hash\":\"" + SceneHash +
                "\",\"rank_id\":\"round-one\",\"source_count\":5,\"eligible_count\":3,\"rank_sha256\":\"" + hash +
                "\",\"rank_path\":\"rank.bin\",\"importance_path\":\"importance.bin\"}");
        }

        [Test]
        public void InMemoryRankCannotClaimAnotherOrdersFrozenIdentity()
        {
            var directory = Path.Combine(Path.GetTempPath(), "stage1-rank-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                WriteRank(directory, new uint[] { 3, 0, 4 });
                var manifest = Json("RankManifest", File.ReadAllText(Path.Combine(directory, "rank_manifest.json")));
                var validation = Contract("Stage1RankLoader").GetMethod("ValidateOrder");
                var tampered = Assert.Throws<TargetInvocationException>(() => validation.Invoke(null, new object[] { manifest, new uint[] { 0, 3, 4 } }));
                Assert.That(tampered.InnerException, Is.TypeOf<InvalidDataException>());
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void CameraBindingCapturesActualMonoMatricesAndSourceTransform()
        {
            var cameraObject = new GameObject("Stage1 test camera");
            var sourceObject = new GameObject("Stage1 test source");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.SetPositionAndRotation(new Vector3(1, 2, 3), Quaternion.Euler(10, 20, 0));
                camera.projectionMatrix = Matrix4x4.Perspective(70, 1.5f, 0.1f, 100);
                sourceObject.transform.position = new Vector3(4, 5, 6);
                var bindings = cameraObject.AddComponent(Contract("Stage1SceneBindings"));
                Call(bindings, "Bind", camera, sourceObject.transform, cameraObject.transform);
                var capture = new object[] { null };
                Assert.That(bindings.GetType().GetMethod("TryCaptureViews").Invoke(bindings, capture), Is.True);
                var sample = capture[0];
                var view = (float[])Field(sample, "head_world_to_camera");
                var projection = (float[])Field(sample, "head_projection");
                for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++)
                {
                    Assert.That(view[row * 4 + col], Is.EqualTo(camera.worldToCameraMatrix[row, col]));
                    Assert.That(projection[row * 4 + col], Is.EqualTo(camera.projectionMatrix[row, col]));
                }
                Assert.That(((float[])Field(sample, "source_to_world"))[3], Is.EqualTo(4));
                Assert.That(((Array)Field(sample, "views")).Length, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(sourceObject);
            }
        }

        [Test]
        public void CameraBindingWaitsForAUsableProjectionAndRecovers()
        {
            var go = new GameObject("Stage1 projection readiness");
            try
            {
                var camera = go.AddComponent<Camera>();
                var bindings = go.AddComponent<Stage1SceneBindings>();
                bindings.Bind(camera, go.transform, go.transform);
                camera.projectionMatrix = Matrix4x4.zero;
                Assert.That(bindings.TryCaptureViews(out var invalid), Is.False,
                    "Uninitialized XR projection must not be recorded as a valid view.");
                Assert.That(invalid, Is.Null);
                camera.ResetProjectionMatrix();
                Assert.That(bindings.TryCaptureViews(out var recovered), Is.True);
                Assert.That(recovered.views, Has.Length.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
