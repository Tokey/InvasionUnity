using UnityEngine;

namespace JndSort
{
    /// <summary>
    /// Minimal IMGUI overlay so the skeleton needs no Canvas wiring.
    /// Shows instructions, trial counter, the confirm prompt, feedback flashes,
    /// and the final JND screen. Swap for uGUI/TMP later if you want polish.
    /// </summary>
    public class SortHud : MonoBehaviour
    {
        string _instructions = "";
        string _status = "";
        string _prompt = "";
        string _flash = "";
        Color _flashColor = Color.white;
        float _flashUntil;
        bool _done;
        string _doneText = "";

        GUIStyle _big, _mid, _small;

        public void ShowInstructions(string text) => _instructions = text;

        public void SetStatus(int trial, int reversals, int reversalsToStop) =>
            _status = $"Trial {trial}    Reversals {reversals}/{reversalsToStop}";

        public void SetPrompt(string text) => _prompt = text;

        public void Flash(bool correct)
        {
            _flash = correct ? "Correct" : "Wrong";
            _flashColor = correct ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.45f, 0.4f);
            _flashUntil = Time.unscaledTime + 0.7f;
        }

        public void ShowDone(float jnd, int trials, string unit = "ms")
        {
            _done = true;
            _doneText = $"Finished!\nJND \u2248 {jnd:F2} {unit} ({trials} trials)\nCSV saved to persistentDataPath (see Console).";
        }

        void EnsureStyles()
        {
            if (_big != null) return;
            _big = new GUIStyle(GUI.skin.label) { fontSize = 30, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _mid = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperLeft, wordWrap = true };
            _big.normal.textColor = _mid.normal.textColor = _small.normal.textColor = Color.white;
        }

        void OnGUI()
        {
            EnsureStyles();
            float w = Screen.width, h = Screen.height;

            if (_done)
            {
                GUI.Label(new Rect(0, h * 0.35f, w, h * 0.3f), _doneText, _big);
                return;
            }

            GUI.Label(new Rect(16, 12, w * 0.6f, 130), _instructions, _small);
            GUI.Label(new Rect(0, 12, w - 16, 30),
                _status, new GUIStyle(_small) { alignment = TextAnchor.UpperRight });

            if (!string.IsNullOrEmpty(_prompt))
                GUI.Label(new Rect(0, h - 70, w, 40), _prompt, _mid);

            if (Time.unscaledTime < _flashUntil)
            {
                var s = new GUIStyle(_big);
                s.normal.textColor = _flashColor;
                GUI.Label(new Rect(0, h * 0.4f, w, 60), _flash, s);
            }
        }
    }
}
