using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JndUfo
{
    /// <summary>
    /// A non-interactive full-screen message board for the gaps between play: the pre-round
    /// countdown, "round complete", and the end-of-session notice. Built from code at runtime,
    /// so it needs no prefab or scene wiring.
    ///
    /// Deliberately has no buttons or input of its own — the director controls how long each
    /// message stays up, and the participant never has to click through anything mid-study.
    /// </summary>
    public class ExperimentOverlay : MonoBehaviour
    {
        /// <summary>Pixels down from the top of the screen (1920x1080 reference) to the practice
        /// banner's top edge. It clears the score / round / weapon row above it. Its height and
        /// width both come from the label — it is a persistent reminder, not a headline, so it only
        /// has to stay legible.</summary>
        const float BannerTop = -112f;

        /// <summary>
        /// Where the practice hint sits, as an offset UP from screen centre, and its half-height.
        ///
        /// Anchored to the centre rather than to the top edge: the alert state shakes the text by
        /// writing anchoredPosition, and anything anchored to an edge is one careless
        /// <c>Vector2.zero</c> away from being flung against that edge and clipped. Measuring from
        /// the middle means the rest position is a small number near zero and a bug puts the text
        /// where it belongs anyway.
        ///
        /// 160 up clears the hit/miss callout's 220-tall box at dead centre, so a hint and the
        /// feedback for the shot it prompted never land on top of each other. The height is not
        /// specified at all — the plate takes it from the text.
        /// </summary>
        const float HintCentreOffsetY = 160f;

        /// <summary>Shortest an alert may be on screen before the trial ending can clear it. On
        /// shockwave the participant fires about a second after the stutter, so without a floor
        /// here the line they are meant to read would be taken away as they read it.</summary>
        const float HintPunchMinSec = 0.45f;

        GameObject      _root;
        TextMeshProUGUI _title;
        TextMeshProUGUI _body;

        // Separate from _root: the banner stays up *during* play, so it must not carry the
        // full-screen dim that the message panel uses.
        GameObject      _bannerRoot;
        TextMeshProUGUI _banner;

        /// <summary>
        /// The practice prompt has two states, and the gap between them is the whole point.
        ///
        /// <see cref="Idle"/> is a standing instruction that breathes quietly — present enough to
        /// tell the participant what they are looking for, dull enough that they stop attending to
        /// it and watch the game instead. <see cref="Alert"/> is the same line of text slammed to
        /// twice the size, shaken and strobed at the instant a stutter is delivered.
        ///
        /// It has to be that violent. The stimulus at the top of the QUEST+ grid is a single 250 ms
        /// frame, and near threshold it is a handful of milliseconds — a participant who has never
        /// knowingly seen one cannot learn what to look for from a caption that merely appears.
        /// The alert is not there to be read, it is there to be impossible to miss, so the two
        /// events fuse and the stutter itself becomes recognisable.
        /// </summary>
        enum HintMode { Off, Idle, Alert }

        TextMeshProUGUI _hint;
        RectTransform   _hintPlate;   // what moves and scales; see AnimateIdleHint
        Vector2         _hintRestPos;
        HintMode        _hintMode;
        string          _idleText  = "";
        float           _alertStart;      // unscaled time the alert began
        float           _alertHold;

        readonly Color _hintIdleColor  = new Color(1f, 0.74f, 0.24f, 0.85f);
        readonly Color _hintAlertColor = new Color(1f, 0.30f, 0.22f, 1f);

        [Header("Between-Trial Gate")]
        [Tooltip("Shown while the scene resets, pulsing. Reads as the run-up to the starting gun " +
                 "rather than as a stoppage — the participant is being counted in, not told off.")]
        public string gateHoldText = "READY…";
        [Tooltip("The starting gun itself. Shaken, then the gate lifts and firing comes back.")]
        public string gateGoText   = "GO!";
        [Tooltip("How much of the play area the reset veil covers. Opaque would hide the pan and " +
                 "the skybox spin, which are what sell the scene changing location.")]
        [Range(0f, 1f)] public float waitVeilAlpha = 0.62f;

        GameObject      _gateRoot;
        TextMeshProUGUI _gate;
        bool            _pulseHold;
        readonly Color  _gateHoldColor = new Color(1f, 0.74f, 0.24f, 1f);
        readonly Color  _gateGoColor   = new Color(0.45f, 1f, 0.6f, 1f);

        public void Show(string title, string body)
        {
            if (_root == null) Build();

            _title.text = title;
            _body.text  = body;

            // A title on its own (the "Press SPACE to start" prompt) gets centred instead of
            // sitting in the upper half with dead space where the body would have been.
            // Tall enough for an 84pt line either way; the centred variant just straddles zero.
            bool hasBody = !string.IsNullOrEmpty(body);
            _body.gameObject.SetActive(hasBody);
            _title.rectTransform.offsetMin = new Vector2(80f, hasBody ?  20f : -80f);
            _title.rectTransform.offsetMax = new Vector2(-80f, hasBody ? 160f :  80f);

            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>
        /// A small label pinned to the top of the screen that stays visible while the game is
        /// being played — used to keep "PRACTICE ROUND" on screen so a participant is never
        /// unsure whether their shots are counting.
        /// </summary>
        public void ShowBanner(string text)
        {
            if (_bannerRoot == null) Build();

            _banner.text = text;
            _bannerRoot.SetActive(true);
        }

        public void HideBanner()
        {
            if (_bannerRoot != null) _bannerRoot.SetActive(false);
        }

        /// <summary>
        /// Flashes a one-line coaching line for <paramref name="holdSec"/>, then fades it.
        ///
        /// Practice only — the caller enforces that, and it matters: the hint fires on the stutter,
        /// so in a main round it would be a second, unmissable copy of the very stimulus QUEST+ is
        /// measuring the detectability of. In practice that is the point, since a participant who
        /// has never knowingly seen a stutter cannot tell what they are being asked to look for.
        ///
        /// Retriggering restarts the hold rather than stacking, so a run of crossings in quick
        /// succession leaves one steady line instead of a flicker.
        /// </summary>
        public void ShowIdleHint(string text)
        {
            if (_hint == null) Build();
            if (_hint == null) return;

            _idleText = text;

            // An alert in flight owns the text until it settles — retriggering the standing prompt
            // underneath it every frame would cancel the punch on the frame after it started.
            if (_hintMode == HintMode.Alert) return;

            if (_hintMode != HintMode.Idle || _hint.text != text)
            {
                _hint.text = text;
                _hintPlate.gameObject.SetActive(true);
                _hintMode  = HintMode.Idle;
            }
        }

        /// <summary>
        /// The stutter just landed: slam the prompt to alert. Retriggering restarts the punch, so a
        /// run of crossings in quick succession reads as repeated hits rather than one long wobble.
        /// </summary>
        public void FlashHint(string text, float holdSec)
        {
            if (_hint == null) Build();
            if (_hint == null) return;

            _hint.text  = text;
            _hintPlate.gameObject.SetActive(true);
            _hintMode   = HintMode.Alert;
            _alertStart = Time.unscaledTime;
            // How long the PUNCH runs, not how long the line stays up — the line now holds for the
            // rest of the trial. This is only the floor on how long an alert must be readable
            // before the trial ending is allowed to take it away.
            _alertHold    = Mathf.Max(HintPunchMinSec, holdSec);
        }

        /// <summary>Clears the standing prompt but lets an alert already in flight finish — the
        /// participant fires the moment the stutter lands, which takes firing away on the very next
        /// frame, and cutting the alert there would make it unreadable exactly when it matters.</summary>
        public void HideIdleHint()
        {
            if (_hintMode == HintMode.Idle) HideHint();
            _idleText = "";
        }

        public void HideHint()
        {
            // Already down. The director calls this on every frame of every main round, so the
            // reset below has to be a one-time cost and not a per-frame transform write on a
            // canvas that would then re-batch for the rest of the session.
            if (_hintMode == HintMode.Off) { _idleText = ""; return; }

            if (_hintPlate != null)
            {
                _hintPlate.gameObject.SetActive(false);
                _hintPlate.anchoredPosition = _hintRestPos;
                _hintPlate.localScale       = Vector3.one;
            }
            _hintMode = HintMode.Off;
            _idleText = "";
        }

        // ── Between-trial gate: "READY…" while the scene resets, then "GO!" ───

        /// <summary>
        /// Covers the between-trial reset: a veil over the play area and a pulsing "READY…".
        ///
        /// It masks as well as instructs. The camera pan, the tower move and whatever the runtime
        /// does with the frame it happens to be on all land in this window, and a hitch there is
        /// visible motion the participant has no way to distinguish from a deliberate stutter.
        /// Under a veil there is nothing moving to judge, so an accidental long frame simply is not
        /// perceptible — which is cheaper and more reliable than trying to make the reset free.
        ///
        /// Deliberately NOT opaque: the pan and the skybox spin still have to read as the scene
        /// changing location, or the participant stops believing the tower moved at all.
        /// </summary>
        public void ShowGateHold()
        {
            if (_gateRoot == null) Build();
            if (_gateRoot == null) return;

            _gate.text  = gateHoldText;
            _gate.color = _gateHoldColor;
            _gateRoot.SetActive(true);
            _pulseHold  = true;
            _gate.rectTransform.localScale = Vector3.one;
        }

        public void HideGate()
        {
            if (_gateRoot != null) _gateRoot.SetActive(false);
            _pulseHold = false;
        }

        /// <summary>
        /// Swaps "READY…" for "GO!", shakes it for <paramref name="seconds"/>, then clears the
        /// veil. Yield on this from the reveal sequence and hand firing back when it returns, so
        /// "the word left the screen" and "shots count again" are the same instant.
        ///
        /// The shockwave clock arms off the firing-enabled edge, so this also gives that task a
        /// defined starting gun rather than letting a trial begin mid-pan.
        /// </summary>
        public IEnumerator GoBeat(float seconds)
        {
            if (_gateRoot == null) Build();
            if (_gateRoot == null) yield break;

            _pulseHold  = false;
            _gate.text  = gateGoText;
            _gate.color = _gateGoColor;
            _gateRoot.SetActive(true);

            // Unscaled throughout: a stutter is a main-thread block, and timing the starting gun
            // off a scaled clock would make the countdown itself carry the stimulus.
            float elapsed = 0f;
            var rect = _gate.rectTransform;

            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, seconds));

                // Shake that decays to nothing, so the word settles instead of being cut off
                // mid-wobble when the gate lifts.
                float decay = 1f - t;
                rect.anchoredPosition = new Vector2(
                    Mathf.Sin(elapsed * 46f) * 14f * decay,
                    Mathf.Sin(elapsed * 37f) * 9f  * decay);
                rect.localScale = Vector3.one * (1f + 0.16f * decay * Mathf.Abs(Mathf.Sin(elapsed * 18f)));

                yield return null;
            }

            rect.anchoredPosition = Vector2.zero;
            rect.localScale       = Vector3.one;
            HideGate();
        }

        // Only does work while something is actually on screen.
        void Update()
        {
            if (_pulseHold && _gate != null)
            {
                // A slow pulse rather than a hard blink: it has to be unmistakably "not yet"
                // without becoming a flicker the eye tries to time.
                float a = 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6.5f));
                _gate.color = new Color(_gateHoldColor.r, _gateHoldColor.g, _gateHoldColor.b, a);
            }

            if (_hint == null || _hintMode == HintMode.Off) return;

            if (_hintMode == HintMode.Idle) { AnimateIdleHint(); return; }
            AnimateAlertHint();
        }

        // Breathing, not blinking. It has to survive being on screen for the whole practice phase
        // without becoming something the participant tries to time.
        void AnimateIdleHint()
        {
            float phase = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 2.4f);

            _hint.color = new Color(_hintIdleColor.r, _hintIdleColor.g, _hintIdleColor.b,
                                     Mathf.Lerp(0.5f, _hintIdleColor.a, phase));
            // The PLATE moves and scales, never the text — the text is a layout child, so anything
            // written to its rect is overwritten on the next layout pass. Scaling the plate also
            // takes the background with it, which is the point of having one.
            _hintPlate.localScale       = Vector3.one * Mathf.Lerp(0.98f, 1.03f, phase);
            _hintPlate.anchoredPosition = _hintRestPos;
        }

        void AnimateAlertHint()
        {
            float t = Time.unscaledTime - _alertStart;

            // The alert OWNS the rest of the trial. It never hands back to the standing prompt —
            // the punch used to expire after ~1.1s and revert to "SPOT THE STUTTER" while the
            // participant was still deciding, which is worse than useless: their measured reaction
            // time averages 1.13s, so the instruction was being replaced by the wrong instruction
            // at almost exactly the moment they acted on it. The prompt comes back only when a new
            // trial arms it.
            //
            // The one thing that clears it is the trial ending — HideIdleHint empties _idleText —
            // and even then not until the punch has run, or a participant who answers on the
            // stutter frame would never get to read what they answered.
            if (t >= _alertHold && string.IsNullOrEmpty(_idleText)) { HideHint(); return; }

            // Everything rides one fast exponential, so the punch, the shake and the strobe spend
            // themselves together instead of drifting apart into three separate wobbles.
            float decay = Mathf.Exp(-6f * t);

            // Slams in at ~1.95x and wobbles down, rather than growing into place.
            _hintPlate.localScale =
                Vector3.one * (1f + 0.95f * decay * Mathf.Abs(Mathf.Cos(t * 26f)));

            float amp = 26f * decay;
            _hintPlate.anchoredPosition = _hintRestPos +
                new Vector2(Mathf.Sin(t * 63f) * amp, Mathf.Sin(t * 48f) * amp * 0.6f);

            // Strobes to white over the alert colour while the punch lasts. It settles INTO the
            // alert colour rather than back toward the standing prompt's amber — the instruction on
            // screen for the rest of the trial is "fire", so it has to keep looking like one.
            float strobe = (0.5f + 0.5f * Mathf.Sin(t * 40f)) * decay;
            Color hot    = Color.Lerp(_hintAlertColor, Color.white, strobe);

            // A shallow breath once the punch is spent, so a line that now stays up for the rest of
            // the trial reads as live rather than as something stuck on screen.
            float alive = 0.82f + 0.18f * (0.5f + 0.5f * Mathf.Sin(t * 7f));
            _hint.color = new Color(hot.r, hot.g, hot.b, Mathf.Lerp(alive, 1f, decay));
        }

        void Build()
        {
            var canvasGo = new GameObject("ExperimentOverlayCanvas");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;                  // above the gameplay HUD and vignette

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight  = 0.5f;

            // No GraphicRaycaster: nothing here is clickable, and without one the overlay can't
            // swallow input meant for the game.
            _root = NewRect("Root", canvasGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var dim = _root.AddComponent<Image>();
            dim.color         = new Color(0f, 0f, 0f, 0.72f);
            dim.raycastTarget = false;

            _title = MakeText(_root.transform, "Title", "", 84f, FontStyles.Bold,
                              new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(80f, 20f), new Vector2(-80f, 160f),
                              new Color(0.95f, 0.96f, 1f));

            // Top-aligned in a tall box rather than centred in a short one: the body is a single
            // line for most prompts but four for the shockwave task instructions, and text that
            // grows around its own centre would climb into the title as it got longer. Anchored to
            // its top edge, every prompt starts at the same place and simply runs further down.
            _body = MakeText(_root.transform, "Body", "", 40f, FontStyles.Normal,
                             new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                             new Vector2(80f, -300f), new Vector2(-80f, 10f),
                             new Color(0.72f, 0.76f, 0.84f));
            _body.alignment = TextAlignmentOptions.Top;

            _root.SetActive(false);

            // ── Top banner (no dim; shown during play) ───────────────────────
            // Sits UNDER the top row of HUD text — the score, the round counter and the weapon
            // chip all live on the first ~104px — rather than above it. It used to own the top of
            // the screen and push everything else down; a slim strip below them keeps all four
            // readable at once and gives the play area back the height.
            // Amber: the banner only ever marks practice, so it reads as "not the real thing". It
            // hugs its label rather than spanning the screen — a full-width strip for six words was
            // the loudest thing on a screen whose whole job is to be judged for timing.
            _banner = MakePlate(canvasGo.transform, "Banner",
                                 new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                 new Vector2(0f, BannerTop),
                                 26f, FontStyles.Bold,
                                 new Color(0.97f, 0.96f, 0.94f),
                                 new Color(0.60f, 0.35f, 0.05f, 0.72f),
                                 padX: 28, padY: 8,
                                 out RectTransform bannerPlate);
            _bannerRoot = bannerPlate.gameObject;
            _bannerRoot.SetActive(false);

            // ── Practice hint (shown during play) ────────────────────────────
            // Idle shares the banner's amber, so the two read as one voice — scaffolding that goes
            // away when the real run starts — rather than as game feedback.
            _hint = MakePlate(canvasGo.transform, "PracticeHint",
                               new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                               new Vector2(0f, HintCentreOffsetY),
                               44f, FontStyles.Bold,
                               _hintIdleColor,
                               new Color(0.03f, 0.03f, 0.05f, 0.55f),
                               padX: 34, padY: 10,
                               out _hintPlate);
            _hintPlate.gameObject.SetActive(false);

            // Captured once, from the rect the plate was placed at. The shake and every reset work
            // RELATIVE to this — writing Vector2.zero instead is what used to throw the text
            // against the top of the screen and clip it.
            _hintRestPos = _hintPlate.anchoredPosition;

            // ── Between-trial gate (veil + "READY…" / "GO!") ─────────────────
            _gateRoot = NewRect("Gate", canvasGo.transform,
                                 Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var veil = _gateRoot.AddComponent<Image>();
            veil.color         = new Color(0.02f, 0.02f, 0.05f, waitVeilAlpha);
            veil.raycastTarget = false;

            _gate = MakeText(_gateRoot.transform, "GateText", "", 150f, FontStyles.Bold,
                             new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                             new Vector2(60f, -110f), new Vector2(-60f, 110f),
                             _gateHoldColor);

            _gateRoot.SetActive(false);
        }

        static GameObject NewRect(string name, Transform parent,
                                   Vector2 anchorMin, Vector2 anchorMax,
                                   Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return go;
        }

        static TextMeshProUGUI MakeText(Transform parent, string name, string text,
                                         float size, FontStyles style,
                                         Vector2 anchorMin, Vector2 anchorMax,
                                         Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var go = NewRect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text          = text;
            t.fontSize      = size;
            t.fontStyle     = style;
            t.alignment     = TextAlignmentOptions.Center;
            t.color         = color;
            t.raycastTarget = false;
            HudFont.Apply(t);
            return t;
        }

        /// <summary>
        /// A text plate that is exactly as wide as its own text, plus padding.
        ///
        /// The alternative — a rect sized by hand — is what left the banner spanning the whole
        /// screen for a six-word label while the weapon chip sat in a box sized for a different
        /// string entirely. A ContentSizeFitter over a layout group means the plate is derived from
        /// the text rather than guessed alongside it, so every caption on screen gets the same
        /// treatment whatever it happens to say.
        ///
        /// Returns the text; <paramref name="plate"/> is the rect to move, scale or hide. Animate
        /// the PLATE, never the text — the text is a layout child, and anything written to its
        /// position is overwritten on the next layout pass.
        /// </summary>
        static TextMeshProUGUI MakePlate(Transform parent, string name,
                                          Vector2 anchor, Vector2 pivot, Vector2 position,
                                          float size, FontStyles style,
                                          Color textColor, Color plateColor,
                                          int padX, int padY,
                                          out RectTransform plate)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);

            plate = root.GetComponent<RectTransform>();
            plate.anchorMin = plate.anchorMax = anchor;
            plate.pivot     = pivot;
            plate.anchoredPosition = position;

            var img = root.AddComponent<Image>();
            img.color         = plateColor;
            img.raycastTarget = false;

            var group = root.AddComponent<HorizontalLayoutGroup>();
            group.padding                = new RectOffset(padX, padX, padY, padY);
            group.childAlignment         = TextAnchor.MiddleCenter;
            group.childForceExpandWidth  = false;
            group.childForceExpandHeight = false;

            var fitter = root.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject(name + "Text", typeof(RectTransform));
            textGo.transform.SetParent(root.transform, false);

            var t = textGo.AddComponent<TextMeshProUGUI>();
            t.fontSize         = size;
            t.fontStyle        = style;
            t.alignment        = TextAlignmentOptions.Center;
            t.color            = textColor;
            t.raycastTarget    = false;
            // Without this the fitter and the wrapper argue: TMP reports a preferred width, the
            // plate shrinks to it, the text wraps, the preferred height changes, and the plate
            // resizes again. One line, one measurement.
            t.textWrappingMode = TextWrappingModes.NoWrap;
            HudFont.Apply(t);
            return t;
        }
    }
}
