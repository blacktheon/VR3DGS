using UnityEngine;

namespace SplatLOD
{
    /// <summary>Ownership metadata; all LOD0 rows share one renderer and one transparent depth sort.</summary>
    [DisallowMultipleComponent]
    public sealed class Stage2Chunk : MonoBehaviour
    {
        [SerializeField] string _chunkId, _layoutId, _inputId;
        [SerializeField] int _count, _memberOffset;
        [SerializeField] Bounds _partitionBounds, _renderBounds;
        public string ChunkId => _chunkId;
        public string LayoutId => _layoutId;
        public string InputId => _inputId;
        public int Count => _count;
        public int MemberOffset => _memberOffset;
        public Bounds PartitionBounds => _partitionBounds;
        public Bounds RenderBounds => _renderBounds;
        public int DisplayedLod => 0;

        public void Configure(Stage2Layout layout, Stage2ChunkData chunk)
        {
            _chunkId = chunk.id; _layoutId = layout.layout_id; _inputId = layout.input_id;
            _count = chunk.count; _memberOffset = chunk.offset;
            _partitionBounds = chunk.PartitionBounds; _renderBounds = chunk.RenderBounds;
        }
    }
}
