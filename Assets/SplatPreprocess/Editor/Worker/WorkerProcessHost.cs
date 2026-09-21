using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SplatPreprocess.Editor
{
    public sealed class WorkerProcessHost
    {
        readonly WorkerJobStore store;
        readonly string python, workerFolder;
        public WorkerProcessHost(WorkerJobStore store, string python, string workerFolder)
        { this.store=store; this.python=python; this.workerFolder=workerFolder; }
        public string Start(string operation, string sourcePath)
        {
            WorkerJobStore.ValidateOperation(operation);
            if (!File.Exists(python)) throw new FileNotFoundException("Set up the local Python worker first", python);
            if (operation == "inspect" && !File.Exists(sourcePath)) throw new FileNotFoundException("Choose the original PLY", sourcePath);
            if (store.Reconcile().Any(s => !s.IsTerminal)) throw new InvalidOperationException("A worker job is already active; wait or cancel it first");
            string id = store.Create(operation, sourcePath);
            string directory = store.JobDirectory(id);
            try
            {
                var start = new ProcessStartInfo(python, "-m splat_worker --job " + Quote(Path.Combine(directory,"request.json")))
                {
                    WorkingDirectory=workerFolder, UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden
                };
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                // The CLI owns its log files. No Editor pipes or exit handlers can be lost on domain reload.
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new IOException("Python did not start");
                    try { WorkerJobStore.WriteAtomic(Path.Combine(directory,"launcher.json"), new WorkerJobStore.Launcher { pid=process.Id, started_utc=WorkerJobStore.ProcessStarted(process) }); }
                    catch (InvalidOperationException) { /* A tiny job may finish before its launcher is queried. */ }
                }
            }
            catch (Exception error)
            {
                WorkerJobStore.WriteAtomic(Path.Combine(directory,"status.json"), new JobSnapshot { job_id=id, operation=operation, state="Failed", message="Could not start Python", error=error.Message, updated_utc=WorkerJobStore.Now });
                throw;
            }
            return id;
        }

        // Windows CommandLineToArgvW quoting; arguments never pass through a shell.
        public static string Quote(string argument)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
