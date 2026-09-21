using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1ContractsTests
    {
        [TestCase(6011316, 0, 0)]
        [TestCase(6011316, 5000, 3005658)]
        [TestCase(6011316, 10000, 6011316)]
        [TestCase(7, 3333, 2)]
        [TestCase(int.MaxValue, 10000, int.MaxValue)]
        public void ExactIntegerCounts(int n, int percentage, int expected) => Assert.AreEqual(expected, Stage1Counts.KeepCount(n, percentage));

        [TestCase(-1, 100)]
        [TestCase(7, -1)]
        [TestCase(7, 10001)]
        public void InvalidCountsAreRejected(int n, int percentage) => Assert.Throws<ArgumentOutOfRangeException>(() => Stage1Counts.KeepCount(n, percentage));

        [Test]
        public void ActualWorkerManifestRoundTripsWithoutLosingIdentity()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../SplatData/sources/7351c4694b28/source_manifest.json"));
            if (!File.Exists(path)) Assert.Ignore("Local source inspection has not run");
            var manifest = JsonUtility.FromJson<SourceManifest>(File.ReadAllText(path));
            manifest.Validate();
            Assert.AreEqual(6011316, manifest.vertex_count);
            Assert.AreEqual("7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d", manifest.sha256);
            Assert.AreEqual("uncalibrated", manifest.calibration_status);
            manifest.id_rule = "position";
            Assert.Throws<InvalidDataException>(() => manifest.Validate());
        }
    }
}
