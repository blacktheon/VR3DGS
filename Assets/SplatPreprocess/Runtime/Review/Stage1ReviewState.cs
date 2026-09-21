using System;

namespace SplatPreprocess
{
    public sealed class Stage1ReviewState
    {
        public int SourceCount { get; }
        public int EligibleCount { get; }
        public string SourceHash { get; }
        public string SceneHash { get; }
        public string FrozenRankId { get; }
        public int CandidateCentiPercent { get; private set; } = 10000;
        public bool IsOriginal { get; private set; }
        public int CandidateCount => Stage1Counts.KeepCount(EligibleCount, CandidateCentiPercent);
        public int DisplayedCount => IsOriginal ? EligibleCount : CandidateCount;
        public string DisplayMode => IsOriginal ? "original" : "candidate";
        public event Action Changed;

        public Stage1ReviewState(int sourceCount, int eligibleCount, string sourceHash, string sceneHash, string rankId)
        {
            if (sourceCount < 0 || eligibleCount < 0 || eligibleCount > sourceCount)
                throw new ArgumentOutOfRangeException(nameof(eligibleCount));
            if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(sceneHash) || string.IsNullOrWhiteSpace(rankId))
                throw new ArgumentException("A review session requires source, scene and frozen rank identities");
            SourceCount = sourceCount;
            EligibleCount = eligibleCount;
            SourceHash = sourceHash;
            SceneHash = sceneHash;
            FrozenRankId = rankId;
        }

        public void SetCandidate(int centiPercent)
        {
            if (centiPercent < 0 || centiPercent > 10000) throw new ArgumentOutOfRangeException(nameof(centiPercent));
            if (CandidateCentiPercent == centiPercent) return;
            CandidateCentiPercent = centiPercent;
            Changed?.Invoke();
        }

        public void SetCandidateNormalized(float normalized)
        {
            if (float.IsNaN(normalized) || float.IsInfinity(normalized) || normalized < 0 || normalized > 1)
                throw new ArgumentOutOfRangeException(nameof(normalized));
            SetCandidate((int)Math.Round(normalized * 10000.0, MidpointRounding.AwayFromZero));
        }

        public void ToggleOriginal()
        {
            IsOriginal = !IsOriginal;
            Changed?.Invoke();
        }

        public MarkRecord GetBookmark(ViewSample view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            view.Validate();
            return new MarkRecord
            {
                mark_id = Guid.NewGuid().ToString("N"),
                source_hash = SourceHash,
                scene_hash = SceneHash,
                rank_id = FrozenRankId,
                source_count = SourceCount,
                eligible_count = EligibleCount,
                candidate_centi_percent = CandidateCentiPercent,
                candidate_count = CandidateCount,
                displayed_count = DisplayedCount,
                display_mode = DisplayMode,
                priority = 3f,
                view = view.Copy()
            };
        }
    }

    [Serializable]
    public sealed class MarkRecord
    {
        public int schema_version = 1;
        public string mark_id, source_hash, scene_hash, rank_id, display_mode;
        public int source_count, eligible_count, candidate_centi_percent, candidate_count, displayed_count;
        public float priority;
        public ViewSample view;
    }
}
