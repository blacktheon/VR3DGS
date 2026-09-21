using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEngine;

namespace SplatPreprocess.Editor
{
    public sealed class SplatPreprocessorWindow : EditorWindow
    {
        [SerializeField] string sourcePath = "";
        [SerializeField] int tab;
        Vector2 scroll;
        WorkerJobStore store;
        IReadOnlyList<JobSnapshot> jobs = Array.Empty<JobSnapshot>();
        IReadOnlyList<string> preflight = Array.Empty<string>();
        bool preflightChecked;
        string error = "";
        double nextPoll;
        string PreferenceKey => "Stage1.Source." + WorkerEnvironment.ProjectRoot;

        [MenuItem("Tools/Splats/Stage 1 Preprocessor")]
        public static void Open() => GetWindow<SplatPreprocessorWindow>("Stage 1 Preprocessor");
        void OnEnable()
        {
            minSize = new Vector2(540, 460);
            if (string.IsNullOrEmpty(sourcePath)) sourcePath = EditorPrefs.GetString(PreferenceKey, "");
            store = new WorkerJobStore(WorkerEnvironment.DataRoot);
            RefreshStatus();
            EditorApplication.update += Tick;
        }
        void OnDisable() => EditorApplication.update -= Tick;
        void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .5;
            RefreshStatus();
            Repaint();
        }
        void RefreshStatus()
        {
            try { jobs = store.Reconcile(); }
            catch (Exception e) { error = e.Message; }
        }
        bool Busy => WorkerEnvironment.SetupRunning || jobs.Any(j => !j.IsTerminal);
        void Attempt(Action action)
        {
            try { error=""; action(); RefreshStatus(); }
            catch (Exception e) { error=e.Message; }
        }
        public string StartOperation(string operation)
        {
            if (WorkerEnvironment.SetupRunning) throw new InvalidOperationException("Wait for worker setup to finish");
            EditorPrefs.SetString(PreferenceKey, sourcePath);
            return new WorkerProcessHost(store, WorkerEnvironment.Python, WorkerEnvironment.WorkerFolder).Start(operation, sourcePath);
        }
        void OnGUI()
        {
            EditorGUILayout.LabelField("Stage 1 · Source and worker setup", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("gsplat-unity · Uncompressed SH0 reference · scale uncalibrated", EditorStyles.miniLabel);
            tab = GUILayout.Toolbar(tab, new[] { "Setup", "Process", "Review", "Export" });
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (tab == 0) DrawSetup();
            else if (tab == 1) EditorGUILayout.HelpBox("Source inspection and the CUDA environment check are available in Setup. Contribution scoring is the next milestone.", MessageType.Info);
            else if (tab == 2) DrawReview();
            else EditorGUILayout.HelpBox("Export becomes available after a verified ranking and subset review. The original PLY is never modified.", MessageType.Info);
            DrawJobs();
            EditorGUILayout.EndScrollView();
        }
        void DrawSetup()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Original source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                sourcePath = EditorGUILayout.TextField(sourcePath);
                if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                {
                    string selected = EditorUtility.OpenFilePanel("Choose the original Gaussian PLY", Path.GetDirectoryName(sourcePath) ?? "", "ply");
                    if (!string.IsNullOrEmpty(selected)) { sourcePath=selected; EditorPrefs.SetString(PreferenceKey,sourcePath); }
                }
            }
            using (new EditorGUI.DisabledScope(Busy || !File.Exists(sourcePath) || !File.Exists(WorkerEnvironment.Python)))
                if (GUILayout.Button("Inspect full source")) Attempt(() => StartOperation("inspect"));
            AttemptReadSource();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Local processing environment", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(File.Exists(WorkerEnvironment.Python) ? "Isolated Python is installed" : "Python worker is not installed");
            using (new EditorGUI.DisabledScope(Busy))
                if (GUILayout.Button("Install / repair worker dependencies")) Attempt(WorkerEnvironment.Setup);
            if (WorkerEnvironment.SetupRunning) EditorGUILayout.HelpBox("Installing dependencies. This may take several minutes. Progress is in SplatData/setup/setup.log.", MessageType.Info);
            var setup = WorkerJobStore.Read<JobSnapshot>(Path.Combine(WorkerEnvironment.SetupDirectory,"status.json"));
            if (setup != null)
            {
                bool interrupted = setup.state=="Running" && !WorkerEnvironment.SetupRunning;
                EditorGUILayout.HelpBox(interrupted ? "Setup stopped before completion. Open SplatData/setup/setup.log for details." : setup.message,
                    setup.state=="Failed" || interrupted ? MessageType.Error : MessageType.None);
            }
            using (new EditorGUI.DisabledScope(Busy || !File.Exists(WorkerEnvironment.Python)))
                if (GUILayout.Button("Check GPU environment")) Attempt(() => StartOperation("check_environment"));
            if (GUILayout.Button("Check Unity rendering setup")) Attempt(() => { preflight = Stage1Preflight.Check(); preflightChecked=true; });
            foreach (string message in preflight) EditorGUILayout.HelpBox(message, MessageType.Warning);
            if (preflightChecked && preflight.Count == 0) EditorGUILayout.HelpBox("Unity rendering setup checks passed.", MessageType.Info);
            if (GUILayout.Button("Open local results folder")) EditorUtility.RevealInFinder(WorkerEnvironment.DataRoot);
        }
        void AttemptReadSource()
        {
            try
            {
                var pointer = store.Current("inspect");
                if (pointer == null) return;
                var manifest = WorkerJobStore.Read<SourceManifest>(Path.Combine(pointer.result_dir,"source_manifest.json"));
                manifest.Validate();
                EditorGUILayout.LabelField($"Last inspected source: {manifest.vertex_count:N0} splats · SH{manifest.sh_degree}");
                EditorGUILayout.LabelField("SHA-256: " + manifest.sha256, EditorStyles.miniLabel);
                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(manifest.source_path), StringComparison.OrdinalIgnoreCase))
                    EditorGUILayout.HelpBox("These results belong to a different source path. Inspect the chosen source before using its results.", MessageType.Warning);
            }
            catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
        }
        void DrawReview()
        {
            foreach (var renderer in FindObjectsByType<GsplatRenderer>(FindObjectsSortMode.None))
            {
                if (!renderer.GsplatAsset) continue;
                EditorGUILayout.ObjectField("Preview", renderer, typeof(GsplatRenderer), true);
                EditorGUILayout.LabelField($"Uploaded: {renderer.SplatCount:N0} / {renderer.GsplatAsset.SplatCount:N0} · {renderer.GsplatAsset.Compression}");
                if (GUILayout.Button("Select model in Scene view")) { Selection.activeGameObject=renderer.gameObject; SceneView.lastActiveSceneView?.FrameSelected(); }
            }
            EditorGUILayout.HelpBox("The full source is available for inspection. Exact percentage selection, contribution ranking and A/B review are not implemented yet. Bindings for your VR rig will follow those controls.", MessageType.Info);
        }
        void DrawJobs()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recent jobs", EditorStyles.boldLabel);
            foreach (var job in jobs.Take(6))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(job.operation + " · " + job.state, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(job.message ?? "", EditorStyles.wordWrappedLabel);
                    if (!job.IsTerminal)
                    {
                        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false,18), job.progress, (job.progress * 100).ToString("F0") + "%");
                        if (GUILayout.Button("Request cancellation")) Attempt(() => store.Cancel(job.job_id));
                    }
                    if (!string.IsNullOrEmpty(job.error)) EditorGUILayout.HelpBox(job.error.Substring(0,Math.Min(job.error.Length,700)), MessageType.Error);
                    if (GUILayout.Button("Open job logs")) EditorUtility.RevealInFinder(store.JobDirectory(job.job_id));
                }
            }
        }
    }
}
