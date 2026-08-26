using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Sound for the JND UFO task. Drop a clip in each slot; the gameplay scripts call the
    /// matching Play method when that event happens.
    ///
    /// Two AudioSources: one for every one-shot, and one for the looping UFO engine (looping is
    /// a per-source setting, so it needs its own). One-shots go through PlayOneShot, which mixes
    /// clips on top of each other rather than replacing them, so the laser, the explosion and
    /// the hit sting all sound together.
    ///
    /// **Deliberately absent: any cue for the tower crossing or the frame-time stutter.** The
    /// crossing fires the stutter and the stutter is the stimulus being measured — a sound on
    /// either would let a participant detect by ear what the staircase is measuring visually.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [Header("Master")]
        [Tooltip("Scales every sound. Each clip below has its own level on top of this.")]
        [Range(0f, 1f)] public float masterVolume = 1f;

        [Header("Shooting")]
        public AudioClip laserFire;
        [Range(0f, 1f)] public float laserFireVolume = 1f;

        [Tooltip("The blast where the shot lands. Plays on every shot, hit or miss.")]
        public AudioClip explosion;
        [Range(0f, 1f)] public float explosionVolume = 1f;

        [Tooltip("Shot landed inside the tower's hit radius.")]
        public AudioClip hit;
        [Range(0f, 1f)] public float hitVolume = 1f;

        [Tooltip("Shot landed outside it. Fired by UIManager alongside the \"Miss!\" callout.")]
        public AudioClip miss;
        [Range(0f, 1f)] public float missVolume = 1f;

        [Header("UFO Engine (loops the whole time)")]
        public AudioClip ufoEngine;
        [Range(0f, 1f)] public float ufoEngineVolume = 0.4f;

        AudioSource _sfx;
        AudioSource _engine;

        void Awake()
        {
            Instance = this;

            // Created here rather than wired in the Inspector, so nothing has to be set up and
            // nothing is allocated later — this project measures frame times, and a mid-round
            // AddComponent would land as a stutter competing with the deliberate one.
            _sfx = gameObject.AddComponent<AudioSource>();
            _sfx.playOnAwake  = false;
            _sfx.spatialBlend = 0f;

            // Its own source because looping is a per-source setting, not a per-clip one.
            _engine = gameObject.AddComponent<AudioSource>();
            _engine.playOnAwake  = false;
            _engine.spatialBlend = 0f;
            _engine.loop         = true;
        }

        void Start()
        {
            // Empty slots are legal, but an unassigned one is the usual reason a sound "isn't
            // playing", so name them once at startup rather than leaving it to guesswork.
            WarnIfMissing(laserFire, nameof(laserFire));
            WarnIfMissing(explosion, nameof(explosion));
            WarnIfMissing(hit,       nameof(hit));
            WarnIfMissing(miss,      nameof(miss));

            if (ufoEngine == null) return;
            _engine.clip   = ufoEngine;
            _engine.volume = ufoEngineVolume * masterVolume;
            _engine.Play();
        }

        // The one-shots read their level fresh on every Play, so they follow a slider drag
        // straight away. The loop's volume is set once when it starts, so it needs re-applying
        // to be tunable in Play mode too.
        void Update()
        {
            if (_engine.isPlaying) _engine.volume = ufoEngineVolume * masterVolume;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        static void WarnIfMissing(AudioClip clip, string fieldName)
        {
            if (clip == null)
                Debug.LogWarning($"[AudioManager] No clip assigned to '{fieldName}' — it will be silent.");
        }

        /// <summary>
        /// Plays a clip once at its own level, scaled by the master. Does nothing if the slot is
        /// empty, so a partly-filled setup is fine.
        /// </summary>
        public void Play(AudioClip clip, float clipVolume = 1f)
        {
            if (clip == null) return;

            float level = Mathf.Clamp01(clipVolume) * masterVolume;
            if (level <= 0f)
            {
                // Silent because a slider is at zero, not because anything failed — say so,
                // since "clip assigned but nothing audible" is otherwise a confusing state.
                Debug.LogWarning($"[AudioManager] '{clip.name}' skipped: volume is 0 " +
                                  $"(clip {clipVolume:0.00} x master {masterVolume:0.00}).");
                return;
            }

            _sfx.PlayOneShot(clip, level);
        }

        public void PlayLaserFire() => Play(laserFire, laserFireVolume);
        public void PlayExplosion() => Play(explosion, explosionVolume);
        public void PlayHit()       => Play(hit,       hitVolume);
        public void PlayMiss()      => Play(miss,      missVolume);

        /// <summary>
        /// The blast, plus the hit sting when the shot landed.
        ///
        /// The miss sting is deliberately not fired here — UIManager plays it alongside the
        /// "Miss!" callout instead, so the sound and the on-screen text are driven by the same
        /// decision rather than two separate ones.
        /// </summary>
        public void PlayShotOutcome(bool isHit)
        {
            PlayExplosion();
            if (isHit) PlayHit();
        }
    }
}
