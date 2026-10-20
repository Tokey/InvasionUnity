using System;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Scores each shot by horizontal distance from the tower base: full points within a short
    /// radius, then a fixed miss penalty outside it. A round that ran out with no shot scores as a
    /// miss.
    /// </summary>
    public class ScoreManager : MonoBehaviour
    {
        [Header("Scoring Curve")]
        [Tooltip("Score awarded for a hit. Also defines a hit: isHit is shotScore >= hitPoints.")]
        [SerializeField, Min(0f)] float hitPoints = 100f;
        [SerializeField, Min(0f)] float closeRadius = 1f;
        [SerializeField] float missPoints = -10f;

        public float TotalScore { get; private set; }
        public float LastShotScore { get; private set; }
        public float LastShotDistance { get; private set; }
        public int ShotCount { get; private set; }
        public float HitPoints => hitPoints;
        public float CloseRadius => closeRadius;
        public float MissPoints => missPoints;

        /// <summary>
        /// Responses this phase that landed, and responses that did not — the HUD's tally. Kept here
        /// rather than derived from the score, because the score is weighted and two different
        /// tallies can produce the same total. Practice and the main run are counted separately:
        /// ExperimentDirector calls <see cref="ResetScore"/> between them.
        /// </summary>
        public int Hits   { get; private set; }
        public int Misses { get; private set; }

        /// <summary>True for a hit, false for a miss — whichever the last scored response was.</summary>
        public bool LastWasHit { get; private set; }

        /// <summary>How the last scored round ended — a shot, or running out. The tally colours a
        /// ran-out round apart from a shot that missed.</summary>
        public TrialOutcome LastOutcome { get; private set; }

        /// <summary>(lastShotScore, totalScore, distance)</summary>
        public event Action<float, float, float> OnScored;

        public void SetScoreRadius(float newCloseRadius)
        {
            closeRadius = Mathf.Max(0f, newCloseRadius);
        }

        /// <summary>Applies the scoring columns of the study config (see StudyConfig).</summary>
        public void SetScoring(float newHitPoints, float newMissPoints)
        {
            hitPoints  = Mathf.Max(0f, newHitPoints);
            missPoints = newMissPoints;
        }

        public float ScoreShot(Vector3 hitPoint, Vector3 towerBase)
        {
            Vector3 a = new Vector3(hitPoint.x, 0f, hitPoint.z);
            Vector3 b = new Vector3(towerBase.x, 0f, towerBase.z);
            float dist = Vector3.Distance(a, b);
            LastShotDistance = dist;

            bool  hit = dist <= closeRadius;
            float pts = hit ? hitPoints : missPoints;

            LastShotScore = pts;
            TotalScore += pts;
            ShotCount += 1;
            LastOutcome = TrialOutcome.Shot;
            Tally(hit);
            OnScored?.Invoke(LastShotScore, TotalScore, dist);
            return pts;
        }

        /// <summary>Scores a round that ended with no shot — always a miss. There is no landing point,
        /// so there is no distance to record either.</summary>
        public float ScoreOutcome(TrialOutcome outcome)
        {
            LastShotDistance = float.NaN;
            LastShotScore    = missPoints;
            TotalScore      += missPoints;
            ShotCount       += 1;
            LastOutcome      = outcome;
            Tally(false);
            OnScored?.Invoke(LastShotScore, TotalScore, float.NaN);
            return missPoints;
        }

        void Tally(bool hit)
        {
            LastWasHit = hit;
            if (hit) Hits++;
            else     Misses++;
        }

        public void ResetScore()
        {
            TotalScore = 0f;
            LastShotScore = 0f;
            LastShotDistance = 0f;
            ShotCount = 0;
            Hits = 0;
            Misses = 0;
            LastWasHit = false;
            LastOutcome = TrialOutcome.Shot;
            // Announced like a shot, so the HUD drops to 0 with the reset — otherwise it kept showing
            // the practice score under the main-run prompt until the first main shot.
            OnScored?.Invoke(0f, 0f, 0f);
        }
    }
}
