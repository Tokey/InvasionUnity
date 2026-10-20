using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JndUfo
{
    /// <summary>
    /// The round's answer, shown after every response: the two frame rates, one either side of the
    /// tower, each in its side's colour — cool for the faster side, warm for the slower — with the
    /// rate actually delivered underneath, while each side of the screen slowly takes on its colour.
    ///
    /// Layout:
    ///
    ///   the numbers  A pair split by the tower's beam. Each side's block sits high in the sky right
    ///                beside the beam, its text aligned toward it, between the beam and that side's
    ///                hit-zone line — so the two rates read as one comparison across the boundary.
    ///                Both blocks share one scale, so the two numbers are always the same size. The
    ///                hit zone is wide enough to hold a three-digit rate at full size, which keeps
    ///                the pair looking the same wherever the tower is; only when the tower is so near
    ///                an edge that the screen cuts into its zone does the pair shrink, together, to
    ///                fit the narrower side. (If the hit zone is ever set too narrow to hold them,
    ///                the numbers sit just outside its lines instead.)
    ///   the tint     Each whole side, from the beam to the screen edge: faint inside the hit zone,
    ///                so the zone stays a clear window exactly as wide as the target; strongest in a
    ///                band just beyond the zone lines; easing off toward the screen edge; clear over
    ///                the city at the bottom. It starts fully transparent and deepens slowly across
    ///                the whole reveal.
    ///
    /// Each side is clipped to its own side of the tower line with a soft mask, so no colour, shadow
    /// or text can cross it, wherever the tower is.
    ///
    /// The motion tells the story of the round: both numbers slide out from behind the beam, the
    /// base side's lands at once, and the altered side's rolls away from the base rate to where the
    /// round put it, so the size of the difference is something the eye watches happen. The pacing
    /// is fitted to how long the reveal stays up, so every part has landed with time left to read it.
    ///
    /// Everything is whole numbers, in plain ASCII — the HUD font (StarShield) has nothing beyond it.
    ///
    /// Drawn on its own overlay canvas BELOW the gameplay HUD's, so the hit/miss callout lands on
    /// top of it, and below the ExperimentOverlay's veil. It only runs during the reveal, between
    /// rounds, at the base frame rate, and does no work at all while hidden. Auto-created by
    /// <see cref="GameManager"/>. <c>Tools ▸ JND ▸ Render FPS Reveal Previews</c> renders it at a
    /// sweep of tower positions without playing.
    /// </summary>
    public class FpsRevealOverlay : MonoBehaviour
    {
        [Header("Colour")]
        [Tooltip("The side that ran at the HIGHER frame rate.")]
        public Color fasterColor = new Color(0.30f, 0.86f, 1.00f, 1f);
        [Tooltip("The side that ran at the LOWER frame rate. Blue against orange stays distinct for " +
                 "the common forms of colour blindness.")]
        public Color slowerColor = new Color(1.00f, 0.58f, 0.22f, 1f);
        [Tooltip("Opacity the tint deepens to at its strongest, in the band just beyond the hit-zone " +
                 "lines. It starts fully transparent.")]
        [Range(0f, 1f)] public float washAlpha = 0.15f;
        [Tooltip("The tint inside the hit zone, as a fraction of its strongest: enough to show which " +
                 "side is which, faint enough that the zone stays a clear window.")]
        [Range(0f, 1f)] public float washInsideZone = 0.3f;
        [Tooltip("Opacity of the soft shadow behind each number, for contrast over a bright sky.")]
        [Range(0f, 1f)] public float shadowAlpha = 0.3f;

        [Header("Type  (1920×1080 reference units)")]
        [Min(20f)] public float numberSize   = 128f;
        [Min(8f)]  public float unitSize     = 30f;
        [Min(8f)]  public float measuredSize = 24f;
        [Tooltip("Height of the numerals' centre as a fraction of the screen, from the bottom: high in " +
                 "the sky, clear of the city, the centre callout and the practice hint.")]
        [Range(0.5f, 0.9f)] public float numberHeight = 0.78f;

        [Header("Layout")]
        [Tooltip("Gap between a number and the tower line at full size; it shrinks with the number.")]
        [Min(0f)] public float beamGap    = 26f;
        [Tooltip("Closest a number comes to a hit-zone line.")]
        [Min(0f)] public float zoneGap    = 10f;
        [Tooltip("Closest a number comes to the screen edge.")]
        [Min(0f)] public float edgeMargin = 14f;
        [Tooltip("When the screen edge squeezes one side's number, the other shrinks with it so the " +
                 "two stay the same size — but never below this scale. Past it, the squeezed side " +
                 "alone goes smaller.")]
        [Range(0.2f, 1f)] public float pairFloor = 0.6f;
        [Tooltip("Smallest scale a number ever takes. Only a tower right against the screen edge, " +
                 "closer than the game ever places it, needs less; there it is clipped at the edge.")]
        [Range(0.1f, 1f)] public float minScale = 0.3f;

        [Header("Motion")]
        [Tooltip("Seconds for a number to slide out from behind the beam, overshoot and settle.")]
        [Min(0.05f)] public float popSec  = 0.32f;
        [Tooltip("Seconds to fade everything out when the reveal ends.")]
        [Min(0.05f)] public float hideSec = 0.25f;
        [Tooltip("How long the reveal stays up when the caller doesn't say. The roll, the measured " +
                 "rates and the tint are paced to fit it.")]
        [Min(0.3f)] public float defaultHoldSec = 2f;

        // The tint's profile spans this many hit-zone half-widths out from the beam; a wider side
        // shows its flat outer tail (the texture clamps).
        const float WashSpanZones   = 8f;
        // The tint's level toward the screen edge, as a fraction of its strongest.
        const float WashTail        = 0.3f;
        const int   WashTexW        = 512;
        const int   WashTexH        = 128;
        // How far behind the beam a number starts its slide, in canvas units at full size.
        const float SlideIn         = 28f;
        const float MeasuredFadeSec = 0.25f;
        // Inside the hit zone is preferred while it holds the numbers at this scale or more.
        const float InsideMinScale  = 0.75f;

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>Everything one side of the screen is made of.</summary>
        sealed class Half
        {
            public float           side;     // −1: left of the tower, +1: right of it
            public RectTransform   area;     // this side of the tower line, clipped by its own mask
            public RectMask2D      mask;
            public RawImage        wash;
            public RectTransform   block;    // the numbers' anchor point: beside the beam, high up
            public RectTransform   shadowRect;
            public RawImage        shadow;
            public TextMeshProUGUI number, unit, measured;

            public int   targetFps, startFps;
            public bool  rolls;              // the altered side rolls away from the base
            public Color color;
            public float naturalWidth;       // the block's widest line at scale 1
            public float scale = 1f;
            public float gap;                // the block's inner edge, out from the tower line
            public int   shownFps = int.MinValue;
        }

        Canvas        _canvas;
        CanvasScaler  _scaler;
        CanvasGroup   _group;
        RectTransform _root;
        Half          _left, _right;
        Texture2D     _washTex, _shadowTex;
        float         _washTexInside = -1f;
        readonly float[] _numberWidth = new float[5];   // by digit count, at scale 1 (NumberWidth)
        char          _widestDigit;

        // Vertical rhythm inside a block, from the type sizes (see Build).
        float _unitY, _measuredY, _blockMidY;

        enum State { Hidden, Showing, Hiding }
        State _state = State.Hidden;
        float _shownAt;     // realtime the reveal (after its delay) begins
        float _hideAt;

        // The reveal's pacing, in seconds after it begins, fitted to how long it stays up (Pace).
        float _rollStart, _rollSec, _measuredAt, _washStart, _washEnd;

        void Awake() => Build();

        void OnDestroy()
        {
            if (_washTex   != null) Destroy(_washTex);
            if (_shadowTex != null) Destroy(_shadowTex);
        }

        // ── API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the reveal for a round that has just ended. <paramref name="towerBase"/> is where
        /// the boundary was and <paramref name="hitRadius"/> the hit zone's half-width, both in world
        /// units. <paramref name="holdSec"/> is how long the reveal will stay up, counted from the end
        /// of <paramref name="delaySec"/> to <see cref="Hide"/>; the animation is paced to land well
        /// inside it. 0 uses <see cref="defaultHoldSec"/>.
        /// </summary>
        public void Show(PerturbationController.TrialRecord trial, Vector3 towerBase, float hitRadius,
                         float delaySec = 0f, float holdSec = 0f)
        {
            if (_root == null) Build();
            if (_root == null || !trial.valid) return;

            // Where the boundary and the hit-zone lines fall on screen. The camera looks straight
            // down +Z, so vertical lines in the world stay vertical on screen.
            float split = 0.5f, zone = 0.12f;
            Camera cam = _canvas.renderMode == RenderMode.ScreenSpaceCamera && _canvas.worldCamera != null
                ? _canvas.worldCamera
                : CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            if (cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(towerBase);
                Vector3 ve = cam.WorldToViewportPoint(towerBase + Vector3.right * hitRadius);
                if (vp.z > 0f)
                {
                    split = Mathf.Clamp(vp.x, 0.02f, 0.98f);
                    zone  = Mathf.Clamp(Mathf.Abs(ve.x - vp.x), 0.01f, 0.45f);
                }
            }

            // Live before measuring: the layout below asks TextMeshPro to lay the text out.
            _root.gameObject.SetActive(true);
            RefreshWashTexture();

            bool leftFaster = trial.LeftFps >= trial.RightFps;
            Configure(_left,  trial.LeftFps,  trial.MeasuredLeftFps,
                      trial.alteredSide == PerturbationController.Side.Left,  trial.baseFps,
                      leftFaster ? fasterColor : slowerColor);
            Configure(_right, trial.RightFps, trial.MeasuredRightFps,
                      trial.alteredSide == PerturbationController.Side.Right, trial.baseFps,
                      leftFaster ? slowerColor : fasterColor);

            Layout(split, zone);
            Pace(holdSec);

            _shownAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySec);
            _state   = State.Showing;
            _group.alpha = 1f;
            Animate(-1f);   // the very first drawn frame is the start pose, not whatever was left
        }

        /// <summary>Fades the reveal out. Safe to call when it is not showing.</summary>
        public void Hide()
        {
            if (_state != State.Showing) return;
            _state  = State.Hiding;
            _hideAt = Time.realtimeSinceStartup;
        }

        // ── Editor preview hooks (Tools ▸ JND ▸ Render FPS Reveal Previews) ───

        /// <summary>Renders into <paramref name="cam"/> instead of over the screen, so an editor tool
        /// can capture it to a texture. Not used in play.</summary>
        public void PreviewOnCamera(Camera cam)
        {
            if (_root == null) Build();
            _canvas.renderMode    = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera   = cam;
            _canvas.planeDistance = 5f;
            RefreshScaler();
        }

        /// <summary>Re-reads the screen size into the canvas scaler — after the preview camera's target
        /// changes size, which the scaler only notices in an Update that edit mode never runs.</summary>
        public void RefreshScaler()
        {
            if (_scaler == null) return;
            _scaler.enabled = false;
            _scaler.enabled = true;
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>Poses the reveal <paramref name="t"/> seconds in.</summary>
        public void PreviewPose(float t) => Animate(t);

        /// <summary>Poses the exit <paramref name="h"/> of the way through (0–1).</summary>
        public void PreviewHidePose(float h)
        {
            Animate(10f);
            _group.alpha = 1f - EaseInQuad(Mathf.Clamp01(h));
            Drift(_left,  h);
            Drift(_right, h);
        }

        // ── Per frame ────────────────────────────────────────────────────────

        void Update()
        {
            if (_state == State.Hidden) return;

            // realtimeSinceStartup throughout: the reveal must animate on the wall clock whatever
            // the frame rate, and unscaledTime is only stamped once per frame.
            float now = Time.realtimeSinceStartup;

            if (_state == State.Hiding)
            {
                float h = Mathf.Clamp01((now - _hideAt) / hideSec);
                _group.alpha = 1f - EaseInQuad(h);
                Drift(_left,  h);
                Drift(_right, h);
                if (h >= 1f)
                {
                    _state = State.Hidden;
                    _root.gameObject.SetActive(false);
                }
                return;
            }

            Animate(now - _shownAt);
        }

        void Animate(float t)
        {
            AnimateHalf(_left,  t);
            AnimateHalf(_right, t);
        }

        void AnimateHalf(Half s, float t)
        {
            // Tint: fully transparent at first, deepening slowly across the whole reveal.
            float w    = Mathf.Clamp01((t - _washStart) / (_washEnd - _washStart));
            float glow = w * w * (3f - 2f * w);   // smoothstep: eases in and settles
            s.wash.color = WithAlpha(s.color, washAlpha * glow);

            // Number: slides out from behind the beam (the mask hides what is still past the tower
            // line), growing outward from its inner edge, overshooting and settling.
            float tp   = t - 0.04f;
            float pop  = Mathf.Clamp01(tp / popSec);
            float grow = Mathf.LerpUnclamped(0.6f, 1f, EaseOutBack(pop));
            float fade = Mathf.Clamp01(tp / (popSec * 0.45f));

            // Roll: the altered side counts from the base rate to its own; the base side holds.
            float tr   = t - _rollStart;
            float roll = EaseOutCubic(Mathf.Clamp01(tr / _rollSec));
            int   fps  = s.rolls ? Mathf.RoundToInt(Mathf.Lerp(s.startFps, s.targetFps, roll)) : s.targetFps;
            if (fps != s.shownFps)
            {
                s.shownFps = fps;
                s.number.SetText("{0}", fps);   // no allocation, unlike a string per frame
            }

            // A small punch when the rolling number lands, so its arrival reads as an event.
            float punch = 0f;
            if (s.rolls && tr >= _rollSec)
                punch = Mathf.Sin(Mathf.Clamp01((tr - _rollSec) / 0.2f) * Mathf.PI) * 0.06f;

            s.block.localScale       = Vector3.one * (s.scale * (grow + punch));
            s.block.anchoredPosition = new Vector2(
                s.side * (s.gap - SlideIn * s.scale * (1f - EaseOutCubic(pop))), 0f);

            Color c = Color.Lerp(s.color, Color.white, 0.18f);
            s.number.color = WithAlpha(c, fade);
            s.unit.color   = WithAlpha(c, 0.85f * fade);
            s.shadow.color = new Color(0f, 0f, 0f, shadowAlpha * fade);

            // Measured: quieter, and only once the numbers have landed.
            float m = EaseOutCubic(Mathf.Clamp01((t - _measuredAt) / MeasuredFadeSec));
            s.measured.color = WithAlpha(Color.Lerp(s.color, Color.white, 0.55f), 0.9f * m);
            s.measured.rectTransform.anchoredPosition = new Vector2(0f, _measuredY - 8f * (1f - m));
        }

        // On the way out: the block lifts and swells a touch as everything fades.
        void Drift(Half s, float h)
        {
            float e = EaseInQuad(h);
            s.block.localScale       = Vector3.one * (s.scale * (1f + 0.05f * e));
            s.block.anchoredPosition = new Vector2(s.side * s.gap, 18f * e);
        }

        // ── Layout ───────────────────────────────────────────────────────────

        void Configure(Half s, int fps, float measured, bool isAltered, int baseFps, Color color)
        {
            s.targetFps = fps;
            s.startFps  = isAltered ? baseFps : fps;
            s.rolls     = isAltered && baseFps != fps;
            s.color     = color;
            s.shownFps  = int.MinValue;

            string m = float.IsNaN(measured) ? "not visited" : "measured " + Mathf.RoundToInt(measured);
            s.unit.SetText("FPS");
            s.measured.SetText(m);

            // The block's footprint: its widest line at scale 1. The number is sized for the most
            // digits the roll passes through, all of them the widest numeral, so nothing has to move
            // mid-animation.
            float numW  = NumberWidth(s.number, DigitCount(Mathf.Max(s.startFps, s.targetFps)));
            float unitW = s.unit.GetPreferredValues("FPS").x;
            float measW = s.measured.GetPreferredValues(m).x;
            s.naturalWidth = Mathf.Max(numW, Mathf.Max(unitW, measW));

            // The shadow sits behind the whole block, which runs outward from the beam.
            s.shadowRect.sizeDelta        = new Vector2(s.naturalWidth + 0.9f * numberSize, 1.9f * numberSize);
            s.shadowRect.anchoredPosition = new Vector2(s.side * s.naturalWidth * 0.5f, _blockMidY);
        }

        /// <summary>
        /// Splits the screen at the tower, points each side's tint outward from it, and sizes and
        /// places the two number blocks. See the class summary for the rules; all of it is in
        /// canvas units.
        /// </summary>
        void Layout(float split, float zoneHalf)
        {
            float W  = CanvasWidth;
            float xs = split * W;              // the tower line
            float z  = zoneHalf * W;           // the hit zone's half-width
            float wl = xs, wr = W - xs;        // each side's width

            SetArea(_left,  0f,    split);
            SetArea(_right, split, 1f);
            SetWash(_left,  wl, z);
            SetWash(_right, wr, z);

            // The scale each block fits at between the beam and its hit-zone line, or the screen
            // edge where that comes first — together with its gap from the beam, which scales with
            // it like the type does ...
            float inL  = Mathf.Min(z - zoneGap, wl - edgeMargin) / Mathf.Max(1f, _left.naturalWidth  + beamGap);
            float inR  = Mathf.Min(z - zoneGap, wr - edgeMargin) / Mathf.Max(1f, _right.naturalWidth + beamGap);
            // ... or beyond the zone line, out to the screen edge.
            float outL = (wl - z - zoneGap - edgeMargin) / Mathf.Max(1f, _left.naturalWidth);
            float outR = (wr - z - zoneGap - edgeMargin) / Mathf.Max(1f, _right.naturalWidth);

            float kIn   = Mathf.Min(inL,  inR);
            float kOut  = Mathf.Min(outL, outR);
            bool inside = kIn >= InsideMinScale || kIn >= kOut;

            // One scale for both, so the two numbers stay the same size — unless the screen edge
            // squeezes one side below pairFloor, in which case that side alone goes smaller.
            float fitL  = inside ? inL : outL;
            float fitR  = inside ? inR : outR;
            float pair  = Mathf.Min(1f, Mathf.Min(fitL, fitR));
            float floor = Mathf.Max(pair, pairFloor);
            _left.scale  = Mathf.Clamp(Mathf.Min(fitL, floor), minScale, 1f);
            _right.scale = Mathf.Clamp(Mathf.Min(fitR, floor), minScale, 1f);

            _left.gap  = inside ? beamGap * _left.scale  : z + zoneGap;
            _right.gap = inside ? beamGap * _right.scale : z + zoneGap;
        }

        /// <summary>
        /// Fits the reveal's beats into how long it stays up: the roll takes up to 40% of it, the
        /// measured rates fade in as the roll settles (not before, or they would give away where
        /// it lands), and the tint keeps deepening until just before the end.
        /// </summary>
        void Pace(float holdSec)
        {
            float hold  = holdSec > 0f ? holdSec : defaultHoldSec;
            _rollStart  = 0.1f;
            _rollSec    = Mathf.Clamp(0.4f * hold, 0.3f, 0.7f);
            _measuredAt = _rollStart + _rollSec - 0.1f;   // the roll's last, slowest tenth of a second
            _washStart  = 0.08f;
            _washEnd    = Mathf.Clamp(0.9f * hold, _washStart + 0.4f, 2.2f);
        }

        void SetArea(Half s, float xMin, float xMax)
        {
            s.area.anchorMin = new Vector2(xMin, 0f);
            s.area.anchorMax = new Vector2(xMax, 1f);
            s.area.offsetMin = Vector2.zero;
            s.area.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// The tint fills its whole side; this maps its texture so that u = 0 sits on the beam and
        /// the profile is measured in hit-zone half-widths from there (the left side mirrors it).
        /// </summary>
        void SetWash(Half s, float width, float zoneHalf)
        {
            s.wash.enabled = width > 2f;
            if (!s.wash.enabled) return;

            float u = width / Mathf.Max(1f, WashSpanZones * zoneHalf);
            s.wash.uvRect = s.side < 0f ? new Rect(u, 0f, -u, 1f) : new Rect(0f, 0f, u, 1f);
        }

        float CanvasWidth
        {
            get
            {
                var rt = _canvas != null ? (RectTransform)_canvas.transform : null;
                float w = rt != null ? rt.rect.width : 0f;
                return w > 1f ? w : 1920f;
            }
        }

        // The widest a number of this many digits can be at scale 1: the widest numeral repeated,
        // measured as one string — bold type adds spacing between characters, which a sum of single
        // numerals would miss. Measured once per digit count.
        float NumberWidth(TextMeshProUGUI t, int digits)
        {
            digits = Mathf.Clamp(digits, 1, _numberWidth.Length - 1);
            if (_numberWidth[digits] > 0f) return _numberWidth[digits];
            if (_widestDigit == '\0')
            {
                float best = -1f;
                for (char d = '0'; d <= '9'; d++)
                {
                    float w = t.GetPreferredValues(d.ToString()).x;
                    if (w > best) { best = w; _widestDigit = d; }
                }
            }
            return _numberWidth[digits] = t.GetPreferredValues(new string(_widestDigit, digits)).x;
        }

        static int DigitCount(int n)
        {
            n = Mathf.Abs(n);
            int digits = 1;
            while (n >= 10) { n /= 10; digits++; }
            return digits;
        }

        void Build()
        {
            if (_root != null) return;

            var canvasGo = new GameObject("FpsRevealCanvas");
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Below the gameplay HUD (0), so the callout and the vignette land on top of the tint
            // instead of being tinted by it; far below the ExperimentOverlay's veil (5000).
            _canvas.sortingOrder = -10;

            _scaler = canvasGo.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1920f, 1080f);
            _scaler.matchWidthOrHeight  = 0.5f;
            // No GraphicRaycaster: nothing here is clickable, and without one it cannot swallow input.

            var rootGo = new GameObject("Reveal", typeof(RectTransform));
            rootGo.transform.SetParent(canvasGo.transform, false);
            _root = (RectTransform)rootGo.transform;
            Stretch(_root);
            _group = rootGo.AddComponent<CanvasGroup>();
            _group.interactable   = false;
            _group.blocksRaycasts = false;

            // Vertical rhythm, from the type sizes: StarShield's numerals are 0.7 em tall and
            // TextMeshPro centres them on the line, so each line's half-height is 0.35 of its size.
            float numHalf  = 0.35f * numberSize;
            float unitHalf = 0.35f * unitSize;
            float measHalf = 0.35f * measuredSize;
            _unitY     = -(numHalf + 0.13f * numberSize + unitHalf);
            _measuredY = _unitY - (unitHalf + 0.4f * unitSize + measHalf);
            _blockMidY = (numHalf + _measuredY - measHalf) * 0.5f;

            _shadowTex = BuildShadowTexture(128);

            _left  = BuildHalf("Left",  -1f);
            _right = BuildHalf("Right", +1f);
            RefreshWashTexture();

            rootGo.SetActive(false);
        }

        Half BuildHalf(string name, float side)
        {
            var s = new Half { side = side };
            bool left = side < 0f;

            s.area = NewRect(name, _root);
            // Everything on this side is clipped to it, softly, so nothing crosses the tower line.
            s.mask = s.area.gameObject.AddComponent<RectMask2D>();
            s.mask.softness = new Vector2Int(12, 0);

            var washRect = NewRect("Tint", s.area);
            Stretch(washRect);
            s.wash = washRect.gameObject.AddComponent<RawImage>();
            s.wash.raycastTarget = false;

            // A point on the tower line's side of the area, at the numbers' height; everything in
            // the block hangs off it and runs outward, so the block scales out from the beam.
            s.block = NewRect("Block", s.area);
            s.block.anchorMin = s.block.anchorMax = new Vector2(left ? 1f : 0f, numberHeight);
            s.block.pivot     = new Vector2(left ? 1f : 0f, 0.5f);
            s.block.sizeDelta = Vector2.zero;

            s.shadowRect = NewRect("Shadow", s.block);
            s.shadowRect.anchorMin = s.shadowRect.anchorMax = new Vector2(0.5f, 0.5f);
            s.shadowRect.pivot     = new Vector2(0.5f, 0.5f);
            s.shadow = s.shadowRect.gameObject.AddComponent<RawImage>();
            s.shadow.texture       = _shadowTex;
            s.shadow.raycastTarget = false;

            s.number   = MakeText("Number",   s.block, side, numberSize,   FontStyles.Bold,   0f,         0f);
            s.unit     = MakeText("Unit",     s.block, side, unitSize,     FontStyles.Bold,   _unitY,     14f);
            s.measured = MakeText("Measured", s.block, side, measuredSize, FontStyles.Normal, _measuredY, 2f);

            // A dark rim, so a pale number stays legible over a bright sky or a lit building.
            s.number.outlineWidth = 0.12f;
            s.number.outlineColor = new Color32(0, 0, 0, 160);

            return s;
        }

        // Text that runs outward from the block's point beside the beam: right-aligned on the left
        // side, left-aligned on the right, so both sides' lines hug the tower line.
        static TextMeshProUGUI MakeText(string name, RectTransform parent, float side, float size,
                                        FontStyles style, float y, float spacing)
        {
            bool left = side < 0f;
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(left ? 1f : 0f, 0.5f);
            rt.sizeDelta = new Vector2(900f, size * 1.3f);
            rt.anchoredPosition = new Vector2(0f, y);

            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize         = size;
            t.fontStyle        = style;
            t.alignment        = left ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
            t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode     = TextOverflowModes.Overflow;
            t.raycastTarget    = false;
            HudFont.Apply(t);
            return t;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ── Textures ─────────────────────────────────────────────────────────

        // (Re)paints the tint whenever washInsideZone has changed since it was last painted, so the
        // inspector value takes effect on the next reveal.
        void RefreshWashTexture()
        {
            if (_washTex != null && Mathf.Approximately(_washTexInside, washInsideZone)) return;
            if (_washTex == null)
                _washTex = new Texture2D(WashTexW, WashTexH, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode   = TextureWrapMode.Clamp,
                };
            PaintWash(_washTex, washInsideZone);
            _washTexInside = washInsideZone;
            _left.wash.texture = _right.wash.texture = _washTex;
        }

        /// <summary>
        /// The tint. Horizontally, u = 0 is the beam and u = 1 is <see cref="WashSpanZones"/>
        /// hit-zone half-widths out from it: <paramref name="inside"/> of full strength across the
        /// zone, rising through the zone line to a peak just beyond it, then easing off to a faint
        /// flat tail. Vertically it is clear over the bottom fifth — the city and the tower's base —
        /// fades in toward the sky, and eases off at the very top so the HUD row stays clean.
        /// </summary>
        static void PaintWash(Texture2D tex, float inside)
        {
            int w = tex.width, h = tex.height;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v   = y / (float)(h - 1);
                float sky = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.5f, v));
                float top = 1f - 0.45f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.85f, 1f, v));
                for (int x = 0; x < w; x++)
                {
                    float r = x / (float)(w - 1) * WashSpanZones;   // out from the beam, in zone half-widths
                    float level = r < 1.25f
                        ? Mathf.Lerp(inside, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 1.25f, r)))
                        : Mathf.Lerp(1f, WashTail, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.25f, 5f, r)));
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(255f * level * sky * top));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
        }

        /// <summary>A soft elliptical shadow — a radial falloff that never shows an edge.</summary>
        static Texture2D BuildShadowTexture(int res)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };

            var px = new Color32[res * res];
            for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float nx = 2f * x / (res - 1) - 1f;
                float ny = 2f * y / (res - 1) - 1f;
                float r  = Mathf.Sqrt(nx * nx + ny * ny);
                float a  = Mathf.Clamp01(1f - r);
                a = a * a * (3f - 2f * a);   // smoothstep: soft centre, no visible rim
                px[y * res + x] = new Color32(255, 255, 255, (byte)(255f * a));
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ── Easing ───────────────────────────────────────────────────────────

        static float EaseOutCubic(float t) { t = 1f - t; return 1f - t * t * t; }
        static float EaseInQuad(float t)   => t * t;

        // Overshoots by ~10% and settles — the "pop".
        static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
    }
}
