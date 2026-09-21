using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SplatPreprocess.Editor
{
    [Serializable]
    public sealed class JobRequest
    {
        public int schema_version = 1;
        public string job_id, operation, source_path, output_root;
    }

    [Serializable]
    public sealed class JobSnapshot
    {
        public int schema_version = 1;
        public string job_id, operation, state, message, error, result_dir;
        public float progress;
        public int worker_pid;
        public double worker_started_utc, updated_utc;
        public bool IsTerminal => state == "Succeeded" || state == "Failed" || state == "Cancelled" || state == "Interrupted";
    }

    [Serializable]
    public sealed class ResultPointer
    {
        public int schema_version;
        public string job_id, operation, result_dir;
    }

    public sealed class WorkerJobStore
    {
        [Serializable] public sealed class Launcher { public int pid; public double started_utc; }
        public string Root { get; }
        public static double Now => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        public WorkerJobStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Path.Combine(Root, "jobs")); }
        public string JobDirectory(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-zA-Z0-9_-]{1,96}$")) throw new ArgumentException("Invalid job ID");
            return Path.Combine(Root, "jobs", id);
        }
        public string Create(string operation, string sourcePath)
        {
            ValidateOperation(operation);
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            string directory = JobDirectory(id);
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "request.json"), new JobRequest { job_id=id, operation=operation, source_path=sourcePath, output_root=Root });
            WriteAtomic(Path.Combine(directory, "status.json"), new JobSnapshot { job_id=id, operation=operation, state="Queued", message="Starting worker", updated_utc=Now });
            return id;
        }
        public IReadOnlyList<JobSnapshot> Reconcile()
        {
            var list = new List<JobSnapshot>();
            foreach (var directory in Directory.GetDirectories(Path.Combine(Root, "jobs")).OrderByDescending(p => p))
            {
                var path = Path.Combine(directory, "status.json");
                JobSnapshot state;
                try { state = Read<JobSnapshot>(path); }
                catch (Exception error) when (error is IOException || error is ArgumentException)
                {
                    list.Add(new JobSnapshot { job_id=Path.GetFileName(directory), state="Unknown", message="Cannot read job state: " + error.Message });
                    continue;
                }
                if (state == null) continue;
                bool alive = state.worker_pid > 0 && IsSameProcess(state.worker_pid, state.worker_started_utc);
                if (!state.IsTerminal && !alive)
                {
                    var launcher = Read<Launcher>(Path.Combine(directory, "launcher.json"));
                    bool starting = state.worker_pid == 0 && ((launcher != null && IsSameProcess(launcher.pid, launcher.started_utc)) || Now - state.updated_utc < 15);
                    if (!starting)
                    {
                        // The worker can publish its final state between the first read and process exit.
                        state = Read<JobSnapshot>(path) ?? state;
                        if (!state.IsTerminal)
                        {
                            state.state = "Interrupted";
                            state.message = "Worker ended before publishing a completed result; inspect its logs";
                            state.updated_utc = Now;
                            WriteAtomic(path, state);
                        }
                    }
                }
                if (!state.IsTerminal && File.Exists(Path.Combine(directory, "cancel.requested")))
                {
                    state.state = "Cancelling";
                    state.message = "Cancellation requested; waiting for the worker to reach a safe boundary";
                }
                list.Add(state);
            }
            return list.OrderByDescending(s => s.updated_utc).ToArray();
        }
        public void Cancel(string jobId) => File.WriteAllText(Path.Combine(JobDirectory(jobId), "cancel.requested"), "cancel\n");
        public ResultPointer Current(string operation)
        {
            ValidateOperation(operation);
            var pointer = Read<ResultPointer>(Path.Combine(Root, "current_" + operation + ".json"));
            if (pointer != null && (pointer.schema_version != 1 || pointer.operation != operation || !Directory.Exists(pointer.result_dir)))
                throw new InvalidDataException("Current result pointer is invalid");
            return pointer;
        }
        public static void ValidateOperation(string operation)
        {
            if (operation != "inspect" && operation != "check_environment") throw new ArgumentException("This milestone supports Inspect and Check Environment");
        }
        public static bool IsSameProcess(int pid, double started)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && Math.Abs(ProcessStarted(process) - started) < .02;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { return false; }
        }
        public static double ProcessStarted(Process process) => (process.StartTime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalSeconds;
        public static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            // Allow the worker's atomic rename while the UI is reading an older snapshot.
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file, Encoding.UTF8)) return JsonUtility.FromJson<T>(reader.ReadToEnd());
        }
        public static void WriteAtomic(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
