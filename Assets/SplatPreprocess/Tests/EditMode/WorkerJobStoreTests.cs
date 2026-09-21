using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using SplatPreprocess.Editor;

namespace SplatPreprocess.Tests
{
    public sealed class WorkerJobStoreTests
    {
        string root;
        [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "Stage1 jobs 测试 & " + Guid.NewGuid().ToString("N"));
        [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test]
        public void ReopeningStoreDoesNotDuplicateRequestsAndCancellationIsDurable()
        {
            var store = new WorkerJobStore(root);
            var id = store.Create("inspect", "C:/source folder/original & model.ply");
            var reopened = new WorkerJobStore(root);
            Assert.AreEqual(1, reopened.Reconcile().Count);
            reopened.Cancel(id);
            Assert.IsTrue(File.Exists(Path.Combine(root, "jobs", id, "cancel.requested")));
            Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(root, "jobs")).Length);
        }

        [Test]
        public void ReusedPidCannotMasqueradeAsALiveWorker()
        {
            var store = new WorkerJobStore(root);
            var id = store.Create("inspect", "unused");
            var status = new JobSnapshot { job_id=id, operation="inspect", state="Running", worker_pid=System.Diagnostics.Process.GetCurrentProcess().Id, worker_started_utc=1 };
            File.WriteAllText(Path.Combine(root,"jobs",id,"status.json"), JsonUtility.ToJson(status));
            Assert.AreEqual("Interrupted", new WorkerJobStore(root).Reconcile()[0].state);
        }

        [Test]
        public void MissingResultIsNotInventedAndUnsafeJobIdsAreRejected()
        {
            var store = new WorkerJobStore(root);
            Assert.IsNull(store.Current("inspect"));
            Assert.Throws<ArgumentException>(() => store.Cancel("../outside"));
        }

        [Test]
        public void CancellationRequestRemainsVisibleWhileWorkerReachesASafeBoundary()
        {
            var store = new WorkerJobStore(root);
            var id = store.Create("check_environment", "unused");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                var status = new JobSnapshot { job_id=id, operation="check_environment", state="Running", worker_pid=process.Id, worker_started_utc=WorkerJobStore.ProcessStarted(process) };
                File.WriteAllText(Path.Combine(root,"jobs",id,"status.json"), JsonUtility.ToJson(status));
                store.Cancel(id);
                Assert.AreEqual("Cancelling", store.Reconcile()[0].state);
                Assert.IsFalse(store.Reconcile()[0].IsTerminal);
            }
        }
    }
}
