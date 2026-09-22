using System;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Scores each shot by horizontal distance from the tower base: full points within
    /// a short radius, then a fixed miss penalty outside it.
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
        /// Responses this phase that landed, and responses that did not — the HUD's tally.
        ///
        /// Kept here rather than derived from the score, because the score is a weighted number
        /// (hitPoints against missPoints) and two different tallies can produce the same total.
        /// Counted per RESPONSE, not per round: a shockwave round that takes an early press and a
        /// late press before its detection contributes two misses and one hit, which is what the
        /// participant actually did and what the shot log records.
        ///
        /// Practice and the main run are counted separately, because ExperimentDirector calls
        /// <see cref="ResetScore"/> between them.
        /// </summary>
        public int Hits   { get; private set; }
        public int Misses { get; private set; }

        /// <summary>True for a hit, false for a miss — whichever the last scored response was.
        /// The HUD uses it to decide which half of the tally to punch.</summary>
        public bool LastWasHit { get; private set; }

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
            Tally(hit);
            Debug.Log($"[ScoreManager] dist={dist:0.00} shot={pts:0.00} total={TotalScore:0.00} closeRadius={closeRadius:0.00} missPoints={missPoints:0.00}");
            OnScored?.Invoke(LastShotScore, TotalScore, dist);
            return pts;
        }

        /// <summary>
        /// Scores a trial whose outcome was decided by something other than distance — the
        /// shockwave weapon's response window.
        ///
        /// The cannon levels the whole plane, so there is no distance for it to be scored by and
        /// aim must not leak into the result: a participant who answered in time earns the hit
        /// whether they were over the tower or at the far edge of the field.
        /// <paramref name="distanceForLog"/> is still recorded so the shot log keeps the geometry
        /// alongside every other trial, but it never reaches the score.
        /// </summary>
        public float ScoreOutcome(bool success, float distanceForLog)
        {
            LastShotDistance = distanceForLog;

            float pts = success ? hitPoints : missPoints;

            LastShotScore = pts;
            TotalScore += pts;
            ShotCount += 1;
            Tally(success);
            Debug.Log($"[ScoreManager] outcome success={success} shot={pts:0.00} total={TotalScore:0.00} (distance not scored)");
            OnScored?.Invoke(LastShotScore, TotalScore, distanceForLog);
            return pts;
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
            // Announced like a shot, so the HUD drops to 0 with the reset — otherwise it kept
            // showing the practice score under the main-run prompt until the first main shot.
            OnScored?.Invoke(0f, 0f, 0f);
        }
    }
}
