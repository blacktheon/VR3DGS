using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SplatPreprocess.Editor;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1CustomizationBoundaryTests
    {
        static object Invoke(string name, params object[] args)
        {
            var method = typeof(Stage1Round1Service).GetMethod(name);
            Assert.That(method, Is.Not.Null, "Missing customization boundary: " + name);
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        [Test]
        public void InactiveAuthoredDeletionBoxesRemainPartOfTheSnapshot()
        {
            var root = new GameObject("Deletable");
            try
            {
                var child = new GameObject("Hidden box");
                child.transform.SetParent(root.transform, false);
                child.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(20, 40, 60));
                child.transform.localScale = new Vector3(2, 3, 4);
                var box = child.AddComponent<BoxCollider>();
                box.center = new Vector3(.2f, .3f, .4f);
                box.size = new Vector3(2, 4, 6);
                child.SetActive(false);
                var captured = (Stage1SceneBox[])Invoke("CaptureDeletionBoxes", root.transform);
                Assert.That(captured.Length, Is.EqualTo(1));
                Assert.That(captured[0].size, Is.EqualTo(new double[] { 2, 4, 6 }));
                Assert.That(captured[0].local_to_world[3], Is.EqualTo(2));
                Assert.That(child.activeSelf, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void ChangedDeletionVolumeCannotReuseThePreviousPreviewRank()
        {
            var expected = new Stage1SceneSnapshot { deletion_boxes = new[] { new Stage1SceneBox { name = "box", center = new double[] { 0, 0, 0 }, size = new double[] { 1, 1, 1 }, local_to_world = Identity(), world_to_local = Identity() } } };
            var actual = JsonUtility.FromJson<Stage1SceneSnapshot>(JsonUtility.ToJson(expected));
            Assert.DoesNotThrow(() => Invoke("ValidateCustomizationSnapshot", expected, actual));
            actual.deletion_boxes[0].size[0] = 1.01;
            Assert.Throws<InvalidDataException>(() => Invoke("ValidateCustomizationSnapshot", expected, actual));
        }

        [Test]
        public void AddedSurfaceCannotReuseThePreviousPreviewRank()
        {
            var expected = new Stage1SceneSnapshot();
            var actual = new Stage1SceneSnapshot { surfaces = new[] { new Stage1SceneWall { name = "new surface" } } };
            Assert.Throws<InvalidDataException>(() => Invoke("ValidateCustomizationSnapshot", expected, actual));
        }

        static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    }
}
