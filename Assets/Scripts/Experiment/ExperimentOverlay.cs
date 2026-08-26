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
        GameObject      _root;
        TextMeshProUGUI _title;
        TextMeshProUGUI _body;

        // Separate from _root: the banner stays up *during* play, so it must not carry the
        // full-screen dim that the message panel uses.
        GameObject      _bannerRoot;
        TextMeshProUGUI _banner;

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

            _body = MakeText(_root.transform, "Body", "", 40f, FontStyles.Normal,
                             new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                             new Vector2(80f, -80f), new Vector2(-80f, 10f),
                             new Color(0.72f, 0.76f, 0.84f));

            _root.SetActive(false);

            // ── Top banner (no dim; shown during play) ───────────────────────
            _bannerRoot = NewRect("Banner", canvasGo.transform,
                                   new Vector2(0f, 1f), new Vector2(1f, 1f),
                                   new Vector2(0f, -96f), new Vector2(0f, -16f));
            // Amber: the banner only ever marks practice, so it reads as "not the real thing".
            var strip = _bannerRoot.AddComponent<Image>();
            strip.color         = new Color(0.60f, 0.35f, 0.05f, 0.55f);
            strip.raycastTarget = false;

            _banner = MakeText(_bannerRoot.transform, "BannerText", "", 40f, FontStyles.Bold,
                               Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                               new Color(0.97f, 0.96f, 0.94f));

            _bannerRoot.SetActive(false);
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
            return t;
        }
    }
}
