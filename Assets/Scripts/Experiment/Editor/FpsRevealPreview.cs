using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// Renders the post-round frame-rate reveal (<see cref="FpsRevealOverlay"/>) to PNGs at a sweep of
    /// tower positions, screen sizes and moments of its animation — so its layout can be checked in
    /// every corner case without playing a single round.
    ///
    /// The backdrop is a stand-in for the game at reveal time, built to the game's own numbers: the
    /// scene camera (0, 1, −100) looking down +Z with a 15° vertical field of view, the night skybox,
    /// black skyline silhouettes on the ground line, the tower's green beam and its two hit-zone
    /// lines at ±5.5 world units. The fog is gone at that moment of the reveal, so there is none.
    ///
    /// The tower positions run past the game's own range on purpose: TowerManager places the tower
    /// in the middle 80% of the view (0.10–0.90) and never outside 0.05–0.95; 0.03 and 0.97 check
    /// that the reveal still degrades gracefully beyond that. The moments are the ones a
    /// participant sees: the reveal is paced for the study's hold (see <see cref="HoldSec"/>), and
    /// the last moment is just before it fades.
    ///
    /// Menu: Tools ▸ JND ▸ Render FPS Reveal Previews (asks to save the open scene first, renders in
    /// a temporary scene, then reopens yours). Writes to &lt;project&gt;/Temp/FpsRevealPreviews.
    ///
    /// Command line (Unity closed):
    ///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod JndUfo.FpsRevealPreview.RenderFromCommandLine
    ///             -previewOut &lt;folder&gt; [-previewQuick]
    /// -previewQuick renders a short sweep (two screen shapes, six positions) for fast iteration.
    /// </summary>
    public static class FpsRevealPreview
    {
        const string HudFontGuid = "78c5dcc89e87b1846b0d3a18e00b4727";   // StarShield SDF
        const string SkyboxGuid  = "a86ce61c39946534bba3a30b47a6c318";   // AllSky Night MoonBurst

        const float CameraZ   = -100f;
        const float GroundY   = -6.75f;
        const float HitRadius = 5.5f;

        // How long the reveal stays up in the study, from its own start to the fade, as GameManager
        // passes it: fogRetreatDuration (0.2 in the scene) + StudyConfig.revealHoldSec (2) −
        // fpsRevealDelay (0.12).
        const float HoldSec = 2.08f;

        struct Case
        {
            public string name;
            public int    baseFps, alteredFps;
            public PerturbationController.Side alteredSide;
            public float  measuredBase, measuredAltered;   // NaN = never visited
        }

        static readonly Case[] Cases =
        {
            new Case { name = "500v85",  baseFps = 500, alteredFps = 85,  alteredSide = PerturbationController.Side.Right, measuredBase = 499.6f, measuredAltered = 85.0f },
            new Case { name = "60v10",   baseFps = 60,  alteredFps = 10,  alteredSide = PerturbationController.Side.Left,  measuredBase = 59.9f,  measuredAltered = 10.0f },
            new Case { name = "144v142", baseFps = 144, alteredFps = 142, alteredSide = PerturbationController.Side.Left,  measuredBase = 143.8f, measuredAltered = float.NaN },
        };

        static readonly float[] TowerPositions = { 0.03f, 0.05f, 0.07f, 0.10f, 0.15f, 0.22f, 0.35f, 0.50f, 0.65f, 0.78f, 0.85f, 0.90f, 0.93f, 0.95f, 0.97f };
        static readonly float[] QuickPositions = { 0.05f, 0.10f, 0.20f, 0.50f, 0.90f, 0.95f };
        static readonly Vector2Int[] Resolutions =
        {
            new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(2560, 1080),
            new Vector2Int(1600, 1200), new Vector2Int(1280, 720),
        };
        static readonly Vector2Int[] QuickResolutions = { new Vector2Int(1920, 1080), new Vector2Int(2560, 1080) };
        // Mid-slide, mid-roll, and settled just before the fade.
        static readonly float[] Times = { 0.15f, 0.45f, 2.05f };

        [MenuItem("Tools/JND/Render FPS Reveal Previews")]
        static void RenderFromMenu()
        {
            if (Application.isPlaying)
            {
                EditorUtility.DisplayDialog("FPS reveal previews", "Stop Play mode first.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string previous = EditorSceneManager.GetActiveScene().path;
            string outDir   = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "FpsRevealPreviews");
            try
            {
                int n = Render(outDir, Resolutions.Take(1), TowerPositions);
                Debug.Log($"[FpsRevealPreview] {n} preview(s) written to {outDir}");
                EditorUtility.RevealInFinder(outDir);
            }
            finally
            {
                if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
            }
        }

        /// <summary>Entry point for -executeMethod. Reads -previewOut &lt;folder&gt;.</summary>
        public static void RenderFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string outDir = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-previewOut") outDir = args[i + 1];
            bool quick = Array.IndexOf(args, "-previewQuick") >= 0;
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "FpsRevealPreviews");

            try
            {
                int n = quick ? Render(outDir, QuickResolutions, QuickPositions)
                              : Render(outDir, Resolutions, TowerPositions);
                Debug.Log($"[FpsRevealPreview] {n} preview(s) written to {outDir}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[FpsRevealPreview] Failed: {e}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Renders the sweep and returns how many images were written. The menu version
        /// renders 1920×1080 only.</summary>
        public static int Render(string outDir, IEnumerable<Vector2Int> sizes, IEnumerable<float> positions)
        {
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(HudFontGuid));
            if (font != null) HudFont.Override(font);
            else Debug.LogWarning("[FpsRevealPreview] HUD font not found — rendering in the TMP default.");

            var sky = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(SkyboxGuid));

            Camera cam = BuildCamera(sky);
            var props  = new List<GameObject>();

            var overlayGo = new GameObject("FpsRevealOverlay (preview)");
            var overlay   = overlayGo.AddComponent<FpsRevealOverlay>();
            overlay.PreviewOnCamera(cam);

            int written = 0;
            foreach (Vector2Int res in sizes)
            {
                var rt = new RenderTexture(res.x, res.y, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.aspect = res.x / (float)res.y;
                overlay.RefreshScaler();

                foreach (float s in positions)
                {
                    Vector3 tower = cam.ViewportToWorldPoint(new Vector3(s, 0.5f, -CameraZ));
                    tower = new Vector3(tower.x, GroundY, 0f);

                    foreach (GameObject p in props) UnityEngine.Object.DestroyImmediate(p);
                    props.Clear();
                    BuildBackdrop(props, tower.x, cam);

                    foreach (Case c in Cases)
                    {
                        // The full time series only for the first case; the corner sweep is the point.
                        float[] times = c.name == Cases[0].name ? Times : new[] { Times[Times.Length - 1] };
                        foreach (float t in times)
                        {
                            overlay.Show(MakeTrial(c), tower, HitRadius, 0f, HoldSec);
                            overlay.PreviewPose(t);
                            string file = string.Format(CultureInfo.InvariantCulture,
                                "{0}x{1}_s{2:0.00}_{3}_t{4:0.00}.png", res.x, res.y, s, c.name, t);
                            Capture(cam, rt, Path.Combine(outDir, file));
                            written++;
                        }
                    }
                }

                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt);
            }
            return written;
        }

        static PerturbationController.TrialRecord MakeTrial(Case c)
        {
            // Measured rates go in as frames over seconds, the way the trial accumulates them.
            float baseSec = 2f, altSec = 2f;
            return new PerturbationController.TrialRecord
            {
                valid         = true,
                diffFps       = c.baseFps - c.alteredFps,
                baseFps       = c.baseFps,
                alteredFps    = c.alteredFps,
                alteredSide   = c.alteredSide,
                baseFrames    = Mathf.RoundToInt(c.measuredBase * baseSec),
                baseSec       = baseSec,
                alteredFrames = float.IsNaN(c.measuredAltered) ? 0 : Mathf.RoundToInt(c.measuredAltered * altSec),
                alteredSec    = float.IsNaN(c.measuredAltered) ? 0f : altSec,
            };
        }

        static Camera BuildCamera(Material sky)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            go.transform.SetPositionAndRotation(new Vector3(0f, 1f, CameraZ), Quaternion.identity);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView   = 15f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane  = 1000f;
            if (sky != null)
            {
                RenderSettings.skybox = sky;
                if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", -20f);
                cam.clearFlags = CameraClearFlags.Skybox;
            }
            else
            {
                cam.clearFlags      = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.1f, 0.2f);
            }
            return cam;
        }

        /// <summary>The game at reveal time, minus everything that moves: skyline silhouettes on the
        /// ground line, the tower, its beam and its hit-zone lines (TowerManager's colours and widths).</summary>
        static void BuildBackdrop(List<GameObject> props, float towerX, Camera cam)
        {
            var silhouette = new Material(Shader.Find("Sprites/Default")) { color = Color.black };

            var rng = new System.Random(11);
            Vector3 left  = cam.ViewportToWorldPoint(new Vector3(0f, 0.5f, -CameraZ));
            Vector3 right = cam.ViewportToWorldPoint(new Vector3(1f, 0.5f, -CameraZ));
            for (float x = left.x - 4f; x < right.x + 4f; )
            {
                float w = 1.5f + (float)rng.NextDouble() * 3f;
                float h = 3f + (float)rng.NextDouble() * 7f;
                props.Add(Quad("Building", new Vector3(x + w * 0.5f, GroundY + h * 0.5f, -2f), new Vector3(w, h, 1f), silhouette));
                x += w * 0.8f;
            }
            props.Add(Quad("Ground", new Vector3(towerX, GroundY - 10f, -2f), new Vector3(400f, 20f, 1f), silhouette));
            props.Add(Quad("Tower", new Vector3(towerX, GroundY + 6f, -1f), new Vector3(0.7f, 12f, 1f), silhouette));

            props.Add(Line("TowerBeam", towerX, 0.12f, new Color(0.2f, 1f, 0.5f, 1f), 1f));
            Color zone = new Color(0.76636857f, 1f, 0f, 1f);
            props.Add(Line("ZoneLeft",  towerX - HitRadius, 0.025f, zone, 0.6f));
            props.Add(Line("ZoneRight", towerX + HitRadius, 0.025f, zone, 0.6f));
        }

        static GameObject Quad(string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position   = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        // TowerManager's beam: full alpha at the base, fading to nothing 30 units up.
        static GameObject Line(string name, float x, float width, Color c, float alpha)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount   = 2;
            lr.useWorldSpace   = true;
            lr.widthMultiplier = width;
            lr.SetPosition(0, new Vector3(x, GroundY, 0f));
            lr.SetPosition(1, new Vector3(x, GroundY + 30f, 0f));
            lr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = new Color(c.r, c.g, c.b, alpha);
            lr.endColor   = new Color(c.r, c.g, c.b, 0f);
            return go;
        }

        static void Capture(Camera cam, RenderTexture rt, string path)
        {
            Canvas.ForceUpdateCanvases();

            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (GraphicsSettings.currentRenderPipeline != null && RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
                cam.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
