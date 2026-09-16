using System.Collections;
using JndUfo;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UIManager : MonoBehaviour
{
    [Header("References")]
    public ScoreManager scoreManager;
    public GameManager  gameManager;
    public TMP_Text     scoreText;

    [Tooltip("The fill of the HUD's progress bar — the top-right slot the round counter used to " +
             "occupy. Anchored to the left edge of its track; its right anchor is driven from " +
             "0 to 1 as the phase progresses. Practice fills by rounds completed; the main run " +
             "fills by how close QUEST+ is to stopping — see RoundProgress for the formula.")]
    public RectTransform progressFill;

    [Tooltip("ONE font for the whole game. Assign a TMP Font Asset here and everything follows it " +
             "— the score readout below, the hit/miss callout, the weapon chip, the practice " +
             "banner and hints, and the between-round prompts.\n\n" +
             "Leave it empty to keep whatever font scoreText is already set to, which is what " +
             "happens today.")]
    public TMP_FontAsset hudFont;

    [Header("Debug")]
    public TMP_Text debugText;

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

    public void SetDebugText(string text)
    {
        if (debugText != null) debugText.text = text;
    }

    public void RefreshDisplayFromManagers(ScoreManager manager)
    {
        if (manager != null) scoreManager = manager;
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

    // ── Score ─────────────────────────────────────────────────────────────

    void HandleScored(float shotScore, float totalScore, float distance) => RefreshScoreText();

    void RefreshScoreText()
    {
        float total = scoreManager != null ? scoreManager.TotalScore : 0f;
        if (scoreText != null) scoreText.text = $"Score: {total:0}";
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

    void PulseScore(Color flashColor, bool positive)
    {
        if (scoreText == null) return;
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
