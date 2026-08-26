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
    public TMP_Text     roundText;

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

    // ── Lifecycle ─────────────────────────────────────────────────────────

    void Awake()
    {
        ResolveReferences();
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
    }

    void Start() => RefreshScoreText();

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
    /// Punches "Hit!" or "Miss!" into the middle of the screen, shakes it, then hides it.
    /// Retriggering restarts the shake rather than stacking coroutines, so rapid fire can't
    /// leave the text stranded off-centre.
    /// </summary>
    public void ShowCallout(bool isHit)
    {
        // The miss sting rides with the callout rather than with the rest of the shot audio in
        // GameManager, so the sound and the text can never disagree about what just happened.
        if (!isHit && AudioManager.Instance != null) AudioManager.Instance.PlayMiss();

        if (_callout == null) BuildCallout();
        if (_callout == null) return;

        if (_calloutCoroutine != null) StopCoroutine(_calloutCoroutine);

        Color c = isHit ? calloutHitColor : calloutMissColor;
        _callout.text    = isHit ? calloutHitText : calloutMissText;
        _callout.color   = c;
        _callout.enabled = true;

        _calloutCoroutine = StartCoroutine(CalloutRoutine(isHit, c));
    }

    public void SetDebugText(string text)
    {
        if (debugText != null) debugText.text = text;
    }

    public void RefreshDisplayFromManagers(ScoreManager manager)
    {
        if (manager != null) scoreManager = manager;
        RefreshScoreText();
    }

    // ── Setup ─────────────────────────────────────────────────────────────

    void ResolveReferences()
    {
        if (scoreManager == null) scoreManager = GetComponent<ScoreManager>();
        if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
        if (gameManager  == null) gameManager  = FindAnyObjectByType<GameManager>();
        if (scoreManager == null && gameManager != null) scoreManager = gameManager.scoreManager;
        if (scoreText    == null) scoreText = FindTextByName("ScoreText");
        if (roundText    == null) roundText = FindTextByName("RoundText");
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
        int   shots = scoreManager != null ? scoreManager.ShotCount  : 0;

        if (scoreText != null) scoreText.text = $"Score: {total:0}";
        if (roundText != null) roundText.text = $"Round: {shots}";
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
