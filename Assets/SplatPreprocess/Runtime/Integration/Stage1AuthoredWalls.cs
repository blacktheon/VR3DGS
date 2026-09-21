using System;
using System.Collections.Generic;
using Gsplat;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SplatPreprocess
{
    /// <summary>Binds finite authored XZ planes to per-camera splat visibility, preserving gameplay collision.</summary>
    [ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(-1900)]
    public sealed class Stage1AuthoredWalls : MonoBehaviour
    {
        [SerializeField] Transform _wallsRoot;
        [SerializeField] GsplatRenderer _renderer;
        [SerializeField, Min(0)] float _rearDepth = 0.1f;

        readonly List<MeshFilter> _filters = new();
        readonly List<MeshRenderer> _visuals = new();
        readonly List<MeshRenderer> _removedVisuals = new();
        readonly Dictionary<MeshRenderer, bool> _ownedVisibility = new();
        readonly List<Snapshot> _snapshots = new();
        readonly List<Stage1Wall> _wallScratch = new();
        Stage1Wall[] _walls = Array.Empty<Stage1Wall>();
        Transform _boundRoot;
        GsplatRenderer _boundRenderer;
        GsplatAsset _previousAsset;
        bool _previousRendererActive;
        bool _forceRebuild = true, _namesDirty = true, _layoutValid, _uploadPending = true;
        float _previousDepth = float.NaN;
        string _reportedError;

        struct Snapshot
        {
            public MeshFilter Filter;
            public Mesh Mesh;
            public Matrix4x4 LocalToWorld;
            public Bounds Bounds;
            public Vector3 Position, Normal;
            public bool Active, Floor;
            public string Name;

            public bool SameGeometry(Snapshot other) => Filter == other.Filter && Mesh == other.Mesh &&
                Active == other.Active && Floor == other.Floor && LocalToWorld.Equals(other.LocalToWorld) &&
                Bounds.Equals(other.Bounds) && Position.Equals(other.Position) && Normal.Equals(other.Normal);
        }

        public Transform WallsRoot => _wallsRoot;
        public GsplatRenderer Renderer => _renderer;
        public IReadOnlyList<Stage1Wall> Walls => _walls;
        public int WallCount => _walls.Length;
        public string LastError { get; private set; } = string.Empty;
        public float RearDepth
        {
            get => _rearDepth;
            set { if (_rearDepth.Equals(value)) return; _rearDepth = value; _forceRebuild = true; }
        }

        public void Bind(Transform wallsRoot, GsplatRenderer renderer)
        {
            if (!wallsRoot) throw new ArgumentNullException(nameof(wallsRoot));
            if (!renderer) throw new ArgumentNullException(nameof(renderer));
            _wallsRoot = wallsRoot;
            _renderer = renderer;
            ApplyNow();
        }

        /// <summary>Revalidate and apply immediately; invalid layouts throw after clearing the wall mask.</summary>
        public void ApplyNow()
        {
            _forceRebuild = true;
            _namesDirty = true;
            Refresh(true);
        }

        void OnEnable()
        {
            _forceRebuild = true;
            _namesDirty = true;
#if UNITY_EDITOR
            EditorApplication.hierarchyChanged += InvalidateHierarchy;
            Undo.undoRedoPerformed += InvalidateHierarchy;
#endif
            Refresh(false);
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.hierarchyChanged -= InvalidateHierarchy;
            Undo.undoRedoPerformed -= InvalidateHierarchy;
#endif
            RestoreVisibility();
            ClearTarget(_boundRenderer);
            _walls = Array.Empty<Stage1Wall>();
            _snapshots.Clear();
            _layoutValid = false;
            _uploadPending = true;
            _forceRebuild = true;
        }

        void OnValidate() { _forceRebuild = true; _namesDirty = true; }
        void InvalidateHierarchy() { _namesDirty = true; }
        void Update() => Refresh(false);

        void Refresh(bool throwOnFailure)
        {
            if (!isActiveAndEnabled) return;
            if (_boundRoot != _wallsRoot || _boundRenderer != _renderer)
            {
                RestoreVisibility();
                ClearTarget(_boundRenderer);
                _boundRoot = _wallsRoot;
                _boundRenderer = _renderer;
                _snapshots.Clear();
                _walls = Array.Empty<Stage1Wall>();
                _layoutValid = false;
                _forceRebuild = true;
                _namesDirty = true;
                _uploadPending = true;
                _previousAsset = null;
            }
            if (!_wallsRoot)
            {
                RestoreVisibility();
                if (!_renderer && !throwOnFailure) return; // An unconfigured new component is inert.
                Fail("Assign the authored Walls root.", throwOnFailure);
                return;
            }

            _visuals.Clear();
            _wallsRoot.GetComponentsInChildren(true, _visuals);
            OwnVisibility();
            if (!_renderer)
            {
                Fail("Assign the Stage 1 splat renderer.", throwOnFailure);
                return;
            }

            _filters.Clear();
            _wallsRoot.GetComponentsInChildren(true, _filters);
            bool changed = CaptureGeometry();
            bool rendererActive = _renderer.isActiveAndEnabled;
            var asset = _renderer.GsplatAsset;
            if (rendererActive != _previousRendererActive || asset != _previousAsset)
            {
                _uploadPending = true;
                if (!_layoutValid) changed = true;
                _previousRendererActive = rendererActive;
                _previousAsset = asset;
            }

            if (changed)
            {
                try
                {
                    CompileWalls();
                    LastError = string.Empty;
                    _reportedError = null;
                    _layoutValid = true;
                    _uploadPending = true;
                }
                catch (Exception error)
                {
                    Fail(error.Message, throwOnFailure);
                    return;
                }
            }
            if (!_layoutValid) return;

            // Authoring remains CPU-only while the hidden CUDA worker owns the disabled preview.
            // GsplatRenderer.SetStage1Walls may create source GPU resources, so never call it here.
            if (!rendererActive || !asset)
            {
                _uploadPending = true;
                return;
            }
            try
            {
                if (_walls.Length == 0)
                {
                    ClearTarget(_renderer);
                    _uploadPending = false;
                }
                else if (_uploadPending || !_renderer.HasStage1Walls)
                {
                    _renderer.SetStage1Walls(_walls);
                    _uploadPending = false;
                }
            }
            catch (Exception error) { Fail(error.Message, throwOnFailure); }
        }

        bool CaptureGeometry()
        {
            bool changed = _forceRebuild || !_rearDepth.Equals(_previousDepth) || _snapshots.Count != _filters.Count;
            _forceRebuild = false;
            _previousDepth = _rearDepth;
            for (int i = 0; i < _filters.Count; i++)
            {
                var filter = _filters[i];
                bool sameSlot = i < _snapshots.Count && _snapshots[i].Filter == filter;
                var transform = filter.transform;
                var mesh = filter.sharedMesh;
                // Unity's Object.name getter creates a string. Refresh names only on hierarchy
                // changes or ApplyNow; stable per-frame transform checks allocate no new strings.
                string name = sameSlot && !_namesDirty ? _snapshots[i].Name : filter.gameObject.name;
                var snapshot = new Snapshot
                {
                    Filter = filter, Mesh = mesh, LocalToWorld = transform.localToWorldMatrix,
                    Bounds = mesh ? mesh.bounds : default, Position = transform.position, Normal = transform.up,
                    Active = filter.gameObject.activeInHierarchy, Floor = name == "Floor", Name = name
                };
                if (!sameSlot || !snapshot.SameGeometry(_snapshots[i])) changed = true;
                if (i < _snapshots.Count) _snapshots[i] = snapshot;
                else _snapshots.Add(snapshot);
            }
            if (_snapshots.Count > _filters.Count) _snapshots.RemoveRange(_filters.Count, _snapshots.Count - _filters.Count);
            _namesDirty = false;
            return changed;
        }

        void CompileWalls()
        {
            if (!Finite(_rearDepth) || _rearDepth < 0) throw new InvalidOperationException("Wall rear depth must be finite and nonnegative.");
            _wallScratch.Clear();
            foreach (var snapshot in _snapshots)
            {
                if (!snapshot.Active || snapshot.Floor) continue;
                if (!snapshot.Mesh || !snapshot.Filter.GetComponent<MeshRenderer>())
                    throw new InvalidOperationException(snapshot.Name + ": each wall requires a MeshFilter mesh and MeshRenderer.");
                var bounds = snapshot.Bounds;
                var min = bounds.min;
                var max = bounds.max;
                if (!Finite(min) || !Finite(max) || max.x <= min.x || max.z <= min.z ||
                    Mathf.Abs(min.y) > 1e-5f || Mathf.Abs(max.y) > 1e-5f)
                    throw new InvalidOperationException(snapshot.Name + ": only finite, flat local XZ rectangles at local Y=0 are supported.");
                var matrix = snapshot.LocalToWorld;
                for (int j = 0; j < 16; j++)
                    if (!Finite(matrix[j])) throw new InvalidOperationException(snapshot.Name + ": wall transform is not finite.");
                if (!Finite(matrix.determinant) || Mathf.Abs(matrix.determinant) < 1e-12f || !Finite(snapshot.Normal) ||
                    snapshot.Normal.sqrMagnitude < 0.999f)
                    throw new InvalidOperationException(snapshot.Name + ": wall transform must be invertible with a valid front direction.");
                Vector3 normal = snapshot.Normal.normalized;
                Vector3 xAxis = matrix.MultiplyVector(Vector3.right), zAxis = matrix.MultiplyVector(Vector3.forward);
                if (Mathf.Abs(Vector3.Dot(normal, xAxis.normalized)) > 1e-4f ||
                    Mathf.Abs(Vector3.Dot(normal, zAxis.normalized)) > 1e-4f)
                    throw new InvalidOperationException(snapshot.Name + ": sheared transforms whose up direction is not the plane normal are unsupported.");
                if (_wallScratch.Count >= 16) throw new InvalidOperationException("At most 16 active non-Floor authored walls are supported.");
                _wallScratch.Add(new Stage1Wall
                {
                    WorldToLocal = matrix.inverse,
                    BoundsMinMaxXZ = new Vector4(min.x, max.x, min.z, max.z),
                    WorldPlane = new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, snapshot.Position)),
                    RearDepth = _rearDepth
                });
            }
            _walls = _wallScratch.Count == 0 ? Array.Empty<Stage1Wall>() : _wallScratch.ToArray();
        }

        void OwnVisibility()
        {
            _removedVisuals.Clear();
            foreach (var entry in _ownedVisibility)
                if (!entry.Key || !_visuals.Contains(entry.Key)) _removedVisuals.Add(entry.Key);
            foreach (var visual in _removedVisuals)
            {
                if (visual) visual.forceRenderingOff = _ownedVisibility[visual];
                _ownedVisibility.Remove(visual);
            }
            foreach (var visual in _visuals)
            {
                if (!_ownedVisibility.ContainsKey(visual)) _ownedVisibility.Add(visual, visual.forceRenderingOff);
                if (!visual.forceRenderingOff) visual.forceRenderingOff = true;
            }
        }

        void RestoreVisibility()
        {
            foreach (var entry in _ownedVisibility)
                if (entry.Key) entry.Key.forceRenderingOff = entry.Value;
            _ownedVisibility.Clear();
        }

        static void ClearTarget(GsplatRenderer renderer)
        {
            if (renderer && renderer.HasStage1Walls) renderer.ClearStage1Walls();
        }

        void Fail(string message, bool throwOnFailure)
        {
            _walls = Array.Empty<Stage1Wall>();
            _layoutValid = false;
            _uploadPending = true;
            LastError = message;
            ClearTarget(_renderer);
            if (throwOnFailure) throw new InvalidOperationException(message);
            if (_reportedError == message) return;
            _reportedError = message;
            Debug.LogError("Stage 1 authored walls: " + message, this);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
