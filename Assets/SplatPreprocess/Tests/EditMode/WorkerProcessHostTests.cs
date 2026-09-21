using System;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using SplatPreprocess.Editor;

namespace SplatPreprocess.Tests
{
    public sealed class WorkerProcessHostTests
    {
        [Test]
        public void ActualPythonProcessInspectsLiteralUnicodeAndShellCharacters()
        {
            string worker = Path.GetFullPath(Path.Combine(Application.dataPath, "../Tools/SplatWorker"));
            string python = Path.Combine(worker, ".venv/Scripts/python.exe");
            if (!File.Exists(python)) Assert.Ignore("Run worker environment setup first");
            string root = Path.Combine(Path.GetTempPath(), "Stage1 real process 测试 & " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "original source & literal.ply");
            string[] names = { "x", "y", "z", "rot_0", "rot_1", "rot_2", "rot_3", "scale_0", "scale_1", "scale_2", "opacity", "f_dc_0", "f_dc_1", "f_dc_2" };
            using (var file = File.Create(source))
            using (var writer = new BinaryWriter(file))
            {
                var header = new StringBuilder("ply\nformat binary_little_endian 1.0\nelement vertex 2\n");
                foreach (var name in names) header.Append("property float ").Append(name).Append('\n');
                writer.Write(Encoding.ASCII.GetBytes(header.Append("end_header\n").ToString()));
                for (int row=0; row<2; row++) foreach (var name in names) writer.Write(name=="rot_0" ? 1f : 0f);
            }
            var store = new WorkerJobStore(root);
            string id = null;
            try
            {
                id = new WorkerProcessHost(store, python, worker).Start("inspect", source);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                JobSnapshot state;
                do
                {
                    Thread.Sleep(30);
                    state = new WorkerJobStore(root).Reconcile()[0];
                } while (!state.IsTerminal && DateTime.UtcNow < deadline);
                Assert.AreEqual("Succeeded", state.state, state.error);
                var manifest = WorkerJobStore.Read<SourceManifest>(Path.Combine(store.Current("inspect").result_dir, "source_manifest.json"));
                manifest.Validate();
                Assert.AreEqual(2, manifest.vertex_count);
                Assert.AreEqual(Path.GetFullPath(source), manifest.source_path);
                Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(root,"jobs")).Length);
            }
            finally
            {
                if (id != null) store.Cancel(id);
                var cleanupDeadline = DateTime.UtcNow.AddSeconds(5);
                JobSnapshot last = id == null ? null : store.Reconcile()[0];
                while (last != null && WorkerJobStore.IsSameProcess(last.worker_pid, last.worker_started_utc) && DateTime.UtcNow < cleanupDeadline)
                    Thread.Sleep(20);
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                    throw new InvalidOperationException("Test cleanup escaped the temporary directory");
                if (last == null || !WorkerJobStore.IsSameProcess(last.worker_pid, last.worker_started_utc))
                {
                    // Windows may still be releasing inherited log handles after
                    // the authoritative worker exits. Bound cleanup by the same
                    // deadline instead of treating that release race as a job failure.
                    while (Directory.Exists(root))
                    {
                        try { Directory.Delete(root, true); }
                        catch (IOException) when (DateTime.UtcNow < cleanupDeadline) { Thread.Sleep(20); }
                    }
                }
            }
        }
    }
}
