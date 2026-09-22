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

        public Stage1ReviewState State => _selection ? _selection.State : null;
        public bool IsRecording => recorder != null && _recordOrdinaryPoses;
        public bool IsSessionOpen => recorder != null;
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
            if (recorder != null) return;
            if (!_selection || !_viewBindings || !_viewBindings.HeadCamera || !_viewBindings.SourceRoot)
                throw new InvalidOperationException("Assign the review renderer, head camera, model and tracking origin bindings");
            if (_selection.State == null) _selection.LoadConfiguredRank();
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
            _selection.BeginSession();
            State.Changed += OnStateChanged;
            poseFilter = new Stage1PoseFilter();
            edges = new Stage1ButtonEdges();
            BookmarkCount = 0;
            nextFlush = Time.realtimeSinceStartupAsDouble + 1;
            startFailed = false;
            SetStatus(_recordOrdinaryPoses ? "Pose recording on" : "Pose recording stopped");
        }

        public void EndSession()
        {
            if (State != null) State.Changed -= OnStateChanged;
            recorder?.Stop();
            recorder = null;
            _selection?.EndSession();
        }

        public void SetCandidateNormalized(float normalized)
        {
            StartSession();
            State.SetCandidateNormalized(normalized);
        }

        public void BookmarkCurrentView()
        {
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
            StartSession();
            _recordOrdinaryPoses = true;
            poseFilter = new Stage1PoseFilter();
            SetStatus("Pose recording on");
        }

        public void StopRecording()
        {
            _recordOrdinaryPoses = false;
            recorder?.Flush();
            SetStatus("Pose recording stopped");
        }

        void LateUpdate()
        {
            if (startFailed || recorder == null) return;
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
                    if ((press & Stage1ButtonPress.Bookmark) != 0) BookmarkCurrentView();
                    if ((press & Stage1ButtonPress.ToggleOriginal) != 0) ToggleOriginal();
                }
                if (_recordOrdinaryPoses && _viewBindings.TryCaptureViews(out var view) && poseFilter.ShouldRecord(view))
                    recorder.AppendPose(view);
                if (Time.realtimeSinceStartupAsDouble >= nextFlush)
                {
                    recorder.Flush();
                    nextFlush = Time.realtimeSinceStartupAsDouble + 1;
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
                "\nA: Save view   B: Original / candidate   Saved: " + BookmarkCount + "\n" + lastStatus;
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
