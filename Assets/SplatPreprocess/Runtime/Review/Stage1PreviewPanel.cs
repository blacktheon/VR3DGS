using UnityEngine;
using UnityEngine.XR;

namespace SplatPreprocess
{
    public sealed class Stage1PreviewPanel : MonoBehaviour
    {
        [SerializeField] Stage1ReviewActions _reviewActions;
        [SerializeField] TextMesh _statusText;
        [SerializeField] bool _showDesktopButtons;

        public void Bind(Stage1ReviewActions reviewActions, TextMesh statusText)
        {
            if (_reviewActions) _reviewActions.StatusChanged -= Refresh;
            _reviewActions = reviewActions;
            _statusText = statusText;
            if (isActiveAndEnabled && _reviewActions) _reviewActions.StatusChanged += Refresh;
            Refresh();
        }

        void OnEnable()
        {
            if (_reviewActions) _reviewActions.StatusChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            if (_reviewActions) _reviewActions.StatusChanged -= Refresh;
        }

        public void Refresh()
        {
            if (_reviewActions && _statusText) _statusText.text = _reviewActions.GetStatusText();
        }

        void OnGUI()
        {
            if (!_showDesktopButtons || !_reviewActions || XRSettings.isDeviceActive) return;
            GUILayout.BeginArea(new Rect(12, 12, 420, 225), GUI.skin.box);
            GUILayout.Label(_reviewActions.GetStatusText());
            GUILayout.BeginHorizontal();
            if (Stage1ReviewActions.CaptureEnabled && GUILayout.Button("Save view (A)")) _reviewActions.BookmarkCurrentView();
            if (GUILayout.Button("Compare (B)")) _reviewActions.ToggleOriginal();
            GUILayout.EndHorizontal();
            if (Stage1ReviewActions.CaptureEnabled && GUILayout.Button(_reviewActions.IsRecording ? "Stop pose recording" : "Record poses"))
            {
                if (_reviewActions.IsRecording) _reviewActions.StopRecording();
                else _reviewActions.StartRecording();
            }
            GUILayout.EndArea();
        }
    }
}
