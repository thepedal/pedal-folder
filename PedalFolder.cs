// PedalFolder.cs — main machine file.
//
// Pedal Folder is a stereo wavefolding distortion effect for ReBuzz.
// Algorithm follows Jatin Chowdhury's "Complex Nonlinearities — Wavefolder":
// https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html
//
// The DSP lives in Wavefolder.cs. This file handles:
//   • ReBuzz IBuzzMachine plumbing (Build §6.2 — both SDK usings)
//   • Parameter declarations (PedalComp §2 — non-negative MinValue)
//   • The bool Work(output, input, n, mode) effect signature
//     (PedalComp §1 — ±32768 ↔ ±1.0 normalisation at the edges)
//   • Per-Work parameter smoothing (Core §32 — two-stage exponential +
//     per-sample lerp for zipper-free modulation by external LFOs)

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace PedalFolder
{
    [MachineDecl(
        Name        = "Pedal Folder",
        ShortName   = "PFold",
        Author      = "Pedal series",
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalFolderMachine : IBuzzMachine
    {
        // ── Constants ────────────────────────────────────────────────────────
        //
        // PedalComp §1: ReBuzz delivers samples at ±32768. Normalise to ±1.0
        // at the input, de-normalise at the output.
        const float SCALE     = 1f / 32768f;
        const float INV_SCALE = 32768f;

        // Core §32: smoothing time constant. 10 ms is the general default
        // documented for modulation-friendly continuous parameters. Drive
        // and Fold change the harmonic content audibly, so any value much
        // shorter than this risks zipper at fast modulation rates; values
        // much longer feel sluggish under finger control.
        const float SMOOTH_SECS = 0.010f;

        // ── Host ─────────────────────────────────────────────────────────────
        readonly IBuzzMachineHost host;

        public PedalFolderMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Parameters ───────────────────────────────────────────────────────
        //
        // All ranges 0..127 (Byte parameter, NoValue = 255, well clear of
        // the Core §9 ceiling). All MinValues ≥ 0 per PedalComp §2.
        //
        // Order is the canonical declaration order — DO NOT REORDER, as
        // preset bundles (if added later) key off declaration index per
        // Build §3.3.

        [ParameterDecl(
            Name        = "Drive",
            Description = "Pre-fold input gain — drives the input harder into the folder",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 32)]
        public int Drive { get; set; } = 32;

        [ParameterDecl(
            Name        = "Fold",
            Description = "Wavefolder injection amount (G) — how much sin-fold is mixed in",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 64)]
        public int Fold { get; set; } = 64;

        [ParameterDecl(
            Name        = "Feedback",
            Description = "Feedback coefficient — adds resonant character; 0 = no feedback",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 0)]
        public int Feedback { get; set; } = 0;

        [ParameterDecl(
            Name        = "Output",
            Description = "Post-fold output trim (−24 dB … +12 dB; 64 = unity)",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 64)]
        public int Output { get; set; } = 64;

        [ParameterDecl(
            Name        = "Mix",
            Description = "Dry/Wet mix (0 = dry, 127 = fully wet)",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 127)]
        public int Mix { get; set; } = 127;

        // ── Parameter mapping helpers ────────────────────────────────────────
        //
        // Each raw 0..127 parameter is mapped to a DSP coefficient. The maps
        // are tuned so the default values (declared above) produce a sensible
        // mild-fold sound out of the box:
        //   • Drive=32  → ~2.0× pre-fold gain (+6 dB)
        //   • Fold=64   → ~0.5  (the page's "G ≈ −0.5" recommendation)
        //   • Feedback=0 → 0    (saturating wavefolder; no feedback path)
        //   • Output=64 → 1.0  (unity)
        //   • Mix=127   → 1.0  (fully wet)

        // Drive: 0..127 → 1.0 .. ~20.0 (linear gain). Exponential so the
        // knob feels even across its range (small movements at the low end
        // are small drive changes; small movements at the high end are
        // small drive changes too, on a perceptual log scale).
        static float MapDrive(int v)
        {
            // v=0  → 1.0 (no drive), v=64 → ~4.5, v=127 → ~20.0
            return MathF.Exp(v * (3.0f / 127f));
        }

        // Fold: 0..127 → 0.0 .. 1.0 (linear).
        // 0.0 = no wavefold contribution (pure tanh sat),
        // 0.5 = the canonical Jatin recommendation,
        // 1.0 = max fold (heavy harmonics).
        static float MapFold(int v) => v / 127f;

        // Feedback: 0..127 → 0.0 .. 0.95. We cap below 1.0 to keep the
        // closed loop stable; values near 1.0 are where the resonance is
        // most pronounced. The page uses 0.9 as its demo value.
        static float MapFeedback(int v) => v * (0.95f / 127f);

        // Output: 0..127, with 64 = unity. Maps to ~−24 dB .. +12 dB.
        // Below 64 attenuates (good for taming the post-fold level boost
        // that heavy fold produces); above 64 makes up if needed.
        static float MapOutput(int v)
        {
            // Convert to dB centred on 64, then to linear gain.
            //   v=0  → −24 dB → 0.0631
            //   v=64 → 0    dB → 1.0
            //   v=127→ +12  dB → 3.98
            // Slope: 24 dB below 64 (0.375 dB per step), 12 dB above 64
            // (0.190 dB per step). We use a piecewise map to make the
            // "attenuate" side wider than the "boost" side — boosting a
            // wavefolder usually causes more harm than good.
            float db = (v < 64) ? (v - 64) * (24f / 64f)     // 0..63 → −24..−0.375
                                : (v - 64) * (12f / 63f);   // 64..127 → 0..+12
            return MathF.Pow(10f, db / 20f);
        }

        // Mix: 0..127 → 0.0 .. 1.0 (linear). 0 = dry, 1 = wet.
        // Linear is fine here — we're crossfading time-correlated signals
        // (the wet is derived from the dry), so an equal-power curve would
        // over-boost the centre. Linear keeps the level honest as you sweep.
        static float MapMix(int v) => v / 127f;

        // ── Smoothing state (Core §32) ───────────────────────────────────────
        //
        // Persistent end-of-Work values. Updated once per Work() toward the
        // current target (exponential), then sub-block-lerped during render.
        float _smDrive, _smFold, _smFeedback, _smOutput, _smMix;
        bool  _smoothInit;

        // ── DSP state ────────────────────────────────────────────────────────
        readonly Wavefolder _wfL = new Wavefolder();
        readonly Wavefolder _wfR = new Wavefolder();

        // ── ReBuzz lifecycle ─────────────────────────────────────────────────

        // ReBuzz reflection-discovers Save/Load to persist state — not used
        // here since all parameters are declared and persisted by the host.

        // ── Work() ───────────────────────────────────────────────────────────
        //
        // Effect machine signature per PedalComp §1 / PedalTracker §12.1:
        //   bool Work(Sample[] output, Sample[] input, int n, WorkModes mode);
        //
        // Returns true when output was written; false when output is silent
        // (e.g. no input). ReBuzz uses this to optimise the downstream chain.
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // No input upstream → no output. The wavefolder has no signal
            // generator of its own (unlike a synth) and the feedback path
            // self-decays to zero, so returning false is correct.
            if (input == null) return false;

            int sr = host?.MasterInfo?.SamplesPerSec ?? 44100;
            if (sr <= 0) sr = 44100;

            // ── Stage 1: per-Work exponential smoothing (Core §32.1) ─────────
            //
            // Read raw target values once at top of Work, then move the
            // persistent smoothed state toward them by an exp-shaped
            // coefficient. The coefficient form `1 - exp(-n/(T·sr))` is
            // mathematically exact regardless of buffer size — total
            // smoothing time stays at SMOOTH_SECS whether n=64 or n=1024.
            float driveTarget    = MapDrive(Drive);
            float foldTarget     = MapFold(Fold);
            float feedbackTarget = MapFeedback(Feedback);
            float outputTarget   = MapOutput(Output);
            float mixTarget      = MapMix(Mix);

            // Core §32.3 lazy init: snap smoothed state to targets on first
            // Work after construction, so we don't audibly ramp from 0 at
            // song load when parameters have already been delivered.
            if (!_smoothInit)
            {
                _smDrive    = driveTarget;
                _smFold     = foldTarget;
                _smFeedback = feedbackTarget;
                _smOutput   = outputTarget;
                _smMix      = mixTarget;
                _smoothInit = true;
            }

            float smoothCoef = 1f - MathF.Exp(-n / (SMOOTH_SECS * sr));

            float drvStart = _smDrive,    drvEnd = drvStart + (driveTarget    - drvStart) * smoothCoef;
            float fldStart = _smFold,     fldEnd = fldStart + (foldTarget     - fldStart) * smoothCoef;
            float fbStart  = _smFeedback, fbEnd  = fbStart  + (feedbackTarget - fbStart)  * smoothCoef;
            float outStart = _smOutput,   outEnd = outStart + (outputTarget   - outStart) * smoothCoef;
            float mixStart = _smMix,      mixEnd = mixStart + (mixTarget      - mixStart) * smoothCoef;

            _smDrive    = drvEnd;
            _smFold     = fldEnd;
            _smFeedback = fbEnd;
            _smOutput   = outEnd;
            _smMix      = mixEnd;

            // ── Stage 2: per-sample linear interpolation through the buffer ──
            //
            // Per Core §32.7 ("Per-sample render (no sub-block)") — the
            // wavefolder has no internal block structure, so we lerp every
            // sample directly. invN normalises sample-index to [0, 1).
            float invN = (n > 1) ? 1f / (n - 1) : 0f;

            // Cache per-sample deltas to avoid one subtraction per sample.
            float drvDelta = drvEnd - drvStart;
            float fldDelta = fldEnd - fldStart;
            float fbDelta  = fbEnd  - fbStart;
            float outDelta = outEnd - outStart;
            float mixDelta = mixEnd - mixStart;

            // ── Per-sample inner loop ────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                float t = i * invN;

                float drv = drvStart + drvDelta * t;
                float fld = fldStart + fldDelta * t;
                float fb  = fbStart  + fbDelta  * t;
                float gOut = outStart + outDelta * t;
                float mix = mixStart + mixDelta * t;

                // Normalise input ±32768 → ±1.0 (PedalComp §1).
                float inL = input[i].L * SCALE;
                float inR = input[i].R * SCALE;

                // Per-channel wavefold.
                float wetL = _wfL.Process(inL, drv, fld, fb);
                float wetR = _wfR.Process(inR, drv, fld, fb);

                // Apply post-fold trim and dry/wet mix.
                float outL = (inL * (1f - mix) + wetL * gOut * mix);
                float outR = (inR * (1f - mix) + wetR * gOut * mix);

                // De-normalise ±1.0 → ±32768. PedalComp §1 idiom: assign a
                // fresh Sample struct rather than mutating fields in place.
                output[i] = new Sample(outL * INV_SCALE, outR * INV_SCALE);
            }

            return true;
        }
    }
}
