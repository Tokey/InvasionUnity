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

            float pts = dist <= closeRadius ? hitPoints : missPoints;

            LastShotScore = pts;
            TotalScore += pts;
            ShotCount += 1;
            Debug.Log($"[ScoreManager] dist={dist:0.00} shot={pts:0.00} total={TotalScore:0.00} closeRadius={closeRadius:0.00} missPoints={missPoints:0.00}");
            OnScored?.Invoke(LastShotScore, TotalScore, dist);
            return pts;
        }

        public void ResetScore()
        {
            TotalScore = 0f;
            LastShotScore = 0f;
            LastShotDistance = 0f;
            ShotCount = 0;
        }
    }
}
