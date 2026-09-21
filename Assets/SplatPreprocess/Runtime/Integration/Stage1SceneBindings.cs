using System;
using UnityEngine;
using UnityEngine.XR;

namespace SplatPreprocess
{
    public sealed class Stage1SceneBindings : MonoBehaviour, IStage1ViewProvider
    {
        [SerializeField] Camera _headCamera;
        [SerializeField] Transform _sourceRoot;
        [SerializeField] Transform _trackingOrigin;

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
            if (!_headCamera || !_sourceRoot) return false;
            var origin = _trackingOrigin ? _trackingOrigin : _headCamera.transform.root;
            var head = _headCamera.transform;
            var stereo = _headCamera.stereoEnabled;
            var width = stereo && XRSettings.eyeTextureWidth > 0 ? XRSettings.eyeTextureWidth : _headCamera.pixelWidth;
            var height = stereo && XRSettings.eyeTextureHeight > 0 ? XRSettings.eyeTextureHeight : _headCamera.pixelHeight;
            var views = stereo
                ? new[] { Eye(Camera.StereoscopicEye.Left, "left", width, height), Eye(Camera.StereoscopicEye.Right, "right", width, height) }
                : new[] { View("mono", _headCamera.worldToCameraMatrix, _headCamera.projectionMatrix, width, height) };
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

        ViewRecord Eye(Camera.StereoscopicEye eye, string label, int width, int height)
            => View(label, _headCamera.GetStereoViewMatrix(eye), _headCamera.GetStereoProjectionMatrix(eye), width, height);

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
