using System;
using System.IO;
using Gsplat;
using UnityEngine;

namespace SplatPreprocess
{
    [ExecuteAlways, DefaultExecutionOrder(-2000)]
    public sealed class Stage1SelectionController : MonoBehaviour
    {
        [SerializeField] GsplatRenderer _renderer;
        [SerializeField] string _rankManifestPath;
        [SerializeField] string _expectedSourceHash;
        [SerializeField] string _expectedSceneHash;

        uint[] _frozenRank;
        GsplatAsset _loadedAsset;
        GsplatResource _selectionResource;
        string _reportedPreviewError;

        public Stage1ReviewState State { get; private set; }
        public bool IsSessionActive { get; private set; }
        public GsplatRenderer Renderer => _renderer;
        public string RankManifestPath => _rankManifestPath;
        public string LastPreviewError { get; private set; } = string.Empty;

        public void Bind(GsplatRenderer renderer, string manifestPath, string sourceHash, string sceneHash)
        {
            RequireBetweenSessions();
            if (State != null) State.Changed -= ApplySelection;
            if (_renderer && _renderer != renderer) _renderer.ClearStage1Selection();
            _renderer = renderer;
            _rankManifestPath = manifestPath;
            _expectedSourceHash = sourceHash;
            _expectedSceneHash = sceneHash;
            State = null;
            _frozenRank = null;
            _loadedAsset = null;
            _selectionResource = null;
            LastPreviewError = string.Empty;
            _reportedPreviewError = null;
        }

        public void LoadConfiguredRank()
        {
            RequireBetweenSessions();
            if (string.IsNullOrWhiteSpace(_rankManifestPath)) throw new InvalidOperationException("Assign the completed ranking manifest before review");
            var path = _rankManifestPath;
            if (!Path.IsPathRooted(path))
            {
#if UNITY_EDITOR
                path = Path.Combine(Application.dataPath, "..", path);
#else
                path = Path.Combine(Application.persistentDataPath, path);
#endif
            }
            var rank = Stage1RankLoader.Load(path, _expectedSourceHash, _expectedSceneHash);
            LoadRank(rank.Manifest, rank.Order);
        }

        public void LoadRank(RankManifest manifest, uint[] order)
        {
            RequireBetweenSessions();
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.Validate(_expectedSourceHash, _expectedSceneHash);
            Stage1RankLoader.ValidateOrder(manifest, order);
            if (!_renderer || !_renderer.GsplatAsset) throw new InvalidOperationException("Assign the Stage 1 renderer and its complete source asset");
            if (!(_renderer.GsplatAsset is GsplatAssetUncompressed) || _renderer.GsplatAsset.SplatCount != manifest.source_count)
                throw new InvalidDataException("Review requires the unchanged, uncompressed original-row-order source asset with all source rows");
            // Renderer disable releases the GPU rank, but a review session keeps the exact
            // validated CPU rank and source identity for a later preview restoration.
            var frozenRank = (uint[])order.Clone();
            _renderer.SetStage1Rank(frozenRank);
            if (State != null) State.Changed -= ApplySelection;
            _frozenRank = frozenRank;
            _loadedAsset = _renderer.GsplatAsset;
            _selectionResource = _renderer.GsplatResource;
            State = new Stage1ReviewState(manifest.source_count, manifest.eligible_count, manifest.source_hash, manifest.scene_hash, manifest.rank_id);
            State.Changed += ApplySelection;
            ApplySelection();
        }

        public void BeginSession()
        {
            if (State == null) throw new InvalidOperationException("Load a validated ranking before starting review");
            IsSessionActive = true;
        }

        public void EndSession() => IsSessionActive = false;

        public void SetKeepCount(int count)
        {
            if (State == null) throw new InvalidOperationException("No ranking is loaded");
            if (count < 0 || count > State.EligibleCount) throw new ArgumentOutOfRangeException(nameof(count));
            if (EnsurePreviewSelection()) _renderer.SetStage1KeepCount(count);
        }

        void ApplySelection() => SetKeepCount(State.DisplayedCount);

        /// <summary>
        /// Restore this session's frozen rank before the next renderer Update. A disabled
        /// preview stays released; recovery never loads another rank or replaces review state.
        /// </summary>
        public bool EnsurePreviewSelection()
        {
            if (State == null) return false;
            try
            {
                if (!_renderer || !_loadedAsset || _renderer.GsplatAsset != _loadedAsset ||
                    _loadedAsset.SplatCount != State.SourceCount)
                    throw new InvalidDataException("The preview source asset changed; restore the loaded source or end the session before loading another rank");
                if (_frozenRank == null || _frozenRank.Length != State.EligibleCount)
                    throw new InvalidDataException("The frozen review rank is unavailable; end the session before loading another rank");
                if (!_renderer.isActiveAndEnabled) return false;

                if (!_renderer.HasStage1Selection || !ReferenceEquals(_selectionResource, _renderer.GsplatResource))
                {
                    _renderer.SetStage1Rank(_frozenRank);
                    _renderer.SetStage1KeepCount(State.DisplayedCount);
                    _selectionResource = _renderer.GsplatResource;
                }
                LastPreviewError = string.Empty;
                _reportedPreviewError = null;
                return true;
            }
            catch (Exception error)
            {
                LastPreviewError = error.Message;
                // Prevent a failed restoration from falling through to an ordinary all-N draw.
                if (_renderer && _renderer.enabled) _renderer.enabled = false;
                throw;
            }
        }

        void Update()
        {
            try { EnsurePreviewSelection(); }
            catch (Exception error)
            {
                if (_reportedPreviewError == error.Message) return;
                _reportedPreviewError = error.Message;
                Debug.LogError("Stage 1 preview recovery: " + error.Message, this);
            }
        }

        void RequireBetweenSessions()
        {
            if (IsSessionActive) throw new InvalidOperationException("The ranking is frozen for this review session; end the session before loading another rank");
        }

        void OnDestroy()
        {
            if (State != null) State.Changed -= ApplySelection;
        }
    }
}
