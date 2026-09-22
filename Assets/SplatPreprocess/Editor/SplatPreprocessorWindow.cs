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
        [SerializeField] string rankManifestPath = "";
        [SerializeField] string lastSceneSnapshot = "";
        [SerializeField] float editModeKeepPercent = 50f;
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
            EditorGUILayout.LabelField("Stage 1 · Splat preprocessing", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("gsplat-unity · Uncompressed SH0 reference · user-authored Unity units", EditorStyles.miniLabel);
            tab = GUILayout.Toolbar(tab, new[] { "Setup", "Process", "Review", "Export" });
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (tab == 0) DrawSetup();
            else if (tab == 1) DrawProcess();
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
            EditorGUILayout.Space();
            var latest = DrawRoundResult();
            if (string.IsNullOrEmpty(rankManifestPath))
            {
                var configured = FindObjectsByType<Stage1SelectionController>(FindObjectsSortMode.None).FirstOrDefault(c => !string.IsNullOrEmpty(c.RankManifestPath));
                if (configured) rankManifestPath = configured.RankManifestPath;
                else if (latest != null) rankManifestPath = Path.Combine(latest.result_dir, "rank_manifest.json");
            }
            EditorGUILayout.LabelField("Frozen ranking for review", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                rankManifestPath = EditorGUILayout.TextField(rankManifestPath);
                if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                {
                    var selected = EditorUtility.OpenFilePanel("Choose a completed rank manifest", Path.GetDirectoryName(rankManifestPath) ?? WorkerEnvironment.DataRoot, "json");
                    if (!string.IsNullOrEmpty(selected)) rankManifestPath = selected;
                }
            }
            using (new EditorGUI.DisabledScope(latest == null || !File.Exists(latest == null ? "" : Path.Combine(latest.result_dir, "rank_manifest.json"))))
                if (GUILayout.Button("Select latest completed ranking")) rankManifestPath = Path.Combine(latest.result_dir, "rank_manifest.json");
            using (new EditorGUI.DisabledScope(Busy || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(rankManifestPath)))
                if (GUILayout.Button("Load selected ranking into review")) Attempt(() =>
                {
                    Stage1Round1Service.LoadReviewRank(rankManifestPath, sourcePath);
                    foreach (var controller in FindObjectsByType<Stage1SelectionController>(FindObjectsSortMode.None))
                        if (controller.State != null) SetEditModePreview(controller, controller.EditModeCentiPercent / 100f, controller.EditModeShowOriginal);
                });
            bool hasLoadedRank = false;
            foreach (var controller in FindObjectsByType<Stage1SelectionController>(FindObjectsSortMode.None))
            {
                if (controller.State == null)
                {
                    if (!string.IsNullOrEmpty(controller.LastPreviewError)) EditorGUILayout.HelpBox(controller.LastPreviewError, MessageType.Warning);
                    continue;
                }
                hasLoadedRank = true;
                var state = controller.State;
                EditorGUILayout.LabelField("Loaded rank: " + state.FrozenRankId, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField($"Candidate: {state.CandidateCentiPercent / 100.0:F2}% · Showing {state.DisplayMode} · {state.DisplayedCount:N0} / {state.EligibleCount:N0}");
                EditorGUILayout.LabelField($"Original source provenance: {state.SourceCount:N0} rows", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(Busy || EditorApplication.isPlayingOrWillChangePlaymode || controller.IsSessionActive || !controller.Renderer || !controller.Renderer.isActiveAndEnabled))
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Edit Mode preview", EditorStyles.boldLabel);
                    EditorGUI.BeginChangeCheck();
                    float percent = EditorGUILayout.Slider("Keep splats (%)", state.CandidateCentiPercent / 100f, 0f, 100f);
                    bool original = EditorGUILayout.Toggle("Show original (100%)", state.IsOriginal);
                    if (EditorGUI.EndChangeCheck()) Attempt(() =>
                    {
                        SetEditModePreview(controller, percent, original);
                        editModeKeepPercent = controller.State.CandidateCentiPercent / 100f;
                    });
                }
            }
            if (!hasLoadedRank) EditorGUILayout.HelpBox("The configured preview reloads after scripts recompile or a saved scene opens. Save pending edits first. A changed wall, deletion box or surface layout requires an updated ranking.", MessageType.Info);
            EditorGUILayout.HelpBox("Edit Mode preview updates the Game and Scene views without entering Play Mode or recording a session. Keep Show original unchecked to see the selected percentage. In Play Mode, the physical slider's position controls the percentage.", MessageType.None);
            EditorGUILayout.HelpBox("In Play Mode, the existing physical slider controls Keep splats (%). Right A saves the exact view and remembered candidate; right B compares the eligible original with that candidate. Rank changes are available only between review sessions.", MessageType.Info);
            EditorGUILayout.HelpBox("Ordinary pose recording and exact bookmarks are saved separately under SplatData/sessions. Use the scene review controls to start or stop pose recording. The source and scene must match the selected rank.", MessageType.None);
        }

        public static void SetEditModePreview(Stage1SelectionController controller, float keepPercent, bool showOriginal)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !controller || controller.IsSessionActive)
                throw new InvalidOperationException("Edit Mode preview is available only outside a running review session");
            if (controller.State == null) throw new InvalidOperationException("Load the selected ranking before adjusting its preview");
            if (!controller.Renderer || !controller.Renderer.isActiveAndEnabled || File.Exists(Stage1Round1Service.LeasePath))
                throw new InvalidOperationException("Wait until the source preview is enabled and processing has finished");
            if (float.IsNaN(keepPercent) || float.IsInfinity(keepPercent) || keepPercent < 0 || keepPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(keepPercent));
            controller.State.SetCandidateNormalized(keepPercent / 100f);
            if (controller.State.IsOriginal != showOriginal) controller.State.ToggleOriginal();
            Undo.RecordObject(controller, "Set splat preview percentage");
            controller.RememberEditModePreview(controller.State.CandidateCentiPercent, showOriginal);
            EditorUtility.SetDirty(controller);
            controller.EnsurePreviewSelection();
            foreach (var panel in FindObjectsByType<Stage1PreviewPanel>(FindObjectsSortMode.None)) panel.Refresh();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.GetType().Name == "GameView" || window is SplatPreprocessorWindow) window.Repaint();
        }

        void DrawProcess()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("First scoring round", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Capture the saved scene, score full-source contribution, freeze one ranking, then verify independent views. Eye positions are sampled 0.3–1.8 units above each authored NavMesh surface. The floor excludes splat centers below its plane; directional wall tests use the finite wall bounds and a 0.1-unit rear band.", MessageType.Info);
            EditorGUILayout.HelpBox("These distances use your authored Unity units. The source renderer is temporarily paused during GPU processing and restored when the job ends. Save scene edits and exit Play Mode before starting.", MessageType.None);
            EditorGUILayout.LabelField("Source", sourcePath, EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(Busy || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(sourcePath)))
            {
                if (GUILayout.Button("Capture immutable scene snapshot")) Attempt(() => lastSceneSnapshot = Stage1Round1Service.ExportScene(sourcePath));
                using (new EditorGUI.DisabledScope(!File.Exists(WorkerEnvironment.Python)))
                    if (GUILayout.Button("Score scene and run first-round verification")) Attempt(() =>
                    {
                        EditorPrefs.SetString(PreferenceKey, sourcePath);
                        Stage1Round1Service.StartRound(sourcePath);
                    });
            }
            if (!string.IsNullOrEmpty(lastSceneSnapshot))
            {
                EditorGUILayout.LabelField("Last captured snapshot", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(lastSceneSnapshot, EditorStyles.wordWrappedLabel, GUILayout.Height(38));
                if (GUILayout.Button("Show snapshot file")) EditorUtility.RevealInFinder(lastSceneSnapshot);
            }
            if (File.Exists(Stage1Round1Service.LeasePath))
                EditorGUILayout.HelpBox("Preview pause recovery is active. Keep this scene available until processing finishes.", MessageType.Info);
            var latestJob = jobs.FirstOrDefault(job => job.operation == "round1");
            if (latestJob != null)
                EditorGUILayout.LabelField("Round-one status: " + latestJob.state + " · " + latestJob.message, EditorStyles.wordWrappedLabel);
            DrawRoundResult();
        }

        ResultPointer DrawRoundResult()
        {
            ResultPointer latest;
            try { latest = store.Current("round1"); }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); return null; }
            if (latest == null)
            {
                EditorGUILayout.HelpBox("No completed first-round result has been published yet.", MessageType.None);
                return null;
            }
            var rankPath = Path.Combine(latest.result_dir, "rank_manifest.json");
            var reportPath = Path.Combine(latest.result_dir, "report.html");
            if (!File.Exists(rankPath))
            {
                EditorGUILayout.HelpBox("The published result is missing its rank manifest. Inspect the job logs before review.", MessageType.Error);
                return null;
            }
            try
            {
                var manifest = WorkerJobStore.Read<RankManifest>(rankPath);
                manifest.Validate(manifest.source_hash, manifest.scene_hash);
                EditorGUILayout.LabelField("Latest completed round", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(manifest.rank_id, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField($"Eligible baseline: {manifest.eligible_count:N0} / {manifest.source_count:N0} original rows");
                EditorGUILayout.LabelField("Rank manifest: " + rankPath, EditorStyles.wordWrappedMiniLabel);
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); return null; }
            using (new EditorGUI.DisabledScope(!File.Exists(reportPath)))
                if (GUILayout.Button("Open verification report")) Application.OpenURL(new Uri(reportPath).AbsoluteUri);
            if (GUILayout.Button("Show completed result files")) EditorUtility.RevealInFinder(latest.result_dir);
            return latest;
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
