using System;
using System.Linq;
using System.Reflection;
using Gsplat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1GpuSelectionTests
    {
        const string SelectionShaderPath = "Packages/wu.yize.gsplat/Runtime/Shaders/Resources/Stage1Selection.compute";

        // Reflection keeps the RED checkpoint compilable against the pinned, unmodified renderer.
        static object CreateSelection(int sourceCount)
        {
            var type = typeof(GsplatRenderer).Assembly.GetType("Gsplat.Stage1GpuSelection");
            Assert.That(type, Is.Not.Null, "The renderer has no early Stage 1 GPU selection path yet.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SelectionShaderPath);
            Assert.That(shader, Is.Not.Null, "Stage 1 selection compute shader is missing.");
            Assert.That(SystemInfo.supportsComputeShaders, Is.True, "A real compute-capable GPU is required.");
            return Activator.CreateInstance(type, (uint)sourceCount, shader);
        }

        static object Call(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Missing {target.GetType().Name}.{name}");
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }

        static T Read<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(name);
            Assert.That(property, Is.Not.Null, $"Missing {target.GetType().Name}.{name}");
            return (T)property.GetValue(target);
        }

        static object Wall(Matrix4x4 worldToLocal, Vector4 plane)
        {
            var type = typeof(GsplatRenderer).Assembly.GetType("Gsplat.Stage1Wall");
            Assert.That(type, Is.Not.Null, "Finite directional Stage 1 walls are not implemented yet.");
            var wall = Activator.CreateInstance(type);
            type.GetField("WorldToLocal").SetValue(wall, worldToLocal);
            type.GetField("BoundsMinMaxXZ").SetValue(wall, new Vector4(-1, 1, -1, 1));
            type.GetField("WorldPlane").SetValue(wall, plane);
            type.GetField("RearDepth").SetValue(wall, 0.1f);
            return wall;
        }

        static uint[] Rank(int sourceCount, int eligibleCount)
        {
            // Coprime stride deliberately makes rank order differ from storage order.
            return Enumerable.Range(0, eligibleCount).Select(i => (uint)((i * 37 + 11) % sourceCount)).ToArray();
        }

        static uint[] DispatchAndRead(object selection, int count)
        {
            using var cmd = new CommandBuffer();
            Call(selection, "RecordSelection", cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            var result = new uint[count];
            if (count > 0) Read<GraphicsBuffer>(selection, "SelectedIds").GetData(result, 0, 0, count);
            return result;
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(1023)]
        [TestCase(1024)]
        [TestCase(1025)]
        [TestCase(4097)]
        public void GpuRankPrefixIsExactAndSortedByStorageId(int keepCount)
        {
            var selection = CreateSelection(5003);
            try
            {
                var rank = Rank(5003, 4097);
                Call(selection, "SetRank", rank);
                Call(selection, "SetKeepCount", keepCount);
                var expected = rank.Take(keepCount).OrderBy(id => id).ToArray();
                Assert.That(DispatchAndRead(selection, keepCount), Is.EqualTo(expected));
                Assert.That(Read<int>(selection, "SelectedCount"), Is.EqualTo(keepCount));
                Assert.That(Read<int>(selection, "EligibleCount"), Is.EqualTo(4097));
            }
            finally { ((IDisposable)selection).Dispose(); }
        }

        [Test]
        public void RapidDecreaseIncreaseKeepsNestedMembershipAndCopiesTheRank()
        {
            var selection = CreateSelection(5003);
            try
            {
                var rank = Rank(5003, 4097);
                var frozen = rank.ToArray();
                Call(selection, "SetRank", rank);
                Array.Reverse(rank); // Caller mutations must not silently change the frozen ranking.
                var selectedBuffer = Read<GraphicsBuffer>(selection, "SelectedIds");
                foreach (int count in new[] { 4097, 7, 1025, 0, 1, 4097, 1024, 1023 })
                {
                    Call(selection, "SetKeepCount", count);
                    Assert.That(DispatchAndRead(selection, count),
                        Is.EqualTo(frozen.Take(count).OrderBy(id => id).ToArray()));
                    Assert.That(Read<GraphicsBuffer>(selection, "SelectedIds"), Is.SameAs(selectedBuffer));
                }
            }
            finally { ((IDisposable)selection).Dispose(); }
        }

        [Test]
        public void CompactionCarriesOffsetsAcrossTheHierarchicalScanBoundary()
        {
            const int sourceCount = 65543;
            var selection = CreateSelection(sourceCount);
            try
            {
                var rank = Rank(sourceCount, 4097);
                Call(selection, "SetRank", rank);
                Assert.That(DispatchAndRead(selection, rank.Length), Is.EqualTo(rank.OrderBy(id => id).ToArray()));
            }
            finally { ((IDisposable)selection).Dispose(); }
        }

        [Test]
        public void InvalidRanksAndKeepCountsCannotReplaceAnAcceptedSelection()
        {
            var selection = CreateSelection(9);
            try
            {
                Call(selection, "SetRank", new uint[] { 8, 2, 5 });
                Call(selection, "SetKeepCount", 2);
                Assert.Throws<ArgumentException>(() => Call(selection, "SetRank", new uint[] { 1, 1 }));
                Assert.Throws<ArgumentException>(() => Call(selection, "SetRank", new uint[] { 9 }));
                Assert.Throws<ArgumentNullException>(() => Call(selection, "SetRank", new object[] { null }));
                Assert.Throws<ArgumentOutOfRangeException>(() => Call(selection, "SetKeepCount", -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => Call(selection, "SetKeepCount", 4));
                Assert.That(DispatchAndRead(selection, 2), Is.EqualTo(new uint[] { 2, 8 }));
                Call(selection, "SetRank", Array.Empty<uint>());
                Assert.That(Read<int>(selection, "EligibleCount"), Is.Zero);
                Assert.That(Read<int>(selection, "SelectedCount"), Is.Zero);
                Assert.That(DispatchAndRead(selection, 0), Is.Empty);
            }
            finally { ((IDisposable)selection).Dispose(); }
        }

        [Test]
        public void EveryCameraReseedsTiedDepthSortAndLeavesTheSelectionImmutable()
        {
            const int sourceCount = 5003;
            const int count = 4097;
            var selection = CreateSelection(sourceCount);
            var sortShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/wu.yize.gsplat/Runtime/Shaders/Gsplat.compute");
            var pass = new GsplatSortPass(sortShader);
            Assert.That(pass.Valid, Is.True);
            using var order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sourceCount, sizeof(uint));
            using var keys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sourceCount, sizeof(float));
            var resources = GsplatSortPass.SupportResources.Load(sourceCount);
            try
            {
                var rank = Rank(sourceCount, count);
                var expected = rank.OrderBy(id => id).ToArray();
                Call(selection, "SetRank", rank);
                for (int camera = 0; camera < 3; camera++)
                {
                    // Simulates previous-camera sort output; equal depths must ignore that history.
                    order.SetData(expected.Reverse().ToArray());
                    keys.SetData(Enumerable.Repeat(-2f, sourceCount).ToArray());
                    using var cmd = new CommandBuffer();
                    Call(selection, "SeedOrder", cmd, order);
                    pass.Dispatch(cmd, new GsplatSortPass.Args
                    {
                        Count = count, InputKeys = keys, InputValues = order, Resources = resources
                    });
                    Graphics.ExecuteCommandBuffer(cmd);
                    var actual = new uint[count];
                    order.GetData(actual, 0, 0, count);
                    Assert.That(actual, Is.EqualTo(expected));
                    Assert.That(DispatchAndRead(selection, count), Is.EqualTo(expected));
                }
            }
            finally { resources.Dispose(); ((IDisposable)selection).Dispose(); }
        }

        [Test]
        public void RendererCountSurvivesUploadBookkeepingAndDepthOnlyTouchesSelectedSlots()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 9;
            asset.SHBands = 0;
            asset.Bounds = new Bounds(Vector3.zero, Vector3.one * 10);
            asset.Allocate();
            for (int i = 0; i < 9; i++) asset.Positions[i] = new Vector3(0, 0, -i - 1);
            var renderer = new GsplatRendererImpl(9);
            try
            {
                renderer.BindGsplatAsset(asset);
                var sourceResource = renderer.GsplatResource;
                var positions = ((GsplatResourceUncompressed)sourceResource).PositionBuffer;
                sourceResource.UploadedCount = 8;
                Assert.Throws<InvalidOperationException>(() => Call(renderer, "SetStage1Rank", new uint[] { 8, 2, 5 }));
                sourceResource.UploadedCount = 9;
                Call(renderer, "SetStage1Rank", new uint[] { 8, 2, 5 });
                foreach (int count in new[] { 1, 0, 3, 2 })
                {
                    Call(renderer, "SetStage1KeepCount", count);
                    renderer.DispatchInitOrder(Array.Empty<GsplatCutout>(), Matrix4x4.identity, true);
                    Assert.That(renderer.m_remainingCount, Is.EqualTo((uint)count));
                    Assert.That(renderer.GsplatResource, Is.SameAs(sourceResource));
                    Assert.That(((GsplatResourceUncompressed)renderer.GsplatResource).PositionBuffer, Is.SameAs(positions));
                    Assert.That(renderer.GsplatResource.UploadedCount, Is.EqualTo(9));
                    renderer.SorterResource.InputKeys.SetData(Enumerable.Repeat(123f, 9).ToArray());
                    using var cmd = new CommandBuffer();
                    renderer.ComputeDepth(cmd, Matrix4x4.identity);
                    Graphics.ExecuteCommandBuffer(cmd);
                    var depths = new float[9];
                    renderer.SorterResource.InputKeys.GetData(depths);
                    var ids = new uint[] { 8, 2, 5 }.Take(count).OrderBy(id => id).ToArray();
                    for (int i = 0; i < count; i++) Assert.That(depths[i], Is.EqualTo(-ids[i] - 1f));
                    for (int i = count; i < 9; i++) Assert.That(depths[i], Is.EqualTo(123f), "Depth dispatched beyond k.");
                }
                Call(renderer, "ClearStage1Selection");
                renderer.DispatchInitOrder(Array.Empty<GsplatCutout>(), Matrix4x4.identity, true);
                Assert.That(renderer.m_remainingCount, Is.EqualTo(9));
            }
            finally { renderer.Dispose(); UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void ReplacingAndClearingRankKeepAlreadySubmittedDrawsConsistent()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 9;
            asset.SHBands = 0;
            asset.Allocate();
            for (int i = 0; i < 9; i++) asset.Positions[i] = new Vector3(0, 0, -i - 1);
            var renderer = new GsplatRendererImpl(9);
            try
            {
                renderer.BindGsplatAsset(asset);
                Call(renderer, "SetStage1Rank", new uint[] { 8, 2, 5 });
                Call(renderer, "SetStage1KeepCount", 2);
                renderer.DispatchInitOrder(Array.Empty<GsplatCutout>(), Matrix4x4.identity, false);
                Assert.That(ReadDepthPrefix(renderer, 2), Is.EqualTo(new[] { -3f, -9f }));

                // A new ranking may arrive after Update submitted a draw for the old two IDs.
                Call(renderer, "SetStage1Rank", new uint[] { 1, 4 });
                Call(renderer, "SetStage1KeepCount", 1);
                Assert.That(ReadDepthPrefix(renderer, 2), Is.EqualTo(new[] { -3f, -9f }));
                renderer.DispatchInitOrder(Array.Empty<GsplatCutout>(), Matrix4x4.identity, false);
                Assert.That(ReadDepthPrefix(renderer, 1), Is.EqualTo(new[] { -2f }));

                Call(renderer, "ClearStage1Selection");
                renderer.SorterResource.OrderBuffer.SetData(new uint[] { 8 });
                Assert.That(ReadDepthPrefix(renderer, 1), Is.EqualTo(new[] { -2f }), "A queued clear must still reseed the current draw.");
                renderer.DispatchInitOrder(Array.Empty<GsplatCutout>(), Matrix4x4.identity, false);
                Assert.That(renderer.m_remainingCount, Is.EqualTo(9));
                Assert.That(renderer.SorterResource.Initialized, Is.False, "The next ordinary sort must restore all source IDs.");
            }
            finally { renderer.Dispose(); UnityEngine.Object.DestroyImmediate(asset); }
        }

        static float[] ReadDepthPrefix(GsplatRendererImpl renderer, int count)
        {
            using var cmd = new CommandBuffer();
            renderer.ComputeDepth(cmd, Matrix4x4.identity);
            Graphics.ExecuteCommandBuffer(cmd);
            var depths = new float[count];
            renderer.SorterResource.InputKeys.GetData(depths, 0, 0, count);
            return depths;
        }

        [TestCase(1f, -0.05f, true)]
        [TestCase(1f, -0.1f, true)]
        [TestCase(1f, -0.1001f, false)]
        [TestCase(1f, 0f, false)]
        [TestCase(1f, 0.05f, false)]
        [TestCase(0f, -0.05f, false)]
        [TestCase(-1f, -0.05f, false)]
        public void WallBlocksOnlyItsFrontViewAndFiniteRearBand(float eyeY, float pointY, bool blocked)
        {
            var wall = Wall(Matrix4x4.identity, new Vector4(0, 1, 0, 0));
            Assert.That(Call(wall, "BlocksView", new Vector3(0, eyeY, 0), new Vector3(0, pointY, 0)), Is.EqualTo(blocked));
        }

        [Test]
        public void WallUsesTheRayIntersectionAndIncludesRectangleEdges()
        {
            var wall = Wall(Matrix4x4.identity, new Vector4(0, 1, 0, 0));
            Assert.That(Call(wall, "BlocksView", new Vector3(0, 1, 0), new Vector3(2, -0.05f, 0)), Is.False);
            Assert.That(Call(wall, "BlocksView", new Vector3(1, 1, 0), new Vector3(1, -0.05f, 0)), Is.True);
            Assert.That(Call(wall, "BlocksView", new Vector3(0, 1, 1.01f), new Vector3(0, -0.05f, 1.01f)), Is.False);
        }

        [Test]
        public void WallRearDepthStaysInWorldUnitsUnderNonuniformTransform()
        {
            var localToWorld = Matrix4x4.TRS(new Vector3(2, 0, 0), Quaternion.Euler(0, 0, -90), new Vector3(2, 3, 4));
            var wall = Wall(localToWorld.inverse, new Vector4(1, 0, 0, -2));
            Assert.That(Call(wall, "BlocksView", new Vector3(3, 0, 0), new Vector3(1.95f, 0, 0)), Is.True);
            Assert.That(Call(wall, "BlocksView", new Vector3(3, 0, 0), new Vector3(1.89f, 0, 0)), Is.False);
        }
    }
}
