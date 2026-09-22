using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SplatPreprocess
{
    [Serializable]
    public sealed class Stage1SessionMetadata
    {
        public int schema_version = 1;
        public string session_id, created_utc, source_hash, scene_hash, rank_id;
        public int source_count, eligible_count;
        public string coordinate_profile = "RUB_to_RUF_once";
        public string matrix_layout = "row_major";
        public string model_matrix_semantics = "source_to_world is imported-model localToWorldMatrix; apply import conversion once before it";
        public string view_matrix_convention = "Unity Camera.worldToCameraMatrix; camera forward is negative Z";
        public string time_basis = "Unity realtimeSinceStartupAsDouble seconds; monotonically increasing within this session";
        public string membership = "development";
    }

    /// <summary>One session owns append-only pose and bookmark streams. Call Stop on session exit.</summary>
    public sealed class Stage1SessionRecorder : IDisposable
    {
        readonly Stage1ReviewState state;
        StreamWriter poses, marks;
        double lastPoseTimestamp = double.NegativeInfinity;
        public string DirectoryPath { get; }
        public bool IsOpen => poses != null;

        public Stage1SessionRecorder(string directory, Stage1ReviewState state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            DirectoryPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(DirectoryPath);
            var metadata = new Stage1SessionMetadata
            {
                session_id = Path.GetFileName(DirectoryPath), created_utc = DateTime.UtcNow.ToString("O"),
                source_hash = state.SourceHash, scene_hash = state.SceneHash, rank_id = state.FrozenRankId,
                source_count = state.SourceCount, eligible_count = state.EligibleCount
            };
            try
            {
                using (var output = NewWriter("metadata.json")) output.WriteLine(JsonUtility.ToJson(metadata, true));
                poses = NewWriter("session.jsonl");
                marks = NewWriter("marks.jsonl");
            }
            catch { Stop(); throw; }
        }

        StreamWriter NewWriter(string file) => new StreamWriter(
            new FileStream(Path.Combine(DirectoryPath, file), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 65536);

        public void AppendPose(ViewSample sample)
        {
            RequireOpen();
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            sample.Validate();
            if (sample.timestamp_seconds <= lastPoseTimestamp) throw new InvalidDataException("Ordinary pose timestamps must increase");
            poses.WriteLine(JsonUtility.ToJson(sample));
            lastPoseTimestamp = sample.timestamp_seconds;
        }

        public void AppendMark(MarkRecord mark)
        {
            RequireOpen();
            if (mark == null || mark.view == null) throw new ArgumentException("Bookmark view is missing", nameof(mark));
            if (mark.source_hash != state.SourceHash || mark.scene_hash != state.SceneHash || mark.rank_id != state.FrozenRankId ||
                mark.source_count != state.SourceCount || mark.eligible_count != state.EligibleCount)
                throw new InvalidDataException("Bookmark identity does not match this frozen session");
            if (mark.candidate_centi_percent < 0 || mark.candidate_centi_percent > 10000 ||
                (mark.display_mode != "original" && mark.display_mode != "candidate") ||
                mark.candidate_count < 0 || mark.candidate_count > state.EligibleCount ||
                (mark.exact_candidate_count
                    ? mark.candidate_centi_percent != (state.EligibleCount == 0 ? 0 :
                        (int)Math.Round(mark.candidate_count * 10000.0 / state.EligibleCount, MidpointRounding.AwayFromZero))
                    : mark.candidate_count != Stage1Counts.KeepCount(state.EligibleCount, mark.candidate_centi_percent)) ||
                mark.displayed_count != (mark.display_mode == "original" ? state.EligibleCount : mark.candidate_count) ||
                float.IsNaN(mark.priority) || mark.priority < 1 || mark.priority > 3)
                throw new InvalidDataException("Bookmark count, mode or bounded priority is invalid");
            mark.view.Validate();
            marks.WriteLine(JsonUtility.ToJson(mark));
            // A confirmed bookmark is durable immediately; ordinary poses are buffered and flushed periodically.
            marks.Flush();
        }

        public void Flush() { poses?.Flush(); marks?.Flush(); }

        public void Stop()
        {
            try { poses?.Dispose(); }
            finally { poses = null; marks?.Dispose(); marks = null; }
        }

        public void Dispose() => Stop();

        void RequireOpen()
        {
            if (!IsOpen) throw new InvalidOperationException("The review session has ended");
        }
    }
}
