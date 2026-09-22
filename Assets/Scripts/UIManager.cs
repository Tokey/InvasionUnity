using System.Collections;
using System.Collections.Generic;
using JndUfo;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UIManager : MonoBehaviour
{
    [Header("References")]
    public ScoreManager scoreManager;
    public GameManager  gameManager;
    [Tooltip("The scene's HUD text. It used to carry the score; it is now the anchor the hit/miss " +
             "tally is built over, and the text component itself is switched off — see " +
             "BuildTally. Leave it assigned: its RectTransform is what positions the tally.")]
    public TMP_Text     scoreText;

    [Tooltip("The fill of the HUD's progress bar. Anchored to the left edge of its track; its " +
             "right anchor is driven from 0 to 1 as the phase progresses. Practice fills by " +
             "rounds completed; the main run fills by how close QUEST+ is to stopping — see " +
             "RoundProgress for the formula.")]
    public RectTransform progressFill;

    [Tooltip("ONE font for the whole game. Assign a TMP Font Asset here and everything follows it " +
             "— the score readout below, the hit/miss callout, the weapon chip, the practice " +
             "banner and hints, and the between-round prompts.\n\n" +
             "Leave it empty to keep whatever font scoreText is already set to, which is what " +
             "happens today.")]
    public TMP_FontAsset hudFont;

    [Header("Debug")]
    [Tooltip("Show the live staircase readout. OFF for anything a participant plays, and off by " +
             "default for that reason: the line spells out the stutter size QUEST+ is about to " +
             "present, the threshold estimate so far and the trial count. A participant who reads " +
             "it is being told the answer to the question the staircase is asking, and every trial " +
             "after that measures their reading rather than their perception.")]
    public bool showDebug = false;

    public TMP_Text debugText;

    /// <summary>Whether the debug readout is being drawn at all. PerturbationController checks it
    /// before building the line — the string is rebuilt on every response and once a second while
    /// a round runs, and none of that work is worth doing for text nobody can see.</summary>
    public bool DebugVisible => showDebug && debugText != null;

    [Header("Flash Timing")]
    [Min(0f)]    public float flashHold = 0.40f;
    [Min(0.01f)] public float flashFade = 0.50f;

    [Header("Hit Vignette  (Green)")]
    public Color hitFlashColor = new(0.2f, 1f, 0.25f, 1f);
    [Range(0f, 1f)] public float hitAlpha = 0.6f;

    [Header("Miss Vignette  (Red)")]
    public Color missRedColor = new(1f, 0.08f, 0.08f, 1f);
    [Range(0f, 1f)] public float missRedAlpha = 0.75f;

    [Header("Vignette Shape")]
    [Tooltip("Normalized distance from center where vignette begins. 0.5 = half screen.")]
    [Range(0f, 0.9f)] public float vignetteInnerRadius = 0.45f;
    public int vignetteResolution = 256;

    [Header("Score Text Pulse")]
    [Tooltip("Scale peak for a positive beat (hit). Miss uses 1/peak as the trough.")]
    public float scoreBeatPeak = 1.45f;

    [Header("Progress Bar")]
    [Tooltip("How lazily the fill follows its target, as a SmoothDamp smooth-time in seconds: " +
             "it covers most of the distance in about this long and settles in two to three " +
             "times it. A spring rather than a fixed-length tween so that a second response " +
             "landing while the bar is still moving just bends its path — a tween restarted " +
             "from rest would visibly stall and set off again.")]
    [Min(0f)] public float progressSmoothSec = 0.5f;

    [Tooltip("Re-place and restyle the scene's progress bar at startup: a long bar across the " +
             "bottom of the screen with its name above it, instead of the short chip it sits at " +
             "in the scene. Done here rather than by hand so the numbers below are the single " +
             "source of truth and cannot drift from a nudged RectTransform. Uncheck to use " +
             "whatever the scene has.")]
    public bool layOutProgressBar = true;

    [Tooltip("Bar width as a fraction of the screen. It is at the bottom, away from the play " +
             "area, so it can afford to be long — and a long bar is what makes a small amount of " +
             "progress visible at all.")]
    [Range(0.2f, 1f)] public float progressWidthFraction = 0.66f;

    [Tooltip("Bar height in pixels. The scene's 12 is a hairline at 1080p.")]
    [Min(2f)] public float progressHeight = 28f;

    [Tooltip("Pixels from the bottom of the screen to the bottom of the bar.")]
    [Min(0f)] public float progressBottomMargin = 48f;

    [Tooltip("Written above the bar. The bar alone does not say what it is counting, and a " +
             "participant who reads it as a timer will pace themselves against it.")]
    public string progressLabelText = "PROGRESS";

    [Tooltip("Show the word during PRACTICE only, and leave the bar unlabelled in main rounds.\n\n" +
             "Practice is where the bar has to be explained — it is the first time the participant " +
             "sees one and they have no way to know what it counts. By the main run they do, and " +
             "the word is then a block of text sitting on screen for the rest of the session with " +
             "nothing left to say. Same reasoning as WeaponIndicator.nameDuringPracticeOnly.")]
    public bool progressLabelDuringPracticeOnly = true;

    [Min(6f)] public float progressLabelSize = 32f;
    [Tooltip("Pixels between the top of the bar and the baseline box of its label.")]
    [Min(0f)] public float progressLabelGap = 8f;

    [Tooltip("Filled part. Bright and nearly opaque — this is the part that has to be readable " +
             "at a glance from the middle of the screen.")]
    public Color progressFillColor  = new(0.55f, 0.95f, 1f, 0.97f);
    [Tooltip("Unfilled part. Dark enough that the boundary between the two is the thing you see.")]
    public Color progressTrackColor = new(0f, 0f, 0f, 0.62f);
    public Color progressLabelColor = new(0.90f, 0.96f, 1f, 0.92f);

    [Header("Hit / Miss Tally")]
    [Tooltip("Replace the score readout with a penalty-shootout strip: one pip per response, in " +
             "order, filled green for a hit and red for a miss, hollow for a slot not reached " +
             "yet.\n\n" +
             "A score is a single number that mixes the two — a participant cannot tell 8 hits " +
             "and 2 misses from 9 hits and 4 misses by looking at it, and the weighting is a " +
             "config choice they were never told. The strip shows the run itself, in the order " +
             "it happened, which a pair of counts cannot. The score is still computed and still " +
             "logged; it is only off the HUD.")]
    public bool showHitMissTally = true;

    [Tooltip("How many responses the strip holds. A block can run to maxTrials (50 today), which " +
             "is far too many pips to read, so the strip is a moving window: once it is full the " +
             "oldest pip drops off the left and the newest appears on the right.")]
    [Range(3, 20)] public int tallySlots = 12;

    [Tooltip("Diameter of one pip in pixels.")]
    [Min(6f)] public float tallyPipSize = 34f;
    [Tooltip("Pixels between pips.")]
    [Min(0f)] public float tallyPipSpacing = 12f;
    [Tooltip("Size of the coloured centre as a fraction of the pip, so the hollow ring stays " +
             "visible around a filled one and the strip reads as slots rather than as dots.")]
    [Range(0.3f, 1f)] public float tallyFillScale = 0.74f;

    public Color tallyHitColor  = new(0.30f, 1f, 0.45f, 1f);
    public Color tallyMissColor = new(1f, 0.32f, 0.32f, 1f);
    [Tooltip("The empty ring. Bright enough to be found against the sky, dim enough not to " +
             "compete with the filled pips beside it.")]
    public Color tallyEmptyColor = new(1f, 1f, 1f, 0.34f);

    [Tooltip("How far the newest pip overshoots as it lands, and how long the landing takes. " +
             "The pip appearing IS the feedback, so it has to be caught out of the corner of the " +
             "eye — the participant is looking at the middle of the screen, not at the strip.")]
    [Range(1f, 2.5f)] public float tallyPopScale = 1.7f;
    [Min(0.05f)]      public float tallyPopSec   = 0.34f;

    [Header("Shockwave Vignette  (Violet)")]
    public Color shockwaveFlashColor = new(0.75f, 0.45f, 1f, 1f);
    [Range(0f, 1f)] public float shockwaveAlpha = 0.85f;

    [Header("Timed Outcome Callout")]
    [Tooltip("Fired inside the response window — the stutter was noticed in time.")]
    public string calloutShockwaveHitText = "DESTROYED!";
    [Tooltip("Fired before the round's first stutter, so there was nothing to answer yet. The " +
             "round carries on.")]
    public string calloutShockwaveEarlyText = "TOO EARLY!";
    [Tooltip("Fired after the window had closed. The round carries on and the stutter comes again.")]
    public string calloutShockwaveLateText = "TOO LATE!";
    [Tooltip("The round ran out with no shot — shockwave's last window closed unanswered, or a " +
             "laser round crossed the tower too many times or ran out its clock.")]
    public string calloutOutOfTimeText = "OUT OF TIME!";
    [Tooltip("Early and late are both failures, but they are failures of opposite kinds. Amber " +
             "rather than the miss red keeps them legible as 'wrong timing' rather than 'bad aim'.")]
    public Color calloutShockwaveFailColor = new(1f, 0.68f, 0.2f, 1f);

    [Header("Hit / Miss Callout")]
    public string calloutHitText  = "Hit!";
    public string calloutMissText = "Miss!";
    [Tooltip("Total time the callout stays on screen (seconds), bounce plus hold.")]
    public float calloutDuration = 1f;
    [Tooltip("How long the bounce itself runs. The rest of calloutDuration is a still hold.")]
    public float calloutBounceDuration = 0.45f;
    public float calloutFontSize = 120f;
    public Color calloutHitColor  = new(0.25f, 1f, 0.4f, 1f);
    public Color calloutMissColor = new(1f, 0.25f, 0.25f, 1f);

    // ── Private ───────────────────────────────────────────────────────────
    Image     _vignette;
    Canvas    _canvas;
    TMP_Text      _callout;
    RectTransform _calloutRect;
    Coroutine     _calloutCoroutine;
    Coroutine _flashCoroutine;
    Coroutine            _scoreCoroutine;
    Texture2D            _vignetteTex;
    Color                _scoreBaseColor;
    Vector3              _scoreBaseScale;

    // What the bar is showing, where it is heading, and how fast — the spring's state, stepped
    // in Update only while the two differ. The target is the most the phase has reached: the bar
    // never goes backward — see RoundProgress — so a phase is over when it reaches 1.
    float _progressShown;
    float _progressPeak;
    float _progressVelocity;
    bool  _progressSettled = true;

    // Penalty-shootout tally, built over the score text's slot. The outcomes list is the moving
    // window — oldest first, at most tallySlots long — and _tallyCounted is how many responses
    // the phase had produced when it was last appended to, which is how a new response is told
    // apart from a repeat refresh of the same one.
    readonly List<bool> _tallyOutcomes = new List<bool>();
    int                 _tallyCounted;
    RectTransform[]     _tallyPips;
    Image[]             _tallyRings;
    Image[]             _tallyFills;
    Coroutine           _tallyCoroutine;
    Texture2D           _discTex, _ringTex;

    // The label above the progress bar, built once by ApplyProgressLayout.
    TMP_Text _progressLabel;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    void Awake()
    {
        ResolveReferences();

        // The hand-placed HUD is restyled here rather than by hand in the Inspector, so hudFont is
        // genuinely one switch for the whole screen. Without this the score readout would be the
        // one text that ignored it — and it sits directly beside the weapon chip, which is
        // exactly where a font mismatch is most obvious.
        if (hudFont != null)
        {
            HudFont.Apply(scoreText);
            HudFont.Apply(debugText);
        }

        BuildOverlays();
        if (scoreText != null)
        {
            _scoreBaseColor = scoreText.color;
            _scoreBaseScale = scoreText.transform.localScale;
        }

        // Off before the first frame is drawn, not on the first Update — a debug line that flashes
        // up for a frame at the top of a session is still a line the participant can read.
        if (debugText != null) debugText.gameObject.SetActive(showDebug);

        if (showHitMissTally) BuildTally();
        ApplyProgressLayout();
    }

    void OnEnable()
    {
        ResolveReferences();
        if (_vignette == null) BuildOverlays();
        if (scoreManager != null) scoreManager.OnScored += HandleScored;
        RefreshScoreText();
        RefreshProgress();
    }

    void Start()
    {
        RefreshScoreText();
        RefreshProgress();
    }

    void OnDisable()
    {
        if (scoreManager != null) scoreManager.OnScored -= HandleScored;
    }

    void OnDestroy()
    {
        if (_vignette    != null) Destroy(_vignette.gameObject);
        if (_vignetteTex != null) Destroy(_vignetteTex);
        if (_callout     != null) Destroy(_callout.gameObject);
        if (_discTex     != null) Destroy(_discTex);
        if (_ringTex     != null) Destroy(_ringTex);
    }

    public void SetDebugText(string text)
    {
        if (!showDebug || debugText == null) return;
        debugText.text = text;
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void DisplayShotResult(float shotScore, float totalScore, float distance,
                                   Vector3 hitWorldPos = default, Vector3 towerWorldPos = default)
    {
        Debug.Log($"[UIManager] shot={shotScore:0.00} total={totalScore:0.00} dist={distance:0.00}");
        RefreshScoreText();

        bool isMiss = scoreManager == null || shotScore < scoreManager.HitPoints;
        if (isMiss) TriggerMissFlash();
        else        TriggerHitFlash();

        ShowCallout(!isMiss);
    }

    /// <summary>
    /// The result of a response decided by timing rather than aim — every shockwave response,
    /// and a laser round that ran out with no shot. Several failure texts rather than one: the
    /// participant can fail by answering too early, too late, or not at all, and those are
    /// different mistakes that need different corrections, so collapsing them into one "Miss!"
    /// would withhold the only feedback that tells them which way to move.
    /// </summary>
    public void DisplayOutcomeCallout(ShockwaveOutcome outcome, float totalScore)
    {
        Debug.Log($"[UIManager] timed outcome={outcome} total={totalScore:0.00}");
        RefreshScoreText();

        bool detected = outcome == ShockwaveOutcome.Detected;
        if (detected) TriggerHitFlash();
        else          TriggerMissFlash();

        string text = outcome switch
        {
            ShockwaveOutcome.Detected => calloutShockwaveHitText,
            ShockwaveOutcome.Early    => calloutShockwaveEarlyText,
            ShockwaveOutcome.Late     => calloutShockwaveLateText,
            _                          => calloutOutOfTimeText,
        };
        ShowCallout(detected, text, detected ? calloutHitColor : calloutShockwaveFailColor);
    }

    /// <summary>
    /// Punches "Hit!" or "Miss!" into the middle of the screen, shakes it, then hides it.
    /// Retriggering restarts the shake rather than stacking coroutines, so rapid fire can't
    /// leave the text stranded off-centre.
    /// </summary>
    public void ShowCallout(bool isHit) =>
        ShowCallout(isHit, isHit ? calloutHitText : calloutMissText,
                     isHit ? calloutHitColor : calloutMissColor);

    /// <summary>As above, with the wording and colour supplied — the shockwave task has three
    /// outcomes and the shared beat animation should not be reimplemented for them.</summary>
    public void ShowCallout(bool isHit, string text, Color color)
    {
        // The miss sting rides with the callout rather than with the rest of the shot audio in
        // GameManager, so the sound and the text can never disagree about what just happened.
        if (!isHit && AudioManager.Instance != null) AudioManager.Instance.PlayMiss();

        if (_callout == null) BuildCallout();
        if (_callout == null) return;

        if (_calloutCoroutine != null) StopCoroutine(_calloutCoroutine);

        _callout.text    = text;
        _callout.color   = color;
        _callout.enabled = true;

        _calloutCoroutine = StartCoroutine(CalloutRoutine(isHit, color));
    }

    /// <summary>
    /// Takes the callout down early. The TOO LATE re-gate puts TRY AGAIN! in the same spot, and
    /// one word replacing another there is the handoff — two words stacked would be a mess.
    /// Safe with no callout up.
    /// </summary>
    public void DismissCallout()
    {
        if (_calloutCoroutine != null)
        {
            StopCoroutine(_calloutCoroutine);
            _calloutCoroutine = null;
        }
        if (_callout == null) return;
        _calloutRect.anchoredPosition = Vector2.zero;
        _callout.transform.localScale = Vector3.one;
        _callout.enabled = false;
    }

    /// <summary>
    /// The cannon's screen flash. Fired by <see cref="ShockwaveCannon"/> at the moment of
    /// detonation, ahead of whichever outcome flash the trial resolves into a beat later — the
    /// blast happens whether or not the timing was right, so the flash cannot be part of the
    /// hit/miss feedback.
    /// </summary>
    public void TriggerShockwaveFlash()
    {
        if (_vignette == null) BuildOverlays();
        if (_vignette == null) return;

        StopFlash();
        _vignette.color = new Color(shockwaveFlashColor.r, shockwaveFlashColor.g,
                                     shockwaveFlashColor.b, shockwaveAlpha);
        _flashCoroutine = StartCoroutine(VignetteRoutine());
    }

    public void RefreshDisplayFromManagers(ScoreManager manager)
    {
        // Re-subscribed, not just reassigned. The strip is driven entirely by OnScored, so a
        // handler left on the previous manager would leave it frozen for the whole session.
        if (manager != null && manager != scoreManager)
        {
            if (scoreManager != null) scoreManager.OnScored -= HandleScored;
            scoreManager = manager;
            scoreManager.OnScored += HandleScored;
        }
        RefreshScoreText();
        RefreshProgress();
    }

    /// <summary>
    /// Re-reads how far the phase is from ending and sets the bar moving toward it. Called once
    /// per response (after the posterior and the round count have moved) and at every phase
    /// start — the read is cheap, but it is the spring in Update that then rebuilds the canvas
    /// each frame, and only until the bar has settled.
    ///
    /// A target of 0 is a fresh phase and resets the never-go-backward peak; anything else can
    /// only raise it. A reset does not animate: the bar empties under the between-phase prompt,
    /// where a fill winding back down would read as something being taken away.
    /// </summary>
    public void RefreshProgress()
    {
        if (progressFill == null) return;

        float target = PhaseProgress();
        if (target <= 0f)
        {
            _progressPeak     = 0f;
            _progressVelocity = 0f;
            _progressSettled  = true;
            SetProgressFill(0f);
            return;
        }

        _progressPeak = Mathf.Max(_progressPeak, target);
        if (!Mathf.Approximately(_progressPeak, _progressShown)) _progressSettled = false;
    }

    // Steps the progress spring. Unscaled time, like every HUD motion here: a stutter is a
    // main-thread block, and a bar that paused with it would carry the stimulus.
    void Update()
    {
        if (_progressSettled || progressFill == null) return;

        float next = progressSmoothSec > 0f
            ? Mathf.SmoothDamp(_progressShown, _progressPeak, ref _progressVelocity,
                               progressSmoothSec, Mathf.Infinity, Time.unscaledDeltaTime)
            : _progressPeak;

        // Close enough to land: snap the last sliver rather than creep at it for frames the eye
        // cannot see, each one a canvas rebuild.
        if (Mathf.Abs(_progressPeak - next) < 0.0005f)
        {
            next              = _progressPeak;
            _progressVelocity = 0f;
            _progressSettled  = true;
        }
        SetProgressFill(next);
    }

    // Practice counts rounds against the ladder; the main run asks the staircase. With no
    // director at all — a hand-played scene — the staircase alone decides.
    float PhaseProgress()
    {
        PerturbationController p = gameManager != null ? gameManager.perturbation : null;
        if (p == null) return 0f;

        if (p.PracticeMode)
            return ExperimentDirector.Instance != null ? ExperimentDirector.Instance.PracticeProgress : 0f;

        return RoundProgress.Of(p.ActiveStaircase as QuestPlusStaircase, p.PosteriorSDMs);
    }

    // The fill is anchor-stretched to its track, so its right anchor IS the fraction — no sprite,
    // no fill-method, and it follows the track through any resize.
    void SetProgressFill(float fraction)
    {
        _progressShown = fraction;
        progressFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;
    }

    // ── Setup ─────────────────────────────────────────────────────────────

    void ResolveReferences()
    {
        if (scoreManager == null) scoreManager = GetComponent<ScoreManager>();
        if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
        if (gameManager  == null) gameManager  = FindAnyObjectByType<GameManager>();
        if (scoreManager == null && gameManager != null) scoreManager = gameManager.scoreManager;
        if (scoreText    == null) scoreText = FindTextByName("ScoreText");
        if (progressFill == null)
        {
            Transform fill = FindChildRecursive(transform, "ProgressFill");
            if (fill != null) progressFill = fill as RectTransform;
        }
    }

    void BuildOverlays()
    {
        if (_canvas == null)
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas == null) _canvas = FindAnyObjectByType<Canvas>();
        }
        if (_canvas == null) return;

        _vignetteTex = BuildVignetteTexture(vignetteResolution, vignetteInnerRadius);
        var sprite   = Sprite.Create(_vignetteTex,
                                     new Rect(0, 0, vignetteResolution, vignetteResolution),
                                     new Vector2(0.5f, 0.5f));

        var go = new GameObject("VignetteFlash");
        go.transform.SetParent(_canvas.transform, false);
        go.transform.SetAsLastSibling();

        _vignette = go.AddComponent<Image>();
        _vignette.sprite         = sprite;
        _vignette.type           = Image.Type.Simple;
        _vignette.preserveAspect = false;
        _vignette.raycastTarget  = false;
        _vignette.color          = new Color(0f, 0f, 0f, 0f);

        var rt = _vignette.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    void BuildCallout()
    {
        if (_canvas == null)
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas == null) _canvas = FindAnyObjectByType<Canvas>();
        }
        if (_canvas == null) return;

        var go = new GameObject("HitMissCallout", typeof(RectTransform));
        go.transform.SetParent(_canvas.transform, false);
        go.transform.SetAsLastSibling();   // above the vignette, which is also a late sibling

        _calloutRect = go.GetComponent<RectTransform>();
        _calloutRect.anchorMin        = new Vector2(0.5f, 0.5f);
        _calloutRect.anchorMax        = new Vector2(0.5f, 0.5f);
        _calloutRect.pivot            = new Vector2(0.5f, 0.5f);
        _calloutRect.sizeDelta        = new Vector2(900f, 220f);
        _calloutRect.anchoredPosition = Vector2.zero;

        var text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize      = calloutFontSize;
        text.fontStyle     = FontStyles.Bold;
        text.alignment     = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        text.enabled       = false;
        // Same typeface as the score readout beside it — see HudFont. Everything on this screen is
        // built from code except the HUD, and without this the callout was the odd one out.
        HudFont.Apply(text);
        _callout = text;
    }

    // Bounces on the same beat as the score text, then holds still for the remainder so the
    // word is readable rather than only glimpsed. A brighter tint is the beat's flash colour,
    // so the pulse is visible on text that is already saturated green or red.
    IEnumerator CalloutRoutine(bool isHit, Color baseColor)
    {
        Color flash = Color.Lerp(baseColor, Color.white, 0.5f);
        float bounce = Mathf.Min(calloutBounceDuration, calloutDuration);

        yield return BeatRoutine(_callout, Vector3.one, baseColor, flash, isHit, bounce);

        float hold = calloutDuration - bounce;
        if (hold > 0f) yield return new WaitForSecondsRealtime(hold);

        _calloutRect.anchoredPosition = Vector2.zero;
        _callout.transform.localScale = Vector3.one;
        _callout.enabled  = false;
        _calloutCoroutine = null;
    }

    // Chebyshev box vignette: transparent inside innerRadius, opaque at edges.
    static Texture2D BuildVignetteTexture(int res, float innerRadius)
    {
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode   = TextureWrapMode.Clamp;
        var pixels = new Color32[res * res];

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dx   = 2f * Mathf.Abs(x / (float)(res - 1) - 0.5f);
                float dy   = 2f * Mathf.Abs(y / (float)(res - 1) - 0.5f);
                float a    = Mathf.SmoothStep(innerRadius, 1.0f, Mathf.Max(dx, dy));
                pixels[y * res + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    // ── Hit / miss tally ──────────────────────────────────────────────────

    void HandleScored(float shotScore, float totalScore, float distance) => RefreshScoreText();

    /// <summary>
    /// Brings the strip up to date with the score manager. Safe to call repeatedly for the same
    /// response — several paths refresh the HUD after one shot, and only a genuine change in the
    /// phase's response count adds a pip.
    /// </summary>
    void RefreshScoreText()
    {
        if (_tallyPips != null)
        {
            int total = scoreManager != null ? scoreManager.Hits + scoreManager.Misses : 0;

            // 0 is the reset between phases, announced by ScoreManager.ResetScore as a scored
            // event of its own. The strip empties with it rather than carrying practice into the
            // main run.
            if (total == 0)
            {
                // Stopped before DrawTally, or a pop still in flight when the phase ended would
                // go on writing a scale onto a pip the redraw had just put back.
                if (_tallyCoroutine != null) { StopCoroutine(_tallyCoroutine); _tallyCoroutine = null; }
                _tallyOutcomes.Clear();
                _tallyCounted = 0;
                DrawTally();
                return;
            }

            if (total <= _tallyCounted) return;
            _tallyCounted = total;

            _tallyOutcomes.Add(scoreManager.LastWasHit);
            while (_tallyOutcomes.Count > tallySlots) _tallyOutcomes.RemoveAt(0);

            DrawTally();
            PopNewestPip();
            return;
        }

        if (scoreText != null)
        {
            float score = scoreManager != null ? scoreManager.TotalScore : 0f;
            scoreText.text = $"Score: {score:0}";
        }
    }

    // Paints the window onto the pips. The window is left-aligned while it is still filling, so
    // the first response of a phase lands in the leftmost slot and the strip grows rightward the
    // way a shootout does; once it is full every pip is occupied and the content scrolls instead.
    void DrawTally()
    {
        if (_tallyPips == null) return;

        for (int i = 0; i < _tallyPips.Length; i++)
        {
            bool filled = i < _tallyOutcomes.Count;
            _tallyRings[i].color = tallyEmptyColor;
            _tallyFills[i].color = filled
                ? (_tallyOutcomes[i] ? tallyHitColor : tallyMissColor)
                : Color.clear;

            // Only the pip that is currently landing is ever off its own size; everything else is
            // put back, so a response arriving mid-animation cannot leave a pip stranded large.
            if (!IsNewestPip(i)) _tallyPips[i].localScale = Vector3.one;
        }
    }

    int NewestPipIndex => Mathf.Min(_tallyOutcomes.Count, tallySlots) - 1;
    bool IsNewestPip(int i) => i == NewestPipIndex;

    void PopNewestPip()
    {
        int i = NewestPipIndex;
        if (i < 0 || _tallyPips == null || i >= _tallyPips.Length) return;

        if (_tallyCoroutine != null) StopCoroutine(_tallyCoroutine);
        _tallyCoroutine = StartCoroutine(PipPopRoutine(_tallyPips[i]));
    }

    // Overshoot and settle, on unscaled time like every other HUD motion here — a stutter is a
    // main-thread block, and anything timed off the scaled clock would carry it.
    IEnumerator PipPopRoutine(RectTransform pip)
    {
        float elapsed = 0f;
        while (elapsed < tallyPopSec)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / tallyPopSec);

            // Up fast, back slowly: a half-sine peaks early and decays, which reads as a landing
            // rather than as a throb.
            float overshoot = Mathf.Sin(Mathf.Pow(t, 0.45f) * Mathf.PI);
            pip.localScale = Vector3.one * (1f + (tallyPopScale - 1f) * overshoot);
            yield return null;
        }

        pip.localScale = Vector3.one;
        _tallyCoroutine = null;
    }

    /// <summary>
    /// Builds the strip over the score readout's slot and switches the readout itself off.
    ///
    /// The scoreText component is disabled rather than its GameObject: WeaponIndicator measures
    /// that RectTransform to line the weapon chip up with the HUD row, and a deactivated object
    /// is not laid out.
    /// </summary>
    void BuildTally()
    {
        if (scoreText == null || _tallyPips != null) return;

        RectTransform anchor = scoreText.rectTransform;
        var parent = anchor.parent as RectTransform;
        if (parent == null) return;

        var rowGo = new GameObject("HitMissTally", typeof(RectTransform));
        rowGo.transform.SetParent(parent, false);
        rowGo.transform.SetSiblingIndex(anchor.GetSiblingIndex());

        // Copied from the slot the score occupied rather than re-specified, so the strip lands
        // exactly where the HUD row already was however the scene has it anchored.
        var row = rowGo.GetComponent<RectTransform>();
        row.anchorMin        = anchor.anchorMin;
        row.anchorMax        = anchor.anchorMax;
        row.pivot            = anchor.pivot;
        row.anchoredPosition = anchor.anchoredPosition;
        row.sizeDelta        = anchor.sizeDelta;

        var group = rowGo.AddComponent<HorizontalLayoutGroup>();
        group.spacing                = tallyPipSpacing;
        group.childAlignment         = TextAnchor.MiddleLeft;
        group.childForceExpandWidth  = false;
        group.childForceExpandHeight = false;

        var fitter = rowGo.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        _ringTex = BuildRingTexture(96, thicknessFrac: 0.16f);
        _discTex = BuildDiscTexture(96);
        Sprite ring = ToSprite(_ringTex);
        Sprite disc = ToSprite(_discTex);

        _tallyPips  = new RectTransform[tallySlots];
        _tallyRings = new Image[tallySlots];
        _tallyFills = new Image[tallySlots];

        for (int i = 0; i < tallySlots; i++)
        {
            var pipGo = new GameObject($"Pip_{i}", typeof(RectTransform));
            pipGo.transform.SetParent(rowGo.transform, false);

            var pip = pipGo.GetComponent<RectTransform>();
            pip.sizeDelta = new Vector2(tallyPipSize, tallyPipSize);

            // The layout group sizes children from what they report, and a bare RectTransform
            // reports nothing — without this the pips collapse to zero width.
            var le = pipGo.AddComponent<LayoutElement>();
            le.preferredWidth  = tallyPipSize;
            le.preferredHeight = tallyPipSize;

            _tallyRings[i] = AddPipImage(pipGo.transform, "Ring", ring, tallyPipSize);
            _tallyFills[i] = AddPipImage(pipGo.transform, "Fill", disc,
                                          tallyPipSize * tallyFillScale);
            _tallyPips[i]  = pip;
        }

        scoreText.enabled = false;
        DrawTally();
    }

    static Image AddPipImage(Transform parent, string name, Sprite sprite, float size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = Vector2.zero;

        var img = go.AddComponent<Image>();
        img.sprite        = sprite;
        img.type          = Image.Type.Simple;
        img.raycastTarget = false;
        return img;
    }

    static Sprite ToSprite(Texture2D tex) =>
        Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

    /// <summary>A filled circle with a soft edge, so a pip is round rather than a stair-stepped
    /// blob at the size it is drawn.</summary>
    static Texture2D BuildDiscTexture(int res)
    {
        var tex = NewPipTexture(res);
        var px  = new Color32[res * res];

        const float radius = 0.47f;
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float nx = x / (float)(res - 1) - 0.5f;
            float ny = y / (float)(res - 1) - 0.5f;
            float r  = Mathf.Sqrt(nx * nx + ny * ny);
            float a  = 1f - Mathf.SmoothStep(radius - 0.02f, radius, r);
            px[y * res + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }

        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    /// <summary>The empty slot: a ring, not a disc, so an untaken pip reads as waiting rather
    /// than as a dim outcome of its own.</summary>
    static Texture2D BuildRingTexture(int res, float thicknessFrac)
    {
        var tex = NewPipTexture(res);
        var px  = new Color32[res * res];

        const float radius = 0.42f;
        float half = Mathf.Max(0.01f, thicknessFrac) * 0.5f;

        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float nx = x / (float)(res - 1) - 0.5f;
            float ny = y / (float)(res - 1) - 0.5f;
            float r  = Mathf.Sqrt(nx * nx + ny * ny);
            float a  = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Abs(r - radius) / half);
            px[y * res + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }

        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static Texture2D NewPipTexture(int res) =>
        new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };

    // ── Progress bar placement ────────────────────────────────────────────

    /// <summary>
    /// Moves the scene's progress bar to a long strip along the bottom of the screen, restyles it
    /// for contrast, and writes its name above it.
    ///
    /// Bottom rather than the top-right corner it sat in: the top of the screen is where the
    /// weapon chip and the tally are, and where the play area starts. The bar has no bearing on
    /// what the participant is doing from moment to moment, so it belongs out of the way — and
    /// once it is out of the way it can be long, which is what makes one round's worth of
    /// progress a visible amount of movement rather than two pixels.
    /// </summary>
    void ApplyProgressLayout()
    {
        if (!layOutProgressBar || progressFill == null) return;

        var root = progressFill.parent as RectTransform;
        if (root == null) return;

        float half = Mathf.Clamp01(progressWidthFraction) * 0.5f;
        root.anchorMin        = new Vector2(0.5f - half, 0f);
        root.anchorMax        = new Vector2(0.5f + half, 0f);
        root.pivot            = new Vector2(0.5f, 0f);
        root.anchoredPosition = new Vector2(0f, progressBottomMargin);
        // X is 0 because the anchors already span the width; only the height is literal.
        root.sizeDelta        = new Vector2(0f, progressHeight);

        var fillImage = progressFill.GetComponent<Image>();
        if (fillImage != null) fillImage.color = progressFillColor;

        foreach (Image img in root.GetComponentsInChildren<Image>(includeInactive: true))
            if (img != fillImage) img.color = progressTrackColor;

        if (_progressLabel == null && !string.IsNullOrEmpty(progressLabelText))
        {
            var go = new GameObject("ProgressLabel", typeof(RectTransform));
            go.transform.SetParent(root, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0.5f, 0f);
            rt.offsetMin        = new Vector2(0f, progressLabelGap);
            rt.offsetMax        = new Vector2(0f, progressLabelGap + progressLabelSize * 1.4f);

            _progressLabel = go.AddComponent<TextMeshProUGUI>();
            _progressLabel.fontSize         = progressLabelSize;
            _progressLabel.fontStyle        = FontStyles.Bold;
            _progressLabel.alignment        = TextAlignmentOptions.Center;
            _progressLabel.raycastTarget    = false;
            _progressLabel.textWrappingMode = TextWrappingModes.NoWrap;
            _progressLabel.color            = progressLabelColor;
            _progressLabel.text             = progressLabelText;
            HudFont.Apply(_progressLabel);
        }
    }

    // ── Flash ─────────────────────────────────────────────────────────────

    void TriggerHitFlash()
    {
        if (_vignette == null) return;
        StopFlash();
        _vignette.color = new Color(hitFlashColor.r, hitFlashColor.g, hitFlashColor.b, hitAlpha);
        _flashCoroutine = StartCoroutine(VignetteRoutine());
        PulseScore(new Color(0.2f, 1f, 0.3f, 1f), positive: true);
    }

    void TriggerMissFlash()
    {
        if (_vignette == null) return;
        StopFlash();
        _vignette.color = new Color(missRedColor.r, missRedColor.g, missRedColor.b, missRedAlpha);
        _flashCoroutine = StartCoroutine(VignetteRoutine());
        PulseScore(new Color(1f, 0.15f, 0.15f, 1f), positive: false);
    }

    void StopFlash()
    {
        if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }
        if (_vignette != null) _vignette.color = new Color(0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// Beats the score readout. With the shootout strip up there is nothing to beat here — the
    /// pip landing is the feedback, and it is already in flight by now: ScoreManager announces the
    /// response before GameManager shows it, so the pip was appended and popped on this same frame.
    /// </summary>
    void PulseScore(Color flashColor, bool positive)
    {
        if (_tallyPips != null || scoreText == null) return;
        if (_scoreCoroutine != null) StopCoroutine(_scoreCoroutine);
        _scoreCoroutine = StartCoroutine(ScorePulseRoutine(flashColor, positive));
    }

    IEnumerator ScorePulseRoutine(Color flashColor, bool positive)
    {
        yield return BeatRoutine(scoreText, _scoreBaseScale, _scoreBaseColor,
                                  flashColor, positive, 0.40f);
        _scoreCoroutine = null;
    }

    /// <summary>
    /// The shared hit/miss beat: hit swells three times, miss dips twice, each beat smaller than
    /// the last so it settles rather than stopping dead. Both durations are the same, so a hit
    /// reads as faster and busier than a miss even before you register the colour.
    ///
    /// Written against a passed-in target rather than the score text specifically, so the
    /// "Hit!"/"Miss!" callout animates identically instead of reimplementing the timing.
    /// </summary>
    IEnumerator BeatRoutine(TMP_Text target, Vector3 baseScale, Color baseColor,
                             Color flashColor, bool positive, float totalDur)
    {
        if (target == null) yield break;

        int   beats   = positive ? 3 : 2;
        float beatDur = totalDur / beats;
        float upFrac  = 0.38f;   // fraction of each beat spent reaching the peak

        for (int i = 0; i < beats; i++)
        {
            float fraction = 1f - (float)i / beats;
            float thisPeak = positive
                ? Mathf.Lerp(1f, scoreBeatPeak,      fraction)
                : Mathf.Lerp(1f, 1f / scoreBeatPeak, fraction);

            Color upFrom = i == 0         ? baseColor : flashColor;
            Color dnTo   = i == beats - 1 ? baseColor : flashColor;

            yield return BeatPhase(target, baseScale, 1f, thisPeak,
                                    beatDur * upFrac, upFrom, flashColor);
            yield return BeatPhase(target, baseScale, thisPeak, 1f,
                                    beatDur * (1f - upFrac), flashColor, dnTo);
        }

        target.transform.localScale = baseScale;
        target.color = baseColor;
    }

    IEnumerator BeatPhase(TMP_Text target, Vector3 baseScale,
                           float scaleFrom, float scaleTo,
                           float duration, Color colorFrom, Color colorTo)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            target.transform.localScale = baseScale * Mathf.Lerp(scaleFrom, scaleTo, t);
            target.color = Color.Lerp(colorFrom, colorTo, t);
            yield return null;
        }
    }

    IEnumerator VignetteRoutine()
    {
        Color peak = _vignette.color;

        yield return new WaitForSecondsRealtime(flashHold);

        float elapsed = 0f;
        while (elapsed < flashFade)
        {
            elapsed += Time.unscaledDeltaTime;
            float a = 1f - Mathf.Clamp01(elapsed / flashFade);
            _vignette.color = new Color(peak.r, peak.g, peak.b, peak.a * a);
            yield return null;
        }

        _vignette.color = new Color(0f, 0f, 0f, 0f);
        _flashCoroutine = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    static Transform FindChildRecursive(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            var found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    TMP_Text FindTextByName(string childName)
    {
        Transform child = FindChildRecursive(transform, childName);
        return child != null ? child.GetComponent<TMP_Text>() : null;
    }
}
