using System;
using System.IO;
using NUnit.Framework;
using SplatPreprocess.Editor;

namespace SplatPreprocess.Tests
{
    public sealed class SplatPreprocessorWindowTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("bad\0path")]
        public void BrowseWithAnEmptyOrInvalidPathUsesAnExistingFallback(string path)
        {
            var method = typeof(SplatPreprocessorWindow).GetMethod("ResolveBrowseDirectory");
            Assert.That(method, Is.Not.Null, "An empty path must not throw from the Browse button and break GUILayout.");
            var fallback = Path.GetTempPath();
            Assert.That(method.Invoke(null, new object[] { path, fallback }), Is.EqualTo(fallback));
        }

        [Test]
        public void BrowseStartsInTheSelectedFilesDirectory()
        {
            var method = typeof(SplatPreprocessorWindow).GetMethod("ResolveBrowseDirectory");
            Assert.That(method, Is.Not.Null);
            var folder = Path.Combine(Path.GetTempPath(), "splat-browse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                Assert.That(method.Invoke(null, new object[] { Path.Combine(folder, "source.ply"), Path.GetTempPath() }), Is.EqualTo(folder));
                Assert.That(method.Invoke(null, new object[] { folder, Path.GetTempPath() }), Is.EqualTo(folder));
            }
            finally { Directory.Delete(folder); }
        }
    }
}
