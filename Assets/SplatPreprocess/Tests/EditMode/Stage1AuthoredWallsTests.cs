using System;
using System.Collections.Generic;
using System.Reflection;
using Gsplat;
using NUnit.Framework;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1AuthoredWallsTests
    {
        readonly List<UnityEngine.Object> owned = new();
        Transform root;
        GsplatRenderer renderer;
        Behaviour binder;
        Mesh plane;

        [SetUp]
        public void SetUp()
        {
            root = NewObject("Stage1 walls test root").transform;
            var target = NewObject("Stage1 disabled test renderer");
            target.SetActive(false);
            renderer = target.AddComponent<GsplatRenderer>();
            renderer.enabled = false;
            target.SetActive(true);
            plane = new Mesh { name = "Stage1 test XZ rectangle" };
            plane.vertices = new[] { new Vector3(-2, 0, -4), new Vector3(-2, 0, 5), new Vector3(3, 0, 5), new Vector3(3, 0, -4) };
            plane.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            plane.RecalculateBounds();
            owned.Add(plane);
        }

        [TearDown]
        public void TearDown()
        {
            if (binder) UnityEngine.Object.DestroyImmediate(binder.gameObject);
            for (int i = owned.Count - 1; i >= 0; i--)
                if (owned[i]) UnityEngine.Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }

        GameObject NewObject(string name)
        {
            var obj = new GameObject(name);
            owned.Add(obj);
            return obj;
        }

        MeshRenderer NewPlane(string name, bool active = true)
        {
            var obj = NewObject(name);
            obj.transform.SetParent(root, false);
            obj.AddComponent<MeshFilter>().sharedMesh = plane;
            var visual = obj.AddComponent<MeshRenderer>();
            obj.AddComponent<BoxCollider>();
            obj.SetActive(active);
            return visual;
        }

        Behaviour NewBinder()
        {
            var type = typeof(Stage1Counts).Assembly.GetType("SplatPreprocess.Stage1AuthoredWalls");
            Assert.That(type, Is.Not.Null, "The persistent authored wall binding is missing.");
            binder = (Behaviour)NewObject("Stage1 wall binding test").AddComponent(type);
            return binder;
        }

        static object Call(object target, string method, params object[] args)
        {
            var member = target.GetType().GetMethod(method);
            Assert.That(member, Is.Not.Null, "Missing wall-binding API: " + method);
            try { return member.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }

        static T Read<T>(object target, string name)
        {
            var member = target.GetType().GetProperty(name);
            Assert.That(member, Is.Not.Null, "Missing wall-binding state: " + name);
            return (T)member.GetValue(target);
        }

        void Bind() => Call(binder, "Bind", root, renderer);
        void Tick() => binder.SendMessage("Update", SendMessageOptions.RequireReceiver);

        [Test]
        public void OnlyExactFloorAndInactivePlanesAreExcludedWithoutHidingTheirMeshes()
        {
            var floor = NewPlane("Floor");
            var wall = NewPlane("Wall");
            var lowerCaseFloor = NewPlane("floor");
            var inactive = NewPlane("Inactive wall", false);
            NewBinder();
            Bind();
            Assert.That(Read<int>(binder, "WallCount"), Is.EqualTo(2));
            foreach (var visual in new[] { floor, wall, lowerCaseFloor, inactive })
            {
                Assert.That(visual.forceRenderingOff, Is.False, "Splat visibility must not hide the authored plane meshes.");
                Assert.That(visual.enabled, Is.True);
                Assert.That(visual.GetComponent<BoxCollider>().enabled, Is.True, "Gameplay collision must remain enabled.");
            }
            var walls = Read<IReadOnlyList<Stage1Wall>>(binder, "Walls");
            Assert.That(walls[0].BoundsMinMaxXZ, Is.EqualTo(new Vector4(-2, 3, -4, 5)));
            Assert.That(walls[0].RearDepth, Is.EqualTo(0.1f));
            Assert.That(renderer.GsplatResource, Is.Null, "A disabled renderer must not acquire GPU buffers for wall authoring.");
        }

        [Test]
        public void TransformAndDepthChangesRefreshTheWorldSpaceWall()
        {
            var wall = NewPlane("Wall");
            NewBinder();
            Bind();
            wall.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(0, 0, 90));
            wall.transform.localScale = new Vector3(2, 3, 4);
            Tick();
            var data = Read<IReadOnlyList<Stage1Wall>>(binder, "Walls")[0];
            Assert.That(data.WorldPlane.x, Is.EqualTo(-1).Within(1e-5));
            Assert.That(data.WorldPlane.y, Is.EqualTo(0).Within(1e-5));
            Assert.That(data.WorldPlane.z, Is.EqualTo(0).Within(1e-5));
            Assert.That(data.WorldPlane.w, Is.EqualTo(2).Within(1e-5));
            Assert.That(data.WorldToLocal.MultiplyPoint3x4(new Vector3(2, 3, 4)).sqrMagnitude, Is.LessThan(1e-8));
            binder.GetType().GetProperty("RearDepth").SetValue(binder, 0.25f);
            Tick();
            Assert.That(Read<IReadOnlyList<Stage1Wall>>(binder, "Walls")[0].RearDepth, Is.EqualTo(0.25f));
            Assert.That(renderer.GsplatResource, Is.Null);
        }

        [Test]
        public void DisablingAndReenablingNeverChangesAuthoredVisibilityFlags()
        {
            var normal = NewPlane("Wall");
            var alreadyHidden = NewPlane("Floor");
            normal.enabled = false;
            alreadyHidden.forceRenderingOff = true;
            NewBinder();
            Bind();
            binder.enabled = false;
            Assert.That(normal.forceRenderingOff, Is.False);
            Assert.That(alreadyHidden.forceRenderingOff, Is.True);
            Assert.That(Read<int>(binder, "WallCount"), Is.Zero);
            binder.enabled = true;
            Assert.That(normal.forceRenderingOff, Is.False);
            Assert.That(normal.enabled, Is.False, "The binding must not enable an intentionally disabled mesh.");
            Assert.That(alreadyHidden.forceRenderingOff, Is.True);
            Assert.That(Read<int>(binder, "WallCount"), Is.EqualTo(1));
            alreadyHidden.forceRenderingOff = false;
            Tick();
            Assert.That(alreadyHidden.forceRenderingOff, Is.False, "Visibility edits must remain under user control.");
            binder.enabled = false;
            Assert.That(normal.forceRenderingOff, Is.False);
            Assert.That(alreadyHidden.forceRenderingOff, Is.False);
        }

        [Test]
        public void PlaneMembershipChangesLeaveMeshVisibilityUntouched()
        {
            var removed = NewPlane("Wall");
            var floor = NewPlane("Floor");
            NewBinder();
            Bind();
            removed.transform.SetParent(null, true);
            Tick();
            Assert.That(removed.forceRenderingOff, Is.False);
            Assert.That(floor.forceRenderingOff, Is.False);
            Assert.That(Read<int>(binder, "WallCount"), Is.Zero);
            var added = NewPlane("Added wall");
            Tick();
            Assert.That(added.forceRenderingOff, Is.False);
            Assert.That(Read<int>(binder, "WallCount"), Is.EqualTo(1));
        }

        [Test]
        public void StableUpdatesReuseTheCompiledLayoutWhileTheRendererIsDisabled()
        {
            NewPlane("Wall");
            NewBinder();
            Bind();
            object original = Read<IReadOnlyList<Stage1Wall>>(binder, "Walls");
            for (int i = 0; i < 10; i++) Tick();
            Assert.That(Read<IReadOnlyList<Stage1Wall>>(binder, "Walls"), Is.SameAs(original));
            Assert.That(renderer.GsplatResource, Is.Null);
        }

        [Test]
        public void UnsupportedVolumeClearsTheCompiledMaskAndReportsFailure()
        {
            var wall = NewPlane("Wall");
            NewBinder();
            Bind();
            var volume = new Mesh { name = "Unsupported volume" };
            volume.vertices = new[] { new Vector3(-1, -1, -1), new Vector3(1, 1, 1) };
            volume.RecalculateBounds();
            owned.Add(volume);
            wall.GetComponent<MeshFilter>().sharedMesh = volume;
            Assert.Throws<InvalidOperationException>(() => Call(binder, "ApplyNow"));
            Assert.That(Read<int>(binder, "WallCount"), Is.Zero);
            Assert.That(Read<string>(binder, "LastError"), Is.Not.Empty);
            Assert.That(wall.forceRenderingOff, Is.False, "Invalid splat geometry must not alter mesh visibility.");
            binder.enabled = false;
            Assert.That(wall.forceRenderingOff, Is.False);
        }

        [Test]
        public void MoreThanSixteenWallsFailsWithoutTruncatingTheLayout()
        {
            for (int i = 0; i < 17; i++) NewPlane("Wall " + i);
            NewBinder();
            Assert.Throws<InvalidOperationException>(Bind);
            Assert.That(Read<int>(binder, "WallCount"), Is.Zero);
            Assert.That(Read<string>(binder, "LastError"), Is.Not.Empty);
            foreach (var visual in root.GetComponentsInChildren<MeshRenderer>()) Assert.That(visual.forceRenderingOff, Is.False);
        }
    }
}
