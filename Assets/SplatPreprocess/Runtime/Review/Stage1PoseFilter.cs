using UnityEngine;

namespace SplatPreprocess
{
    /// <summary>At most ten ordinary samples per second, preserving changes to replay identity and projection.</summary>
    public sealed class Stage1PoseFilter
    {
        ViewSample previous;
        public double MinimumIntervalSeconds { get; set; } = 0.1;
        public float MinimumPositionDelta { get; set; } = 0.01f;
        public float MinimumRotationDegrees { get; set; } = 1f;

        public bool ShouldRecord(ViewSample sample)
        {
            sample.Validate();
            if (previous != null)
            {
                if (sample.timestamp_seconds - previous.timestamp_seconds < MinimumIntervalSeconds) return false;
                var moved = Vector3.Distance(Position(sample), Position(previous)) >= MinimumPositionDelta;
                var rotated = Quaternion.Angle(Rotation(sample), Rotation(previous)) >= MinimumRotationDegrees;
                if (!moved && !rotated && SameSettings(previous, sample)) return false;
            }
            previous = sample.Copy();
            return true;
        }

        static Vector3 Position(ViewSample view) => new Vector3(view.head_position[0], view.head_position[1], view.head_position[2]);
        static Quaternion Rotation(ViewSample view) => new Quaternion(view.head_rotation[0], view.head_rotation[1], view.head_rotation[2], view.head_rotation[3]);

        static bool SameSettings(ViewSample a, ViewSample b)
        {
            if (a.tracking_origin_id != b.tracking_origin_id || !Same(a.head_projection, b.head_projection) ||
                !Same(a.source_to_world, b.source_to_world) || !Same(a.tracking_origin_to_world, b.tracking_origin_to_world) ||
                a.views.Length != b.views.Length) return false;
            for (var i = 0; i < a.views.Length; i++)
            {
                var av = a.views[i]; var bv = b.views[i];
                if (av.eye != bv.eye || av.width != bv.width || av.height != bv.height ||
                    av.near_clip != bv.near_clip || av.far_clip != bv.far_clip || !Same(av.projection, bv.projection)) return false;
            }
            return true;
        }

        static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (Mathf.Abs(a[i] - b[i]) > 0.000001f) return false;
            return true;
        }
    }
}
