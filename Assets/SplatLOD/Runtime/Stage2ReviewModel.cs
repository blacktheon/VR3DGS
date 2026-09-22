using System;
using Gsplat;
using UnityEngine;
using UnityEngine.XR;

namespace SplatLOD
{
    public sealed class Stage2ButtonEdges
    {
        bool held;
        public bool Sample(bool pressed) { bool down = pressed && !held; held = pressed; return down; }
    }

    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class Stage2ReviewModel : MonoBehaviour
    {
        [SerializeField] TextAsset _layoutJson;
        [SerializeField] GsplatRenderer _source;
        [SerializeField] string _acceptedPlyHash;
        [SerializeField] Material _lineMaterial;
        [SerializeField] bool _boxesVisible;
        [SerializeField] bool _pollRightA = true;
        [SerializeField] bool _showDesktopControls = true;
        Mesh wireMesh;
        Stage2ButtonEdges edges = new();
        public Stage2Layout Layout { get; private set; }
        public string LastError { get; private set; } = "";
        public bool BoxesVisible => _boxesVisible;
        public GsplatRenderer Source => _source;
        public TextAsset LayoutAsset => _layoutJson;
        public string AcceptedPlyHash => _acceptedPlyHash;

        public void Configure(GsplatRenderer source, TextAsset layoutJson, string acceptedPlyHash, Material lineMaterial)
        {
            if (!source || !source.GsplatAsset || !layoutJson || !lineMaterial) throw new ArgumentException("Assign accepted LOD0, layout and wire material");
            Stage2Layout.Parse(layoutJson.text, acceptedPlyHash, (int)source.GsplatAsset.SplatCount);
            _source = source; _layoutJson = layoutJson; _acceptedPlyHash = acceptedPlyHash; _lineMaterial = lineMaterial;
            RebuildOverlay();
        }

        public void SetBoxesVisible(bool visible)
        {
            _boxesVisible = visible;
            GetComponent<MeshRenderer>().enabled = visible && wireMesh && Layout != null;
        }
        public void ToggleBoxes() => SetBoxesVisible(!_boxesVisible);

        public void RebuildOverlay()
        {
            if (!_source || !_source.GsplatAsset || !_layoutJson || !_lineMaterial) return;
            var layout = Stage2Layout.Parse(_layoutJson.text, _acceptedPlyHash, (int)_source.GsplatAsset.SplatCount);
            var vertices = new Vector3[layout.chunks.Length * 8];
            var indices = new int[layout.chunks.Length * 24];
            int[] edges = { 0,1, 0,2, 0,4, 1,3, 1,5, 2,3, 2,6, 3,7, 4,5, 4,6, 5,7, 6,7 };
            for (int i = 0; i < layout.chunks.Length; ++i)
            {
                var bounds = layout.chunks[i].PartitionBounds;
                for (int corner = 0; corner < 8; ++corner)
                    vertices[i*8+corner] = new Vector3((corner&1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner&2) == 0 ? bounds.min.y : bounds.max.y, (corner&4) == 0 ? bounds.min.z : bounds.max.z);
                for (int edge = 0; edge < 24; ++edge) indices[i*24+edge] = i*8+edges[edge];
            }
            ReleaseMesh();
            wireMesh = new Mesh { name = "Stage2 occupied chunk bounds", hideFlags = HideFlags.DontSave,
                indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            wireMesh.vertices = vertices;
            wireMesh.SetIndices(indices, MeshTopology.Lines, 0, true);
            GetComponent<MeshFilter>().sharedMesh = wireMesh;
            var wire = GetComponent<MeshRenderer>();
            wire.sharedMaterial = _lineMaterial;
            wire.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wire.receiveShadows = false;
            Layout = layout; LastError = "";
            SetBoxesVisible(_boxesVisible);
        }

        void OnEnable()
        {
            edges = new Stage2ButtonEdges();
            try { RebuildOverlay(); }
            catch (Exception error) { LastError = error.Message; GetComponent<MeshRenderer>().enabled = false; Debug.LogError("Stage2 layout: " + error.Message, this); }
        }
        void OnDisable() { GetComponent<MeshRenderer>().enabled = false; ReleaseMesh(); }
        void ReleaseMesh()
        {
            if (!wireMesh) return;
            if (Application.isPlaying) Destroy(wireMesh); else DestroyImmediate(wireMesh);
            wireMesh = null;
        }
        void Update()
        {
            if (!Application.isPlaying || !_pollRightA) return;
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool pressed = right.isValid && right.TryGetFeatureValue(CommonUsages.primaryButton, out bool button) && button;
            if (edges.Sample(pressed)) ToggleBoxes();
        }
        void OnGUI()
        {
            if (!Application.isPlaying || !_showDesktopControls || XRSettings.isDeviceActive) return;
            GUILayout.BeginArea(new Rect(12, 12, 420, 125), GUI.skin.box);
            GUILayout.Label("Stage 2 · Accepted LOD0");
            GUILayout.Label(Layout == null ? LastError : $"{Layout.count:N0} splats · {Layout.chunks.Length} chunks · 6 textured surfaces");
            if (GUILayout.Button(_boxesVisible ? "Hide chunk boxes (A)" : "Show chunk boxes (A)")) ToggleBoxes();
            GUILayout.Label("LOD1–LOD3 generation is the next development milestone.");
            GUILayout.EndArea();
        }
    }
}
