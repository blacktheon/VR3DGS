// Headset movement recording and A-button view capture are temporarily paused.
// Uncomment this one define to restore both, including their UI controls.
// #define STAGE1_REVIEW_CAPTURE

using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;
using Oculus.Interaction.Input;

namespace SplatPreprocess
{
    /// <summary>Discrete calls may be connected to user-owned input events. Enable polling only when those events are not also bound.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class Stage1ReviewActions : MonoBehaviour
    {
        [SerializeField] Stage1SelectionController _selection;
        [SerializeField] Stage1SceneBindings _viewBindings;
        [SerializeField] bool _pollRightControllerButtons;
        [SerializeField, Tooltip("Use the same Meta right-controller data source as locomotion when assigned.")]
        Controller _rightController;
        [SerializeField] bool _recordOrdinaryPoses = true;
        [SerializeField] string _sessionRoot = "SplatData/sessions";
        [SerializeField, Range(1, 3)] float _bookmarkPriority = 3;
        [SerializeField] UnityEvent _onBookmark = new UnityEvent();

        Stage1SessionRecorder recorder;
        Stage1PoseFilter poseFilter;
        Stage1ButtonEdges edges = new Stage1ButtonEdges();
        double nextFlush;
        string lastStatus = "Review is ready to start";
        bool startFailed;
        bool sessionOpen;

        public static bool CaptureEnabled
        {
            get
            {
#if STAGE1_REVIEW_CAPTURE
                return true;
#else
                return false;
#endif
            }
        }

        public Stage1ReviewState State => _selection ? _selection.State : null;
        public bool IsRecording => CaptureEnabled && recorder != null && _recordOrdinaryPoses;
        public bool IsSessionOpen => sessionOpen;
        public int BookmarkCount { get; private set; }
        public string SessionDirectory => recorder?.DirectoryPath;
        public string LastStatus => lastStatus;
        public event Action StatusChanged;

        public void Bind(Stage1SelectionController selection, Stage1SceneBindings views)
        {
            EndSession();
            _selection = selection;
            _viewBindings = views;
            startFailed = false;
        }

        void Start()
        {
            try { StartSession(); }
            catch (Exception exception) { startFailed = true; ReportFailure(exception); }
        }

        public void StartSession()
        {
            if (sessionOpen) return;
            if (!_selection) throw new InvalidOperationException("Assign the Stage 1 selection controller before review");
            if (CaptureEnabled && (!_viewBindings || !_viewBindings.HeadCamera || !_viewBindings.SourceRoot))
                throw new InvalidOperationException("Assign the review renderer, head camera, model and tracking origin bindings");
            if (_selection.State == null) _selection.LoadConfiguredRank();
            // Review input remains usable without creating a recorder or querying the headset.
            if (CaptureEnabled)
            {
                var root = _sessionRoot;
                if (string.IsNullOrWhiteSpace(root)) root = "SplatData/sessions";
                if (!Path.IsPathRooted(root))
                {
#if UNITY_EDITOR
                    root = Path.Combine(Application.dataPath, "..", root);
#else
                    root = Path.Combine(Application.persistentDataPath, root);
#endif
                }
                var directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                recorder = new Stage1SessionRecorder(directory, State);
                poseFilter = new Stage1PoseFilter();
                nextFlush = Time.realtimeSinceStartupAsDouble + 1;
            }
            _selection.BeginSession();
            State.Changed += OnStateChanged;
            sessionOpen = true;
            edges = new Stage1ButtonEdges();
            BookmarkCount = 0;
            startFailed = false;
            SetStatus(CaptureEnabled ? (_recordOrdinaryPoses ? "Pose recording on" : "Pose recording stopped") : "Review ready");
        }

        public void EndSession()
        {
            if (State != null) State.Changed -= OnStateChanged;
            recorder?.Stop();
            recorder = null;
            sessionOpen = false;
            _selection?.EndSession();
        }

        public void SetCandidateNormalized(float normalized)
        {
            StartSession();
            State.SetCandidateNormalized(normalized);
        }

        public void BookmarkCurrentView()
        {
            if (!CaptureEnabled) return; // Keep serialized UnityEvents safe while capture is paused.
            StartSession();
            if (!_viewBindings.TryCaptureViews(out var view))
            {
                SetStatus("View not saved: waiting for the headset camera. Press A again when tracking resumes.");
                return;
            }
            var mark = State.GetBookmark(view);
            mark.priority = Mathf.Clamp(_bookmarkPriority, 1, 3);
            recorder.AppendMark(mark);
            BookmarkCount++;
            SetStatus("View saved (" + BookmarkCount.ToString(CultureInfo.InvariantCulture) + ")");
            _onBookmark.Invoke();
        }

        public void ToggleOriginal()
        {
            StartSession();
            State.ToggleOriginal();
        }

        public void StartRecording()
        {
            if (!CaptureEnabled) return;
            StartSession();
            _recordOrdinaryPoses = true;
            poseFilter = new Stage1PoseFilter();
            SetStatus("Pose recording on");
        }

        public void StopRecording()
        {
            if (!CaptureEnabled) return;
            _recordOrdinaryPoses = false;
            recorder?.Flush();
            SetStatus("Pose recording stopped");
        }

        void LateUpdate()
        {
            if (startFailed || !sessionOpen) return;
            try
            {
                if (_pollRightControllerButtons)
                {
                    bool a, b;
                    if (_rightController)
                    {
                        var connected = _rightController.IsConnected && _rightController.Handedness == Handedness.Right;
                        a = connected && _rightController.IsButtonUsageAnyActive(ControllerButtonUsage.PrimaryButton);
                        b = connected && _rightController.IsButtonUsageAnyActive(ControllerButtonUsage.SecondaryButton);
                    }
                    else
                    {
                        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                        right.TryGetFeatureValue(CommonUsages.primaryButton, out a);
                        right.TryGetFeatureValue(CommonUsages.secondaryButton, out b);
                    }
                    var press = edges.Sample(a, b);
                    // Simultaneous A/B captures the mode that was visible when both buttons were sampled.
                    if (CaptureEnabled && (press & Stage1ButtonPress.Bookmark) != 0) BookmarkCurrentView();
                    if ((press & Stage1ButtonPress.ToggleOriginal) != 0) ToggleOriginal();
                }
                if (CaptureEnabled && recorder != null)
                {
                    if (_recordOrdinaryPoses && _viewBindings.TryCaptureViews(out var view) && poseFilter.ShouldRecord(view))
                        recorder.AppendPose(view);
                    if (Time.realtimeSinceStartupAsDouble >= nextFlush)
                    {
                        recorder.Flush();
                        nextFlush = Time.realtimeSinceStartupAsDouble + 1;
                    }
                }
            }
            catch (Exception exception)
            {
                // Preserve existing files and stop retrying a failed disk write every frame.
                EndSession();
                startFailed = true;
                ReportFailure(exception);
            }
        }

        public string GetStatusText()
        {
            var state = State;
            if (state == null) return "Keep splats (%)\n" + lastStatus;
            var percent = (state.CandidateCentiPercent / 100.0).ToString("F2", CultureInfo.InvariantCulture);
            var mode = state.IsOriginal ? "Original 100%" : "Candidate " + percent + "%";
            return "Keep splats (%)\nCandidate: " + percent + "%  |  Showing: " + mode +
                "\nSelected: " + state.DisplayedCount.ToString("N0", CultureInfo.InvariantCulture) + " / " + state.EligibleCount.ToString("N0", CultureInfo.InvariantCulture) +
                "\nRank: " + state.FrozenRankId + "  Source: " + state.SourceHash.Substring(0, Math.Min(8, state.SourceHash.Length)) +
                (CaptureEnabled ? "\nA: Save view   B: Original / candidate   Saved: " + BookmarkCount : "\nB: Original / candidate") + "\n" + lastStatus;
        }

        void OnStateChanged() => StatusChanged?.Invoke();
        void SetStatus(string status) { lastStatus = status; StatusChanged?.Invoke(); }
        void ReportFailure(Exception exception)
        {
            SetStatus("Review unavailable: " + exception.Message);
            Debug.LogError("Stage 1 review: " + exception.Message, this);
        }

        void OnApplicationPause(bool paused) { if (paused) recorder?.Flush(); }
        void OnApplicationQuit() => EndSession();
        void OnDisable() => EndSession();
    }
}
