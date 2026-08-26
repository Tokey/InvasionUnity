using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Works out the chance-level hit rate for the task — the value QUEST+'s <c>guessRate</c>
    /// (γ) column should hold.
    ///
    /// γ is the probability of "noticing" at a stimulus of zero. In this design a landed shot
    /// *is* the detection response, so γ is the probability of landing a shot with no
    /// information at all: the tower is hidden in fog, the participant aims blind, and the shot
    /// counts if it lands within <c>closeRadius</c> of the tower base.
    ///
    ///     γ = reachable hit window / range the shot can land in
    ///
    /// The tower's X is random, but the hit window it carries is the same width wherever it
    /// lands, so for a narrow window a blind shot's chance does not depend on where the tower
    /// went and γ is simply 2·closeRadius / width. That shortcut holds only while the window
    /// never reaches past the edge of the shot-reachable band. Once closeRadius exceeds the
    /// margin between the tower's spawn band and that edge, towers near the edge carry a window
    /// partly over ground the UFO cannot fly over — unhittable, so chance is lower than the
    /// ratio suggests. The reachable width is therefore averaged over tower positions.
    ///
    /// Assumes shots travel vertically, so a shot's landing X equals the UFO's X. If
    /// LaserFirer.fireDirection is ever angled, this becomes an approximation.
    /// </summary>
    public static class GuessRateCalculator
    {
        public static bool TryCompute(Camera cam, float fixedZ, float spawnWidthFraction,
                                       float closeRadius, out float guessRate, out string report)
        {
            guessRate = 0f;
            report    = "";
            if (cam == null) return false;

            // Full visible width at the tower's depth.
            float vMin = CameraRig.ViewportPointAtZ(cam, 0f, 0.5f, fixedZ).x;
            float vMax = CameraRig.ViewportPointAtZ(cam, 1f, 0.5f, fixedZ).x;
            Order(ref vMin, ref vMax);
            float visibleWidth = vMax - vMin;

            // Where a shot can actually land — the UFO's clamp in UfoController.Update. Slightly
            // narrower than the full view, so it gives a slightly higher (stricter) γ.
            float uMin = CameraRig.ViewportPointAtZ(cam, 0.02f, 0.5f, fixedZ).x;
            float uMax = CameraRig.ViewportPointAtZ(cam, 0.98f, 0.5f, fixedZ).x;
            Order(ref uMin, ref uMax);
            float shotWidth = uMax - uMin;

            if (visibleWidth <= 1e-4f) return false;

            float r      = Mathf.Max(0f, closeRadius);
            float window = 2f * r;

            // Tower spawn band.
            float margin = (1f - Mathf.Clamp01(spawnWidthFraction)) * 0.5f;
            float tMin = CameraRig.ViewportPointAtZ(cam, margin,      0.5f, fixedZ).x;
            float tMax = CameraRig.ViewportPointAtZ(cam, 1f - margin, 0.5f, fixedZ).x;
            Order(ref tMin, ref tMax);
            float edgeMargin = Mathf.Min(tMin - uMin, uMax - tMax);

            // Average, over every tower position, of how much of the hit window a shot can
            // actually reach. While edgeMargin exceeds closeRadius this equals 2r and the simple
            // ratio is exact — but with a wide window the tower can sit close enough to the edge
            // that part of its window lies outside the shot-reachable band, and you cannot hit
            // ground the UFO cannot fly over. Integrated rather than solved in closed form: it is
            // a one-off startup cost and immune to sign and clamping slips.
            const int steps = 20000;
            double sum = 0.0;
            for (int i = 0; i < steps; i++)
            {
                float t  = Mathf.Lerp(tMin, tMax, (i + 0.5f) / steps);
                float lo = Mathf.Max(uMin, t - r);
                float hi = Mathf.Min(uMax, t + r);
                sum += Mathf.Max(0f, hi - lo);
            }
            float reachableWindow = (float)(sum / steps);

            guessRate = Mathf.Clamp01(reachableWindow / shotWidth);
            float naive = Mathf.Clamp01(window / shotWidth);

            var sb = new StringBuilder();
            sb.AppendLine("[GuessRate] Chance-level hit rate from scene geometry:");
            sb.AppendLine($"    Visible X width  : {visibleWidth:0.000}   ({vMin:0.000} .. {vMax:0.000})");
            sb.AppendLine($"    Shot-reachable   : {shotWidth:0.000}   ({uMin:0.000} .. {uMax:0.000})");
            sb.AppendLine($"    Tower spawn band : {tMin:0.000} .. {tMax:0.000}   (edge margin {edgeMargin:0.000})");
            sb.AppendLine($"    Hit window (2r)  : {window:0.000}   (closeRadius {r:0.000})");
            sb.AppendLine($"    Reachable window : {reachableWindow:0.000}   averaged over tower positions");
            sb.AppendLine($"    >>> guessRate    : {guessRate:0.0000}   <-- put this in ExperimentConfig.csv");

            if (edgeMargin < r)
                sb.AppendLine($"    (window is clipped at the field edges; the naive 2r/width would " +
                              $"read {naive:0.0000}, which overstates chance by {naive - guessRate:0.0000})");

            report = sb.ToString();
            return true;
        }

        static void Order(ref float a, ref float b)
        {
            if (a > b) { float tmp = a; a = b; b = tmp; }
        }
    }
}
