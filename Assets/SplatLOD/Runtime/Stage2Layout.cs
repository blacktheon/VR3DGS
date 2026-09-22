using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplatLOD
{
    [Serializable]
    public sealed class Stage2ChunkData
    {
        public string id, group;
        public int count, offset, depth;
        public float[] partition_min, partition_max, render_min, render_max;
        public int[] available_lods;
        public Bounds PartitionBounds => Stage2Layout.BoundsFrom(partition_min, partition_max);
        public Bounds RenderBounds => Stage2Layout.BoundsFrom(render_min, render_max);
    }

    [Serializable]
    public sealed class Stage2Layout
    {
        public int schema_version, count;
        public string layout_id, input_id, input_ply_sha256, coordinate_space, calibration_status;
        public string input_manifest, input_manifest_sha256, ownership_path, ownership_sha256, members_path, members_sha256;
        public int[] available_lods;
        public float[] model_local_to_world;
        public Stage2ChunkData[] chunks;

        public static Stage2Layout Parse(string json, string expectedPlyHash, int expectedCount)
        {
            var value = JsonUtility.FromJson<Stage2Layout>(json);
            if (value == null || value.schema_version != 1 || value.count != expectedCount || expectedCount <= 0 ||
                string.IsNullOrWhiteSpace(value.layout_id) || string.IsNullOrWhiteSpace(value.input_id) ||
                value.coordinate_space != "renderer_local_RUF" || !IsHash(expectedPlyHash) || value.input_ply_sha256 != expectedPlyHash ||
                !OnlyLodZero(value.available_lods) || value.chunks == null || value.chunks.Length == 0)
                throw new ArgumentException("Chunk layout does not match this accepted LOD0 export");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var chunk in value.chunks)
            {
                if (chunk == null || string.IsNullOrWhiteSpace(chunk.id) || !ids.Add(chunk.id) ||
                    chunk.id.IndexOfAny(new[] { '/', '\\' }) >= 0 || string.IsNullOrWhiteSpace(chunk.group) ||
                    chunk.count <= 0 || chunk.offset != total || !OnlyLodZero(chunk.available_lods))
                    throw new ArgumentException("Invalid chunk identity, coverage, or LOD availability");
                CheckBounds(chunk.partition_min, chunk.partition_max);
                CheckBounds(chunk.render_min, chunk.render_max);
                total += chunk.count;
                if (total > expectedCount) throw new ArgumentException("Chunk coverage exceeds the accepted count");
            }
            if (total != expectedCount) throw new ArgumentException("Chunk coverage misses accepted splats");
            return value;
        }

        static bool OnlyLodZero(int[] levels) => levels != null && levels.Length == 1 && levels[0] == 0;
        public static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        static void CheckBounds(float[] min, float[] max)
        {
            if (min == null || max == null || min.Length != 3 || max.Length != 3)
                throw new ArgumentException("Bounds require three axes");
            for (int axis = 0; axis < 3; axis++)
                if (!float.IsFinite(min[axis]) || !float.IsFinite(max[axis]) || min[axis] >= max[axis])
                    throw new ArgumentException("Bounds must be finite and increasing");
        }
        public static Bounds BoundsFrom(float[] min, float[] max)
        {
            CheckBounds(min, max);
            var result = new Bounds();
            result.SetMinMax(new Vector3(min[0], min[1], min[2]), new Vector3(max[0], max[1], max[2]));
            return result;
        }
    }
}
