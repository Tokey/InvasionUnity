using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Obscures the tower with cloud while it's hidden and fades the cloud away when a
    /// shot reveals it. Drive a ParticleSystem (assign a material using one of the
    /// supplied cloud textures) and/or a set of billboard Renderers. Place this object's
    /// transform over the tower; TowerManager calls MoveOver() each time the tower moves.
    /// </summary>
    public class CloudCover : MonoBehaviour
    {
        [Header("Cloud Visuals (assign at least one)")]
        public ParticleSystem cloudParticles;
        public Renderer[] cloudRenderers; // quads/billboards using a cloud texture

        [Header("Fade")]
        public float fadeDuration = 0.4f;
        [Range(0f, 1f)] public float obscuredAlpha = 1f;
        [Range(0f, 1f)] public float revealedAlpha = 0f;

        Coroutine _fade;
        float _alpha = 1f;

        void Start() => ApplyAlpha(obscuredAlpha);

        public void MoveOver(Vector3 worldPos) => transform.position = worldPos;

        public void Reveal() => FadeTo(revealedAlpha);
        public void Obscure() => FadeTo(obscuredAlpha);

        void FadeTo(float target)
        {
            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeRoutine(target));
        }

        IEnumerator FadeRoutine(float target)
        {
            float start = _alpha;
            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.unscaledDeltaTime;
                ApplyAlpha(Mathf.Lerp(start, target, t / fadeDuration));
                yield return null;
            }
            ApplyAlpha(target);
        }

        void ApplyAlpha(float a)
        {
            _alpha = a;

            if (cloudParticles != null)
            {
                var main = cloudParticles.main;
                Color c = main.startColor.color;
                c.a = a;
                main.startColor = c;

                if (a <= 0.001f && cloudParticles.isPlaying)
                    cloudParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                else if (a > 0.001f && !cloudParticles.isPlaying)
                    cloudParticles.Play();
            }

            if (cloudRenderers != null)
            {
                foreach (var r in cloudRenderers)
                {
                    if (r == null) continue;
                    Material mat = r.material;
                    if (mat.HasProperty("_Color"))
                    {
                        Color c = mat.color; c.a = a; mat.color = c;
                    }
                    else if (mat.HasProperty("_BaseColor"))
                    {
                        Color c = mat.GetColor("_BaseColor"); c.a = a; mat.SetColor("_BaseColor", c);
                    }
                }
            }
        }
    }
}
