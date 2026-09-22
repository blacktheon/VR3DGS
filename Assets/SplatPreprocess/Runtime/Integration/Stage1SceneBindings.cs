using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace SplatPreprocess
{
    public sealed class Stage1SceneBindings : MonoBehaviour, IStage1ViewProvider
    {
        [SerializeField] Camera _headCamera;
        [SerializeField] Transform _sourceRoot;
        [SerializeField] Transform _trackingOrigin;
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        public Camera HeadCamera => _headCamera;
        public Transform SourceRoot => _sourceRoot;

        public void Bind(Camera headCamera, Transform sourceRoot, Transform trackingOrigin)
        {
            _headCamera = headCamera;
            _sourceRoot = sourceRoot;
            _trackingOrigin = trackingOrigin;
        }

        public bool TryCaptureViews(out ViewSample sample)
        {
            sample = null;
            if (!_headCamera || !_sourceRoot || !_headCamera.isActiveAndEnabled) return false;
            var origin = _trackingOrigin ? _trackingOrigin : _headCamera.transform.root;
            var head = _headCamera.transform;
            var stereo = _headCamera.stereoEnabled ||
                (XRSettings.isDeviceActive && _headCamera.stereoTargetEye != StereoTargetEyeMask.None);
            if (!Usable(_headCamera.worldToCameraMatrix) || !Usable(_headCamera.projectionMatrix)) return false;
            ViewRecord[] views;
            if (stereo)
            {
                if (!TryCaptureStereo(out views)) return false;
            }
            else views = new[] { View("mono", _headCamera.worldToCameraMatrix, _headCamera.projectionMatrix,
                _headCamera.pixelWidth, _headCamera.pixelHeight) };
            sample = new ViewSample
            {
                timestamp_seconds = Time.realtimeSinceStartupAsDouble,
                utc_time = DateTime.UtcNow.ToString("O"),
                tracking_origin_id = HierarchyPath(origin),
                head_position = new[] { head.position.x, head.position.y, head.position.z },
                head_rotation = new[] { head.rotation.x, head.rotation.y, head.rotation.z, head.rotation.w },
                head_world_to_camera = ViewSample.RowMajor(_headCamera.worldToCameraMatrix),
                head_projection = ViewSample.RowMajor(_headCamera.projectionMatrix),
                source_to_world = ViewSample.RowMajor(_sourceRoot.localToWorldMatrix),
                tracking_origin_to_world = ViewSample.RowMajor(origin.localToWorldMatrix),
                views = views
            };
            sample.Validate();
            return true;
        }

        bool TryCaptureStereo(out ViewRecord[] views)
        {
            views = null;
            SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
            {
                if (!display.running) continue;
                // URP sets camera stereo matrices during rendering, after LateUpdate. The XR display
                // owns the actual per-eye matrices and works with both single-pass and multipass.
                var eyes = new ViewRecord[2];
                var count = 0;
                for (var passIndex = 0; passIndex < display.GetRenderPassCount() && count < 2; passIndex++)
                {
                    display.GetRenderPass(passIndex, out var pass);
                    for (var eyeIndex = 0; eyeIndex < pass.GetRenderParameterCount() && count < 2; eyeIndex++)
                    {
                        pass.GetRenderParameter(_headCamera, eyeIndex, out var eye);
                        if (!Usable(eye.view) || !Usable(eye.projection) || eye.viewport.width <= 0 || eye.viewport.height <= 0)
                            return false;
                        eyes[count] = View(count == 0 ? "left" : "right", eye.view, eye.projection,
                            Mathf.RoundToInt(eye.viewport.width), Mathf.RoundToInt(eye.viewport.height));
                        count++;
                    }
                }
                if (count != 2) return false;
                views = eyes;
                return true;
            }
            return false;
        }

        static bool Usable(Matrix4x4 matrix)
        {
            for (var i = 0; i < 16; i++)
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i])) return false;
            return Mathf.Abs(matrix.determinant) > 1e-10f;
        }

        ViewRecord View(string label, Matrix4x4 view, Matrix4x4 projection, int width, int height) => new ViewRecord
        {
            eye = label, world_to_camera = ViewSample.RowMajor(view), projection = ViewSample.RowMajor(projection),
            width = Math.Max(1, width), height = Math.Max(1, height), near_clip = _headCamera.nearClipPlane, far_clip = _headCamera.farClipPlane
        };

        static string HierarchyPath(Transform current)
        {
            var path = current.name;
            while (current.parent) { current = current.parent; path = current.name + "/" + path; }
            return path;
        }
    }
}
