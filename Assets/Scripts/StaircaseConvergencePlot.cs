using System.Collections.Generic;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Live debug overlay: plots the QUEST+ threshold estimate (theta-hat) and its
    /// uncertainty band (+/- posterior SD) against trial number as the FT staircase runs,
    /// so convergence is visible during piloting.
    ///
    /// showPlot defaults to OFF. Leave it off for real data-collection sessions -- watching
    /// your own live threshold estimate mid-study would bias later responses. Flip it on only
    /// for debugging/demoing.
    /// </summary>
    public class StaircaseConvergencePlot : MonoBehaviour
    {
        [Tooltip("Master toggle. Leave OFF for real study sessions -- debug/piloting only.")]
        public bool showPlot = false;

        public PerturbationController perturbation;

        [Header("Layout")]
        public Vector2 screenPosition = new Vector2(20, 20);
        public Vector2 size = new Vector2(400, 220);

        readonly List<float> _theta = new List<float>();
        readonly List<float> _sd    = new List<float>();

        int   _lastTrialCount = -1;
        float _yMin, _yMax;
        int   _xMaxTrials = 1;
        Texture2D _tex;

        static readonly Color Bg   = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color Line = new Color(0.3f, 0.95f, 0.55f);
        static readonly Color Band = new Color(0.3f, 0.95f, 0.55f, 0.25f);
        static readonly Color Grid = new Color(1f, 1f, 1f, 0.12f);

        void Awake()
        {
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
        }

        void Update()
        {
            if (!showPlot || perturbation == null || !perturbation.IsQuestPlusStaircase) return;

            int trials = perturbation.StaircaseTrials;
            if (trials == _lastTrialCount) return;
            _lastTrialCount = trials;

            _theta.Add(perturbation.JndEstimate);
            _sd.Add(perturbation.StaircaseThresholdSD);
            Redraw();
        }

        void Redraw()
        {
            var cfg = perturbation.QuestPlusConfigRef;
            if (cfg == null || _theta.Count == 0) return;

            int w = Mathf.Max(1, Mathf.RoundToInt(size.x));
            int h = Mathf.Max(1, Mathf.RoundToInt(size.y));
            if (_tex == null || _tex.width != w || _tex.height != h)
                _tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };

            _yMin = cfg.threshGrid[0];
            _yMax = cfg.threshGrid[cfg.threshGrid.Length - 1];
            _xMaxTrials = Mathf.Max(1, cfg.maxTrials);

            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = Bg;

            void Plot(int x, int y, Color c)
            {
                if (x < 0 || x >= w || y < 0 || y >= h) return;
                px[y * w + x] = c;
            }

            void DrawLine(int x0, int y0, int x1, int y1, Color c)
            {
                int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;
                while (true)
                {
                    Plot(x0, y0, c);
                    if (x0 == x1 && y0 == y1) break;
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; x0 += sx; }
                    if (e2 <= dx) { err += dx; y0 += sy; }
                }
            }

            for (int g = 1; g < 4; g++)
            {
                int gy = Mathf.RoundToInt(g / 4f * (h - 1));
                DrawLine(0, gy, w - 1, gy, Grid);
            }

            Vector2Int PointFor(int i)
            {
                float x01 = Mathf.Clamp01((i + 1) / (float)_xMaxTrials);
                float y01 = Mathf.Clamp01((_theta[i] - _yMin) / (_yMax - _yMin));
                return new Vector2Int(Mathf.RoundToInt(x01 * (w - 1)), Mathf.RoundToInt(y01 * (h - 1)));
            }

            for (int i = 0; i < _theta.Count; i++)
            {
                var p = PointFor(i);
                int sdPix = Mathf.RoundToInt((_sd[i] / (_yMax - _yMin)) * (h - 1));
                DrawLine(p.x, p.y - sdPix, p.x, p.y + sdPix, Band);
            }

            for (int i = 1; i < _theta.Count; i++)
            {
                var a = PointFor(i - 1);
                var b = PointFor(i);
                DrawLine(a.x, a.y, b.x, b.y, Line);
            }

            _tex.SetPixels(px);
            _tex.Apply();
        }

        void OnGUI()
        {
            if (!showPlot || _tex == null) return;

            var rect = new Rect(screenPosition.x, screenPosition.y, size.x, size.y);
            GUI.DrawTexture(rect, _tex);
            GUI.Label(new Rect(rect.x, rect.y - 18, rect.width, 18),
                $"FT JND convergence -- trial {_lastTrialCount}/{_xMaxTrials}");
            GUI.Label(new Rect(rect.x, rect.yMax + 2, rect.width * 0.5f, 18), $"{_yMin:0} ms");
            GUI.Label(new Rect(rect.xMax - rect.width * 0.5f, rect.yMax + 2, rect.width * 0.5f, 18),
                $"{_yMax:0} ms", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight });
        }
    }
}
