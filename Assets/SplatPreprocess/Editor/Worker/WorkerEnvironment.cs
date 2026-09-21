using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace SplatPreprocess.Editor
{
    public static class WorkerEnvironment
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string WorkerFolder => Path.Combine(ProjectRoot, "Tools/SplatWorker");
        public static string Python => Path.Combine(WorkerFolder, ".venv/Scripts/python.exe");
        public static string DataRoot => Path.Combine(ProjectRoot, "SplatData");
        public static string SetupDirectory => Path.Combine(DataRoot, "setup");
        public static bool SetupRunning
        {
            get
            {
                var launcher = WorkerJobStore.Read<WorkerJobStore.Launcher>(Path.Combine(SetupDirectory, "launcher.json"));
                return launcher != null && WorkerJobStore.IsSameProcess(launcher.pid, launcher.started_utc);
            }
        }
        public static void Setup()
        {
            if (SetupRunning) throw new InvalidOperationException("Worker setup is already running");
            Directory.CreateDirectory(SetupDirectory);
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + WorkerProcessHost.Quote(Path.Combine(WorkerFolder,"setup_worker.ps1")))
            { WorkingDirectory=WorkerFolder, UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new IOException("Worker setup did not start");
                WorkerJobStore.WriteAtomic(Path.Combine(SetupDirectory,"launcher.json"), new WorkerJobStore.Launcher { pid=process.Id, started_utc=WorkerJobStore.ProcessStarted(process) });
            }
        }
    }
}
