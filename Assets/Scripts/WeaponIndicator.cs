using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JndUfo
{
    /// <summary>
    /// A chip in the top-right corner naming the armed weapon, with a moving icon that shows what
    /// that weapon does: a bolt travelling up a rail for the laser, expanding shock arcs for the
    /// shockwave cannon. It shares the top row with the hit/miss tally on the left, leaving the
    /// middle of the screen — the play area, and where the hit/miss callout lands — clear.
    ///
    /// This is not decoration. The two weapons ask for opposite behaviour — the laser wants the
    /// participant to aim at a hidden tower, the cannon wants them to sit still and then react — and
    /// until now the only place the study said which one was armed was the block's start prompt.
    /// A participant who loses track mid-block answers the wrong task, and that contaminates the
    /// posterior rather than merely confusing them.
    ///
    /// Deliberately says nothing about the stimulus. The weapon is a block-level fact the
    /// participant is told up front; the stutter's size and timing are what the study measures, and
    /// nothing here may hint at either. In particular the icon's animation is free-running off a
    /// wall clock and never reacts to a spike, a window opening, or a shot.
    ///
    /// It builds its own canvas rather than joining the gameplay HUD's, so its per-frame animation
    /// dirties only its own six graphics — a rebuild of the score/round canvas every frame would be
    /// real work in exactly the place this project measures frame times. That cost is also constant
    /// across conditions, so it cannot bias a threshold either way, but keeping it isolated and
    /// small is cheap. Set <see cref="animate"/> false to pin both icons to a still frame.
    ///
    /// Auto-created by <see cref="GameManager"/>, so no scene wiring is needed.
    /// </summary>
    public class WeaponIndicator : MonoBehaviour
    {
        [Header("Placement")]
        [Tooltip("Sit on the same line as the HUD's hit/miss tally, so the top of the screen reads " +
                 "as one row: tally on the left, weapon on the right. Uncheck to use topOffset " +
                 "instead.")]
        public bool alignToScoreText = true;
        [Tooltip("Pixels down from the top of the screen (1920x1080 reference) to the chip's top " +
                 "edge. Used only when alignToScoreText is off, or when there is no score text to " +
                 "align to.")]
        public float topOffset = 20f;
        [Tooltip("Which side of the screen the chip hangs from. Right, opposite the hit/miss " +
                 "tally: the two are the only things on the top row and splitting them to the " +
                 "corners leaves the middle — where the UFO is flown and where the callout lands " +
                 "— clear. Centre would put the chip straight over the play area.")]
        public bool anchorRight = true;
        [Tooltip("Pixels in from the screen edge the chip is anchored to.")]
        public float sideMargin = 24f;
        [Tooltip("Pixels the chip's top edge must stay below the top of the screen. Centring on a " +
                 "score readout that sits flush with the top edge would otherwise crop it.")]
        [Min(0f)] public float minTopMargin = 8f;
        [Tooltip("Padding between the plate's edge and its contents. The plate's SIZE is not set " +
                 "here — it is measured from whatever the chip is currently showing, so an " +
                 "icon-only chip is a small square and a named one is a wide pill.")]
        public Vector2Int platePadding = new Vector2Int(16, 8);

        [Header("Laser")]
        [Tooltip("Colour of the laser icon. Not read from the beam: the beam's hue comes from the " +
                 "UFO pointer's material emission, which is a look choice on the model rather than " +
                 "anything the HUD should inherit.")]
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);
        [Tooltip("Seconds for the bolt to travel the length of the rail once.")]
        [Min(0.05f)] public float laserSweepSec = 0.85f;

        [Header("Shockwave")]
        [Tooltip("Take the arc colours from the scene's ShockwaveCannon, so the icon and the real " +
                 "blast are never two different violets. Uncheck to use the two below.")]
        public bool matchCannonColors = true;
        public Color arcCoreColor = new Color(1f, 0.96f, 1f, 1f);
        public Color arcEdgeColor = new Color(0.55f, 0.22f, 1f, 1f);
        [Tooltip("Seconds for one arc to expand from its smallest to the edge of the icon.")]
        [Min(0.05f)] public float arcCycleSec = 1.3f;
        [Tooltip("How small an arc starts, as a fraction of full size. This is the 'small to big' " +
                 "read — at 1 the arcs would only fade, not expand.")]
        [Range(0.02f, 0.9f)] public float arcMinScale = 0.12f;

        [Header("Behaviour")]
        [Tooltip("Run the icon animations. Off leaves both icons on a still frame.")]
        public bool animate = true;

        [Tooltip("Wording, matched to ExperimentDirector's block banners so the chip and the start " +
                 "prompt never name the same weapon differently. Shown during PRACTICE only — see " +
                 "nameDuringPracticeOnly.")]
        public string laserLabel      = "LASER";
        public string shockwaveLabel = "SHOCKWAVE CANNON";

        [Tooltip("Drop the weapon's NAME in main rounds and show a larger icon alone.\n\n" +
                 "Practice is where the participant is still learning which task they are being " +
                 "asked to do, so the name earns its space there. By the main run they know, and " +
                 "the name is a block of text sitting over the play area for the whole session — " +
                 "the icon says the same thing without competing for the screen.")]
        public bool nameDuringPracticeOnly = true;

        [Tooltip("Icon size while the name is beside it.")]
        [Min(8f)] public float iconSizeWithName = 52f;
        [Tooltip("Icon size when it stands alone. Larger, since it is now carrying the whole " +
                 "message rather than decorating a label.")]
        [Min(8f)] public float iconSizeAlone = 84f;

        const int RingCount = 3;

        // Whatever the icon is currently drawn at. Follows the two sizes above rather than being
        // fixed, so switching between named and icon-only rescales the artwork instead of leaving
        // a small icon in a large plate.
        float _iconSize;
        float _boltHeight;

        PerturbationController _perturbation;
        UIManager              _uiManager;

        // Both canvases scale with the screen, and they do not necessarily scale by the same
        // factor, so the alignment has to be redone when the window changes size rather than
        // trusting the first measurement to hold.
        Vector2Int      _alignedFor = new Vector2Int(-1, -1);
        readonly Vector3[] _corners = new Vector3[4];

        GameObject     _root;
        Image          _plate;
        RectTransform  _iconRect;
        LayoutElement  _iconLayout;
        TMP_Text       _label;
        Image          _rail, _bolt;
        Image[]        _rings;
        Texture2D      _beamTex, _arcTex;

        // Null until the first Update, so the first pass always applies rather than trusting a
        // default that happens to match.
        WeaponKind? _shown;
        bool?       _shownWithName;

        void Awake()
        {
            _perturbation = FindAnyObjectByType<PerturbationController>();
            Build();
        }

        // Not Awake: both of these depend on components that are themselves auto-created — the
        // cannon from LaserFirer.Awake, the score text from UIManager.Awake — and Unity gives no
        // ordering guarantee between two Awakes. Asking too early would silently fall back to the
        // defaults and quietly ignore whatever the scene actually has.
        void Start()
        {
            if (matchCannonColors)
            {
                var cannon = FindAnyObjectByType<ShockwaveCannon>();
                if (cannon != null)
                {
                    arcCoreColor = cannon.arcCoreColor;
                    arcEdgeColor = cannon.arcEdgeColor;
                    _shown = null;   // re-apply next frame, with the colours that just arrived
                }
            }

            _uiManager = FindAnyObjectByType<UIManager>();
        }

        /// <summary>
        /// Puts the chip's vertical centre on the score readout's, so the two read as one row.
        ///
        /// Measured from the live RectTransform rather than hard-coded: the score text is placed by
        /// hand in the scene, on a different canvas with its own scaler, so any constant here would
        /// be a guess that drifts the moment either is nudged. Both canvases are ScreenSpaceOverlay,
        /// which is what makes the round-trip through screen space exact.
        /// </summary>
        void AlignChip()
        {
            var rt = (RectTransform)_root.transform;
            ApplySideAnchor(rt);
            rt.anchoredPosition = new Vector2(SideOffsetX, -topOffset);

            // Stamped even when the alignment below bails out, so a scene with no score text
            // settles on topOffset instead of re-measuring every frame.
            _alignedFor = new Vector2Int(Screen.width, Screen.height);
            if (!alignToScoreText) return;

            RectTransform score = _uiManager != null && _uiManager.scoreText != null
                ? _uiManager.scoreText.rectTransform
                : null;
            var canvasRect = _root.transform.parent as RectTransform;
            if (score == null || canvasRect == null) return;

            // The pivot is the box's top-left corner, not its middle, so centre it off the corners.
            score.GetWorldCorners(_corners);
            Vector3 worldCentre = (_corners[0] + _corners[2]) * 0.5f;

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, worldCentre);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screen, null, out Vector2 local)) return;

            // The plate's height is produced by a ContentSizeFitter, which runs during the canvas
            // layout pass — so on the frame the chip changes shape, rect.height is still the old
            // one. Force the rebuild before measuring, or the chip aligns to the size it used to be.
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            // The chip anchors to the canvas's top edge and pivots on its own, so getting its
            // CENTRE onto local.y means offsetting by half of each.
            float y = local.y + rt.rect.height * 0.5f - canvasRect.rect.height * 0.5f;

            // The score text sits flush with the top of the screen, so a chip taller than it would
            // centre itself partly off-screen and lose its first line. Slipping a few pixels out of
            // alignment is the lesser evil against being cropped.
            rt.anchoredPosition = new Vector2(SideOffsetX, Mathf.Min(y, -minTopMargin));
        }

        // Anchored and pivoted to whichever edge the chip hangs from, so the plate grows inward
        // from that edge as its contents change size — pivoting on the far side instead would let
        // a wider chip slide off the screen.
        void ApplySideAnchor(RectTransform rt)
        {
            float x = anchorRight ? 1f : 0.5f;
            rt.anchorMin = rt.anchorMax = new Vector2(x, 1f);
            rt.pivot     = new Vector2(x, 1f);
        }

        float SideOffsetX => anchorRight ? -sideMargin : 0f;

        void OnDestroy()
        {
            if (_root    != null) Destroy(_root);
            if (_beamTex != null) Destroy(_beamTex);
            if (_arcTex  != null) Destroy(_arcTex);
        }

        /// <summary>
        /// Visible only while a round is live. Between rounds the director owns the screen with a
        /// full-screen prompt, and a weapon chip floating over "Press SPACE to start" would be
        /// naming a weapon nobody is holding yet. With no director at all — a hand-played scene —
        /// it simply stays up.
        /// </summary>
        bool ShouldShow =>
            ExperimentDirector.Instance == null || ExperimentDirector.Instance.RoundActive;

        void Update()
        {
            if (_root == null) return;

            bool show = ShouldShow;
            if (_root.activeSelf != show) _root.SetActive(show);
            if (!show) return;

            // Deliberately here rather than in Start: a ScreenSpaceOverlay canvas is not sized
            // until its first layout pass, so measuring in Start would divide by a zero-height
            // rect on the very first frame. By the first Update that the chip is visible, both
            // canvases have been laid out.
            if (_alignedFor.x != Screen.width || _alignedFor.y != Screen.height) AlignChip();

            WeaponKind weapon = _perturbation != null ? _perturbation.Weapon : WeaponKind.Laser;

            // The name is practice scaffolding. In a main round the icon carries it alone, larger.
            bool withName = !nameDuringPracticeOnly ||
                            (_perturbation != null && _perturbation.PracticeMode);

            if (_shown != weapon || _shownWithName != withName) Apply(weapon, withName);
            if (animate) Animate(weapon);
        }

        // ── Weapon switch ────────────────────────────────────────────────────

        void Apply(WeaponKind weapon, bool withName)
        {
            _shown         = weapon;
            _shownWithName = withName;
            bool isAm      = weapon == WeaponKind.Shockwave;

            _label.text  = isAm ? shockwaveLabel : laserLabel;
            _label.color = isAm ? Color.Lerp(arcEdgeColor, Color.white, 0.55f)
                                : Color.Lerp(laserColor,   Color.white, 0.35f);
            // SetActive, not just blanking the string: a disabled child is skipped by the layout
            // group entirely, which is what lets the plate collapse to a square around the icon.
            _label.gameObject.SetActive(withName);

            // The plate exists to give text a legible ground. The icon does not need one — it is
            // already a bright shape against a dark sky, and a black slab around it in every main
            // round is a permanent rectangle over the play area for no benefit. Disabled rather
            // than made transparent so it stops being drawn at all.
            _plate.enabled = withName;

            ApplyIconSize(withName ? iconSizeWithName : iconSizeAlone);

            // The plate just changed shape, so the vertical alignment it was measured against is
            // stale. Re-measuring costs one forced layout on a weapon or phase change.
            _alignedFor = new Vector2Int(-1, -1);

            _rail.gameObject.SetActive(!isAm);
            _bolt.gameObject.SetActive(!isAm);
            for (int i = 0; i < _rings.Length; i++) _rings[i].gameObject.SetActive(isAm);

            // A still frame for each, so the icon is correct even with animation off.
            _rail.color = WithAlpha(laserColor, 0.22f);
            _bolt.color = WithAlpha(laserColor, 1f);
            _bolt.rectTransform.anchoredPosition = Vector2.zero;
            for (int i = 0; i < _rings.Length; i++)
            {
                float f = i / (float)_rings.Length;
                _rings[i].rectTransform.localScale = Vector3.one * Mathf.Lerp(arcMinScale, 1f, f);
                _rings[i].color = WithAlpha(Color.Lerp(arcCoreColor, arcEdgeColor, f), 1f - f);
            }
        }

        /// <summary>
        /// Redraws the icon artwork at a new size, rather than scaling it. A localScale would leave
        /// the layout group measuring the old footprint, so the plate would not grow with the icon.
        /// Cheap, and only ever called on a weapon or phase change.
        /// </summary>
        void ApplyIconSize(float size)
        {
            _iconSize   = size;
            _boltHeight = size * 0.32f;

            _iconRect.sizeDelta       = new Vector2(size, size);
            _iconLayout.preferredWidth  = size;
            _iconLayout.preferredHeight = size;

            _rail.rectTransform.sizeDelta = new Vector2(size * 0.16f, size);
            _bolt.rectTransform.sizeDelta = new Vector2(size * 0.22f, _boltHeight);
            for (int i = 0; i < _rings.Length; i++)
                _rings[i].rectTransform.sizeDelta = new Vector2(size, size);
        }

        void Animate(WeaponKind weapon)
        {
            // unscaledTime, like every other animation in this project: the deliberate stutter runs
            // on the main thread and Time.time would carry it into the icon's motion, which would
            // turn the HUD into a second, unmeasured channel for the stimulus.
            float clock = Time.unscaledTime;

            if (weapon == WeaponKind.Shockwave)
            {
                for (int i = 0; i < _rings.Length; i++)
                {
                    // Staggered rather than synchronised, so the icon shows a chain of fronts
                    // leaving the same point — which is what the weapon actually does.
                    float t = Mathf.Repeat(clock / arcCycleSec + i / (float)_rings.Length, 1f);

                    // Ease-out on the same 2.2 exponent ShockwaveCannon expands the real arc
                    // with, so the icon and the blast decelerate identically.
                    float eased = 1f - Mathf.Pow(1f - t, 2.2f);

                    _rings[i].rectTransform.localScale =
                        Vector3.one * Mathf.Lerp(arcMinScale, 1f, eased);
                    _rings[i].color =
                        WithAlpha(Color.Lerp(arcCoreColor, arcEdgeColor, t), 1f - t);
                }
                return;
            }

            float p = Mathf.Repeat(clock / laserSweepSec, 1f);
            float travel = (_iconSize - _boltHeight) * 0.5f;
            _bolt.rectTransform.anchoredPosition = new Vector2(0f, Mathf.Lerp(-travel, travel, p));
            // Fades in off the muzzle and out at the far end, so the bolt reads as leaving rather
            // than as a block sliding up and snapping back to the bottom.
            _bolt.color = WithAlpha(laserColor, Mathf.Sin(p * Mathf.PI));
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        // ── Construction ─────────────────────────────────────────────────────

        void Build()
        {
            var canvasGo = new GameObject("WeaponIndicatorCanvas");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the gameplay HUD and its vignette, below ExperimentOverlay's 5000 — so the
            // between-round prompt's full-screen dim covers the chip instead of the chip floating
            // over it.
            canvas.sortingOrder = 4500;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight  = 0.5f;

            // No GraphicRaycaster: nothing here is clickable, and without one the chip cannot
            // swallow a click meant for the game.

            // The plate is a layout group with a size fitter, not a rect with hand-picked
            // dimensions. Its width and height are whatever the icon plus the label currently
            // measure, so an icon-only chip is a small square and a named one is a wide pill —
            // and neither is a guess that goes stale when the wording changes.
            _root = NewRect("WeaponChip", canvasGo.transform);
            var rt = _root.GetComponent<RectTransform>();
            ApplySideAnchor(rt);
            rt.anchoredPosition = new Vector2(SideOffsetX, -topOffset);

            _plate = _root.AddComponent<Image>();
            _plate.color         = new Color(0f, 0f, 0f, 0.38f);
            _plate.raycastTarget = false;

            var group = _root.AddComponent<HorizontalLayoutGroup>();
            group.padding = new RectOffset(platePadding.x, platePadding.x,
                                            platePadding.y, platePadding.y);
            group.spacing                = 10f;
            group.childAlignment         = TextAnchor.MiddleCenter;
            group.childForceExpandWidth  = false;
            group.childForceExpandHeight = false;

            var fitter = _root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            _beamTex = BuildBeamTexture(24, 96);
            _arcTex  = BuildArcTexture(128, arcDegrees: 220f, thicknessFrac: 0.13f);
            Sprite beam = ToSprite(_beamTex);
            Sprite arc   = ToSprite(_arcTex);

            // ── Icon box ─────────────────────────────────────────────────────
            // A LayoutElement, because the layout group sizes children from what they report and a
            // bare RectTransform full of manually-placed images reports nothing — the box would
            // collapse to zero and take the artwork with it.
            var icon  = NewRect("Icon", _root.transform);
            _iconRect = icon.GetComponent<RectTransform>();
            _iconLayout = icon.AddComponent<LayoutElement>();

            _rail = NewImage("Rail", icon.transform, beam, Vector2.one);
            _bolt = NewImage("Bolt", icon.transform, beam, Vector2.one);

            _rings = new Image[RingCount];
            for (int i = 0; i < RingCount; i++)
                _rings[i] = NewImage($"Arc_{i}", icon.transform, arc, Vector2.one);

            // ── Label ────────────────────────────────────────────────────────
            var labelGo = NewRect("Label", _root.transform);
            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.fontSize      = 28f;
            _label.fontStyle     = FontStyles.Bold;
            _label.alignment     = TextAlignmentOptions.Left;
            _label.raycastTarget = false;
            // One line, always. The plate is measured FROM this, so a wrap here would not overflow
            // the chip any more — it would silently make it twice as tall.
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            HudFont.Apply(_label);

            ApplyIconSize(iconSizeWithName);
            _root.SetActive(false);
        }

        static GameObject NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static Image NewImage(string name, Transform parent, Sprite sprite, Vector2 size)
        {
            var go = NewRect(name, parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;

            var img = go.AddComponent<Image>();
            img.sprite         = sprite;
            img.type           = Image.Type.Simple;
            img.raycastTarget  = false;
            img.preserveAspect = false;
            return img;
        }

        static Sprite ToSprite(Texture2D tex) =>
            Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

        // ── Icon textures (built once, at startup) ───────────────────────────

        /// <summary>A soft-edged vertical beam: alpha falls off across the width and rounds off at
        /// both ends, so a stretched copy reads as a glowing line rather than a rectangle.</summary>
        static Texture2D BuildBeamTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };

            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float ny   = 2f * Mathf.Abs(y / (float)(h - 1) - 0.5f);
                float ends = 1f - Mathf.SmoothStep(0.75f, 1f, ny);

                for (int x = 0; x < w; x++)
                {
                    float nx   = 2f * Mathf.Abs(x / (float)(w - 1) - 0.5f);
                    float core = 1f - Mathf.SmoothStep(0.15f, 1f, nx);
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(core * ends * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// One shock arc: a ring band centred straight down and spanning <paramref name="arcDegrees"/>,
        /// the same geometry <see cref="ShockwaveCannon"/> draws in world space, fading out toward
        /// both tips so a scaled copy dissolves at the ends the way the real front does.
        /// </summary>
        static Texture2D BuildArcTexture(int res, float arcDegrees, float thicknessFrac)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };

            const float radius = 0.42f;               // leaves room for the band plus antialiasing
            float halfBand  = Mathf.Max(0.01f, thicknessFrac) * 0.5f;
            float halfArc   = Mathf.Max(1f, arcDegrees) * 0.5f;
            const float down = -90f;                  // straight down, as in ShockwaveCannon

            var px = new Color32[res * res];
            for (int y = 0; y < res; y++)
            {
                float ny = y / (float)(res - 1) - 0.5f;
                for (int x = 0; x < res; x++)
                {
                    float nx = x / (float)(res - 1) - 0.5f;

                    float r    = Mathf.Sqrt(nx * nx + ny * ny);
                    float band = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Abs(r - radius) / halfBand);

                    float deg  = Mathf.Atan2(ny, nx) * Mathf.Rad2Deg;
                    float off  = Mathf.Abs(Mathf.DeltaAngle(deg, down));
                    float arc  = 1f - Mathf.SmoothStep(halfArc * 0.75f, halfArc, off);

                    px[y * res + x] = new Color32(255, 255, 255, (byte)(band * arc * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
