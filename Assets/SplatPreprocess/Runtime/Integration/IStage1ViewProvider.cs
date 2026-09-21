using System;
using System.IO;
using UnityEngine;

namespace SplatPreprocess
{
    public interface IStage1ViewProvider
    {
        bool TryCaptureViews(out ViewSample sample);
    }

    [Serializable]
    public sealed class ViewRecord
    {
        public string eye;
        public float[] world_to_camera, projection;
        public int width, height;
        public float near_clip, far_clip;

        internal ViewRecord Copy() => new ViewRecord
        {
            eye = eye, world_to_camera = (float[])world_to_camera.Clone(), projection = (float[])projection.Clone(),
            width = width, height = height, near_clip = near_clip, far_clip = far_clip
        };
    }

    /// <summary>Matrices use row-major arrays. source_to_world is the imported model's localToWorldMatrix;
    /// apply the recorded import coordinate conversion to raw PLY positions before this matrix.</summary>
    [Serializable]
    public sealed class ViewSample
    {
        public int schema_version = 1;
        public double timestamp_seconds;
        public string utc_time, tracking_origin_id;
        public float[] head_position, head_rotation, head_world_to_camera, head_projection;
        public float[] source_to_world, tracking_origin_to_world;
        public ViewRecord[] views;

        public ViewSample Copy()
        {
            var copy = (ViewSample)MemberwiseClone();
            copy.head_position = (float[])head_position.Clone();
            copy.head_rotation = (float[])head_rotation.Clone();
            copy.head_world_to_camera = (float[])head_world_to_camera.Clone();
            copy.head_projection = (float[])head_projection.Clone();
            copy.source_to_world = (float[])source_to_world.Clone();
            copy.tracking_origin_to_world = (float[])tracking_origin_to_world.Clone();
            copy.views = new ViewRecord[views.Length];
            for (var i = 0; i < views.Length; i++) copy.views[i] = views[i].Copy();
            return copy;
        }

        public void Validate()
        {
            if (double.IsNaN(timestamp_seconds) || double.IsInfinity(timestamp_seconds) || timestamp_seconds < 0)
                throw new InvalidDataException("View timestamp must be finite and monotonic");
            CheckArray(head_position, 3); CheckArray(head_rotation, 4);
            CheckArray(head_world_to_camera, 16); CheckArray(head_projection, 16);
            CheckArray(source_to_world, 16); CheckArray(tracking_origin_to_world, 16);
            if (views == null || views.Length == 0) throw new InvalidDataException("A view needs a mono camera or actual stereo eyes");
            foreach (var view in views)
            {
                if (view == null || string.IsNullOrEmpty(view.eye) || view.width <= 0 || view.height <= 0)
                    throw new InvalidDataException("View eye and resolution are required");
                CheckArray(view.world_to_camera, 16); CheckArray(view.projection, 16);
            }
        }

        static void CheckArray(float[] data, int length)
        {
            if (data == null || data.Length != length) throw new InvalidDataException("View matrix or pose has an invalid shape");
            foreach (var value in data)
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("View matrix or pose is not finite");
        }

        public static float[] RowMajor(Matrix4x4 matrix)
        {
            var result = new float[16];
            for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++)
                result[4 * row + column] = matrix[row, column];
            return result;
        }
    }
}
