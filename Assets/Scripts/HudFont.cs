using TMPro;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The typeface the scene's HUD is authored in, so everything built from code matches it
    /// instead of falling back to TextMeshPro's default.
    ///
    /// There is exactly one place to set it: <see cref="UIManager.hudFont"/>. Assign a font asset
    /// there and every caption in the game follows, hand-placed and runtime-built alike. Leave it
    /// empty and the score readout's own font stands in, so a scene that predates the field keeps
    /// looking the way it did.
    ///
    /// A single field rather than one per overlay because the overlays are built from code and the
    /// HUD is not — a serialized font on each would be four things to keep in sync and four ways
    /// for the screen to end up in two typefaces.
    ///
    /// A null result is not an error — it just leaves TMP's default in place, which is what a
    /// scene with neither a font nor a score text should get.
    /// </summary>
    public static class HudFont
    {
        static TMP_FontAsset _font;
        static bool          _resolved;

        public static TMP_FontAsset Font
        {
            get
            {
                if (_resolved) return _font;
                _resolved = true;

                // Both fields are serialized on a scene object, so they are populated before any
                // Awake runs — no ordering problem, unlike asking UIManager to have resolved them.
                var ui = Object.FindAnyObjectByType<UIManager>();
                if (ui != null)
                {
                    _font = ui.hudFont;
                    if (_font == null && ui.scoreText != null) _font = ui.scoreText.font;
                }

                if (_font == null)
                    Debug.Log("[HudFont] No score text to take a font from — runtime UI will use " +
                              "the TextMeshPro default.");

                return _font;
            }
        }

        public static void Apply(TMP_Text text)
        {
            if (text == null) return;
            TMP_FontAsset f = Font;
            if (f != null) text.font = f;
        }

        // Statics survive between Play sessions when "Enter Play Mode without domain reload" is on,
        // which would otherwise carry a font asset from a previous run — or a cached null from a
        // run that started before the scene was set up — into the next one.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _font    = null;
            _resolved = false;
        }
    }
}
