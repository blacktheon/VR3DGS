using System;
using UnityEngine;

namespace SplatPreprocess
{
    /// <summary>Observes the existing physical slider's parent-space X translation; never moves its handle.</summary>
    [DefaultExecutionOrder(9990)]
    public sealed class Stage1PhysicalSliderBinding : MonoBehaviour
    {
        [SerializeField] Stage1ReviewActions _reviewActions;
        [SerializeField] Transform _sliderTransform;
        [SerializeField] float _minimumLocalX = -0.06f;
        [SerializeField] float _maximumLocalX = 0.06f;

        int lastCentiPercent = -1;
        Stage1ReviewState lastState;
        bool reportedError;

        public void Bind(Stage1ReviewActions reviewActions, Transform sliderTransform, float minimumLocalX, float maximumLocalX)
        {
            if (float.IsNaN(minimumLocalX) || float.IsNaN(maximumLocalX) || float.IsInfinity(minimumLocalX) || float.IsInfinity(maximumLocalX) || minimumLocalX >= maximumLocalX)
                throw new ArgumentException("Slider bounds must be finite and increasing");
            _reviewActions = reviewActions;
            _sliderTransform = sliderTransform;
            _minimumLocalX = minimumLocalX;
            _maximumLocalX = maximumLocalX;
            lastCentiPercent = -1;
            lastState = null;
            reportedError = false;
        }

        public void RefreshValue()
        {
            if (!_reviewActions || !_sliderTransform || !_reviewActions.IsSessionOpen) return;
            if (_minimumLocalX >= _maximumLocalX) throw new InvalidOperationException("The physical slider needs its actual minimum and maximum local X bounds");
            var normalized = Mathf.InverseLerp(_minimumLocalX, _maximumLocalX, _sliderTransform.localPosition.x);
            var centiPercent = (int)Math.Round(normalized * 10000.0, MidpointRounding.AwayFromZero);
            if (centiPercent == lastCentiPercent && lastState == _reviewActions.State) return;
            _reviewActions.SetCandidateNormalized(normalized);
            lastCentiPercent = centiPercent;
            lastState = _reviewActions.State;
        }

        void LateUpdate()
        {
            if (reportedError) return;
            try { RefreshValue(); }
            catch (Exception exception)
            {
                reportedError = true;
                Debug.LogError("Stage 1 slider binding: " + exception.Message, this);
            }
        }
    }
}
