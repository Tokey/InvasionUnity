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

        [Header("Shockwave")]
        [Tooltip("PLACEHOLDER SLOT — the shockwave cannon's own detonation, played once at the " +
                 "moment it goes off. Leave it empty until you have a clip: the barrage below " +
                 "carries the blast on its own, and this only layers a signature on top of it.")]
        public AudioClip shockwaveBlast;
        [Range(0f, 1f)] public float shockwaveBlastVolume = 1f;

        [Tooltip("How loud each explosion in the barrage is relative to a single shot's. The cannon " +
                 "sets off a whole row of them at once, so leaving them at full level stacks into " +
                 "clipping rather than into weight.")]
        [Range(0f, 1f)] public float barrageExplosionVolume = 0.55f;

        [Tooltip("How positional the barrage is. 0 plays every blast dead centre like the rest of " +
                 "the UI sounds; 1 fully pans and attenuates each one by where it went off, which " +
                 "is what makes the explosions read as spanning the field rather than as one " +
                 "loud sound. Only affects the barrage — every other cue stays 2D.")]
        [Range(0f, 1f)] public float barrageSpatialBlend = 0.75f;

        [Tooltip("Positional voices reserved for the barrage. The cannon fires its explosions in a " +
                 "chain rather than all at once, so this needs to cover the overlap, not the " +
                 "whole row. Anything beyond it falls back to the ordinary 2D one-shot.")]
        [Min(1)] public int barrageVoices = 12;

        [Header("UFO Engine (loops the whole time)")]
        public AudioClip ufoEngine;
        [Range(0f, 1f)] public float ufoEngineVolume = 0.4f;

        AudioSource   _sfx;
        AudioSource   _engine;
        AudioSource[] _positional;
        int           _nextPositional;

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

            // A pool of 3D voices for the shockwave barrage. Spatial blend is per-SOURCE, not
            // per-clip, so a positional one-shot cannot go through _sfx — and the usual way to get
            // one, PlayClipAtPoint, creates and destroys a GameObject per call. Dozens of those
            // inside one blast is exactly the kind of garbage that lands as a stutter competing
            // with the deliberate one, so the voices are made once here and cycled.
            _positional = new AudioSource[Mathf.Max(1, barrageVoices)];
            for (int i = 0; i < _positional.Length; i++)
            {
                var go = new GameObject($"PositionalSfx_{i}");
                go.transform.SetParent(transform, worldPositionStays: false);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake  = false;
                src.spatialBlend = barrageSpatialBlend;
                // Linear rather than the default logarithmic rolloff: the play field is only tens
                // of units wide, and logarithmic would make the far edge of a blast that is meant
                // to cover everything almost inaudible.
                src.rolloffMode  = AudioRolloffMode.Linear;
                src.minDistance  = 8f;
                src.maxDistance  = 120f;

                // Doppler OFF. It defaults to 1, and the AudioListener lives on Main Camera — the
                // object CameraRig shakes. The shockwave shake displaces that camera by several
                // world units several times a second, which Unity reads as listener VELOCITY and
                // answers with a rapid pitch warble on every 3D voice. Nothing in this game
                // actually moves relative to the listener, so the entire effect was artefact:
                // audible as a crackle over the barrage, and it got worse every time the shake
                // was made stronger.
                src.dopplerLevel = 0f;

                _positional[i]   = src;
            }
        }

        void Start()
        {
            // Empty slots are legal, but an unassigned one is the usual reason a sound "isn't
            // playing", so name them once at startup rather than leaving it to guesswork.
            WarnIfMissing(laserFire, nameof(laserFire));
            WarnIfMissing(explosion, nameof(explosion));
            WarnIfMissing(hit,       nameof(hit));
            WarnIfMissing(miss,      nameof(miss));

            // Not WarnIfMissing: this one is a placeholder that is *expected* to be empty for now,
            // and a warning would train the experimenter to ignore the ones that do matter.
            if (shockwaveBlast == null)
                Debug.Log("[AudioManager] 'shockwaveBlast' is empty (placeholder slot) — the cannon " +
                          "runs on the explosion barrage alone until a clip is assigned.");

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

        /// <summary>
        /// Plays a one-shot at a world position through the positional pool, so it pans and
        /// attenuates by where it happened. Falls back to the ordinary 2D one-shot when every
        /// voice is busy — a dropped explosion inside a barrage of them would be more noticeable
        /// than one that simply isn't panned.
        /// </summary>
        public void PlayAt(AudioClip clip, float clipVolume, Vector3 worldPos)
        {
            if (clip == null || _positional == null || _positional.Length == 0) { Play(clip, clipVolume); return; }

            float level = Mathf.Clamp01(clipVolume) * masterVolume;
            if (level <= 0f) return;

            // Round-robin from the oldest voice, then take the first idle one from there. Starting
            // the search at the cursor means a long barrage steals the voice that has been playing
            // longest rather than always retrying the same busy slot.
            for (int i = 0; i < _positional.Length; i++)
            {
                AudioSource src = _positional[(_nextPositional + i) % _positional.Length];
                if (src == null || src.isPlaying) continue;

                _nextPositional = (_nextPositional + i + 1) % _positional.Length;
                src.transform.position = worldPos;
                src.spatialBlend       = barrageSpatialBlend;
                src.PlayOneShot(clip, level);
                return;
            }

            Play(clip, clipVolume);
        }

        public void PlayLaserFire() => Play(laserFire, laserFireVolume);
        public void PlayExplosion() => Play(explosion, explosionVolume);
        public void PlayHit()       => Play(hit,       hitVolume);
        public void PlayMiss()      => Play(miss,      missVolume);

        /// <summary>
        /// One blast of the shockwave barrage, at its own position on the field. Deliberately the
        /// SAME explosion clip the laser uses: the cannon's scale is meant to come from hearing that
        /// sound go off everywhere at once, not from a different, bigger sample.
        /// </summary>
        public void PlayExplosionAt(Vector3 worldPos) =>
            PlayAt(explosion, barrageExplosionVolume / Mathf.Sqrt(ActivePositionalVoices() + 1), worldPos);

        /// <summary>
        /// How many barrage voices are sounding right now. Used to keep the row of explosions from
        /// summing past full scale.
        ///
        /// The cannon fires 11 detonations across ~0.85 s and the explosion clip is ~1.7 s long, so
        /// every one of them is still sounding when the last one starts. At a flat 0.55 each that
        /// is a peak around six times full scale — which the output stage answers by clipping, and
        /// clipping is heard as a crackle. Dividing by sqrt(n) is the incoherent-summing rule:
        /// n copies of the same sound are sqrt(n) louder, not n louder, so this holds the barrage
        /// at roughly the level of a single blast however many go off.
        /// </summary>
        int ActivePositionalVoices()
        {
            if (_positional == null) return 0;

            int n = 0;
            for (int i = 0; i < _positional.Length; i++)
                if (_positional[i] != null && _positional[i].isPlaying) n++;
            return n;
        }

        /// <summary>The cannon's own signature, layered over the barrage. Silent until a clip is
        /// assigned — see the placeholder note on <see cref="shockwaveBlast"/>.</summary>
        public void PlayShockwaveBlast() => Play(shockwaveBlast, shockwaveBlastVolume);

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
