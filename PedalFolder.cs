// PedalFolder.cs — main machine file.
//
// Pedal Folder is a stereo wavefolding distortion effect for ReBuzz.
// Algorithm follows Jatin Chowdhury's "Complex Nonlinearities — Wavefolder":
// https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html
//
// v1.1 adds four parameters on top of the v1.0 five:
//   Bias    — pre-fold DC offset for asymmetric folding
//   PreTilt — tilt EQ at 800 Hz that shapes what hits the folder
//   Shape   — fold curve selector (Sine / Triangle / Wrap)
//   OS      — oversampling factor (1× / 2× / 4×)
//
// The DSP lives in Wavefolder.cs (fold math), PreTilt.cs (tilt EQ), and
// Oversampler.cs (FIR half-band 2×/4×). This file handles:
//   • ReBuzz IBuzzMachine plumbing (Build §6.2 — both SDK usings)
//   • Parameter declarations (PedalComp §2 — non-negative MinValue)
//   • The bool Work(output, input, n, mode) effect signature
//     (PedalComp §1 — ±32768 ↔ ±1.0 normalisation at the edges)
//   • Per-Work parameter smoothing (Core §32 — two-stage exponential +
//     per-sample lerp for zipper-free modulation by external LFOs)
//   • The OS dispatch — picks one of three pre-allocated Oversampler
//     instances per channel based on the OS parameter

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
        // documented for modulation-friendly continuous parameters. Drive,
        // Fold, Bias and PreTilt-gains all use this constant so per-Work
        // ramp times stay consistent across the smoothed parameters.
        const float SMOOTH_SECS = 0.010f;

        // PreTilt pivot frequency. 800 Hz sits in the lower-mid range — low
        // enough to clearly affect bass when boosted, high enough to keep
        // the "treble" band including most of the harmonic excitement the
        // wavefolder generates. Hard-coded; not user-exposed.
        const float PRE_TILT_CORNER_HZ = 800f;

        // ── Host ─────────────────────────────────────────────────────────────
        readonly IBuzzMachineHost host;

        public PedalFolderMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Parameters ───────────────────────────────────────────────────────
        //
        // Continuous parameters: 0..127 range (Byte parameter, NoValue = 255,
        // well clear of the Core §9 ceiling). All MinValues ≥ 0 per
        // PedalComp §2.
        //
        // Declaration order is canonical. Per Build §3.3, never reorder
        // existing parameters. v1.1 appends Bias, PreTilt, Shape, and OS
        // AFTER the v1.0 five — old presets that don't know about these
        // params get the declared DefValue, which preserves v1.0 character.

        [ParameterDecl(
            Name        = "Drive",
            Description = "Pre-fold input gain — drives the input harder into the folder",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 32)]
        public int Drive { get; set; } = 32;

        [ParameterDecl(
            Name        = "Fold",
            Description = "Wavefolder injection amount (G) — how much fold is mixed in",
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

        // ── v1.1 additions ───────────────────────────────────────────────────

        [ParameterDecl(
            Name        = "Bias",
            Description = "Pre-fold DC offset — 64 = no bias, sweeps to ±2.0",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 64)]
        public int Bias { get; set; } = 64;

        [ParameterDecl(
            Name        = "PreTilt",
            Description = "Tilt EQ before the folder — 64 = flat, ±12 dB at the pivot",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 64)]
        public int PreTilt { get; set; } = 64;

        [ParameterDecl(
            Name              = "Shape",
            Description       = "Fold curve — Sine (smooth) / Triangle (sharp) / Wrap (harsh)",
            MinValue          = 0,
            MaxValue          = 2,
            DefValue          = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Wrap" })]
        public int Shape { get; set; } = 0;

        [ParameterDecl(
            Name              = "OS",
            Description       = "Oversampling factor — higher = cleaner but more CPU",
            MinValue          = 0,
            MaxValue          = 2,
            DefValue          = 1,                    // = 2× (good fresh-load sound)
            ValueDescriptions = new[] { "1×", "2×", "4×" })]
        public int OS { get; set; } = 1;

        // ── Parameter mapping helpers ────────────────────────────────────────
        //
        // Each raw 0..127 parameter is mapped to a DSP coefficient. The maps
        // are tuned so the default values (declared above) produce a sensible
        // mild-fold sound out of the box.

        // Drive: 0..127 → 1.0 .. ~20.0 (linear gain), exponential curve.
        // v=0 → 1.0, v=64 → ~4.5, v=127 → ~20.0
        static float MapDrive(int v) => MathF.Exp(v * (3.0f / 127f));

        // Fold: 0..127 → 0.0 .. 1.0 (linear).
        static float MapFold(int v) => v / 127f;

        // Feedback: 0..127 → 0.0 .. 0.95 (capped below 1.0 for stability).
        static float MapFeedback(int v) => v * (0.95f / 127f);

        // Output: piecewise dB centred at 64.
        //   v=0  → −24 dB → 0.0631
        //   v=64 → 0   dB → 1.0
        //   v=127→ +12 dB → 3.98
        static float MapOutput(int v)
        {
            float db = (v < 64) ? (v - 64) * (24f / 64f)
                                : (v - 64) * (12f / 63f);
            return MathF.Pow(10f, db / 20f);
        }

        // Mix: 0..127 → 0.0 .. 1.0 (linear — preserves dry level at centre).
        static float MapMix(int v) => v / 127f;

        // Bias: 0..127 → ~−2.0 .. +2.0, centred at 64.
        // Range chosen wide enough to push x_drv past a full fold of the
        // shape function — bias near ±2 sits at the next fold lobe even
        // with Drive at minimum, which is musically more interesting than
        // a "subtle DC trim" range.
        static float MapBias(int v) => (v - 64) * (1f / 32f);

        // OS parameter index → integer oversampling factor.
        static int MapOsFactor(int v) => v switch
        {
            0 => 1,
            1 => 2,
            _ => 4,
        };

        // ── Smoothing state (Core §32) ───────────────────────────────────────
        //
        // Persistent end-of-Work values. Updated once per Work() toward the
        // current target (exponential), then sub-block-lerped during render.
        //
        // PreTilt is unusual: rather than smoothing the raw [-1, +1] tilt
        // value (which would require per-sample MathF.Pow to derive the
        // band gains), we smooth the final linear gains directly. ±12 dB
        // is a narrow enough dynamic range that linear-space smoothing is
        // perceptually indistinguishable from dB-space smoothing.
        float _smDrive, _smFold, _smFeedback, _smOutput, _smMix;
        float _smBias, _smLowGain, _smHighGain;
        bool  _smoothInit;

        // ── DSP state ────────────────────────────────────────────────────────

        readonly Wavefolder _wfL = new Wavefolder();
        readonly Wavefolder _wfR = new Wavefolder();

        readonly PreTilt _preTiltL = new PreTilt();
        readonly PreTilt _preTiltR = new PreTilt();

        // One Oversampler instance per (channel × factor). Factor 1 is a
        // passthrough but exists as a real instance so the dispatch in
        // Work() can use the same Up()/Down() shape regardless of mode.
        // Allocated upfront — total cost is well under 2 KB and avoids any
        // chance of allocation on the audio thread.
        readonly Oversampler _osL_1x = new Oversampler(1);
        readonly Oversampler _osR_1x = new Oversampler(1);
        readonly Oversampler _osL_2x = new Oversampler(2);
        readonly Oversampler _osR_2x = new Oversampler(2);
        readonly Oversampler _osL_4x = new Oversampler(4);
        readonly Oversampler _osR_4x = new Oversampler(4);

        // ── ReBuzz lifecycle ─────────────────────────────────────────────────

        // ReBuzz reflection-discovers Save/Load to persist state — not used
        // here since all parameters are declared and persisted by the host.

        // ── Work() ───────────────────────────────────────────────────────────
        //
        // Effect machine signature per PedalComp §1 / PedalTracker §12.1:
        //   bool Work(Sample[] output, Sample[] input, int n, WorkModes mode);
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // No input upstream → no output. The wavefolder has no signal
            // generator of its own (unlike a synth) and the feedback path
            // self-decays to zero, so returning false is correct.
            if (input == null) return false;

            int sr = host?.MasterInfo?.SamplesPerSec ?? 44100;
            if (sr <= 0) sr = 44100;

            // ── Discrete (non-smoothed) parameters ───────────────────────────
            //
            // Shape and OS are enum selectors. Per Core §32.4, discrete
            // parameters are not smoothed — switching them produces a
            // discontinuity but that's the expected behaviour for a mode
            // switch. (Smoothing an enum would mean briefly running TWO
            // shapes/factors and crossfading, which is more code than it's
            // worth for a setup-time parameter.)
            int shape    = Shape;
            int osFactor = MapOsFactor(OS);

            // Pick the Oversampler pair for the current factor. By holding
            // separate instances per factor we don't need to reconfigure
            // any FIR delay lines on the audio thread — the inactive ones
            // sit idle with whatever state they last had, which means a
            // brief artifact when the user toggles OS factor (acceptable;
            // OS is a setup parameter, not a modulation target).
            Oversampler osL, osR;
            switch (osFactor)
            {
                case 4:  osL = _osL_4x; osR = _osR_4x; break;
                case 2:  osL = _osL_2x; osR = _osR_2x; break;
                default: osL = _osL_1x; osR = _osR_1x; break;
            }

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
            float biasTarget     = MapBias(Bias);

            // PreTilt: convert raw value to per-band linear gains. Use the
            // RAW (not smoothed) PreTilt value to compute the gain targets,
            // then smooth the gains themselves below — that way the per-Pow
            // calls stay outside the inner loop.
            //
            // tilt ∈ [-1, +1]. ±0.6 = 12 dB / 20 dB → ±12 dB per band, so
            // PreTilt = 127 gives +12 dB highs and −12 dB lows (24 dB total
            // tilt). The gains are reciprocals, so PreTilt = 64 gives 1.0
            // for both bands → perfectly flat.
            float tiltTarget     = (PreTilt - 64) * (1f / 64f);
            float lowGainTarget  = MathF.Pow(10f, -tiltTarget * 0.6f);
            float highGainTarget = MathF.Pow(10f,  tiltTarget * 0.6f);

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
                _smBias     = biasTarget;
                _smLowGain  = lowGainTarget;
                _smHighGain = highGainTarget;
                _smoothInit = true;
            }

            float smoothCoef = 1f - MathF.Exp(-n / (SMOOTH_SECS * sr));

            float drvStart  = _smDrive,    drvEnd  = drvStart  + (driveTarget    - drvStart)  * smoothCoef;
            float fldStart  = _smFold,     fldEnd  = fldStart  + (foldTarget     - fldStart)  * smoothCoef;
            float fbStart   = _smFeedback, fbEnd   = fbStart   + (feedbackTarget - fbStart)   * smoothCoef;
            float outStart  = _smOutput,   outEnd  = outStart  + (outputTarget   - outStart)  * smoothCoef;
            float mixStart  = _smMix,      mixEnd  = mixStart  + (mixTarget      - mixStart)  * smoothCoef;
            float biasStart = _smBias,     biasEnd = biasStart + (biasTarget     - biasStart) * smoothCoef;
            float lgStart   = _smLowGain,  lgEnd   = lgStart   + (lowGainTarget  - lgStart)   * smoothCoef;
            float hgStart   = _smHighGain, hgEnd   = hgStart   + (highGainTarget - hgStart)   * smoothCoef;

            _smDrive    = drvEnd;
            _smFold     = fldEnd;
            _smFeedback = fbEnd;
            _smOutput   = outEnd;
            _smMix      = mixEnd;
            _smBias     = biasEnd;
            _smLowGain  = lgEnd;
            _smHighGain = hgEnd;

            // ── Stage 2: per-sample linear interpolation through the buffer ──
            //
            // Per Core §32.7 ("Per-sample render (no sub-block)"). All
            // smoothed params lerp at SOURCE rate (not OS rate) — they're
            // constant across the OS samples within one source sample,
            // which is plenty smooth since SMOOTH_SECS at any reasonable
            // sample rate is hundreds of source samples.
            float invN = (n > 1) ? 1f / (n - 1) : 0f;

            float drvDelta  = drvEnd  - drvStart;
            float fldDelta  = fldEnd  - fldStart;
            float fbDelta   = fbEnd   - fbStart;
            float outDelta  = outEnd  - outStart;
            float mixDelta  = mixEnd  - mixStart;
            float biasDelta = biasEnd - biasStart;
            float lgDelta   = lgEnd   - lgStart;
            float hgDelta   = hgEnd   - hgStart;

            // PreTilt LP coefficient. Rate-dependent (per Core §29); recomputed
            // every Work in case the host changes sample rate mid-song.
            float lpCoef = 1f - MathF.Exp(-2f * MathF.PI * PRE_TILT_CORNER_HZ / sr);

            // ── Per-sample inner loop ────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                float t = i * invN;

                // Sample-rate lerped params (constant across OS samples).
                float drv  = drvStart  + drvDelta  * t;
                float fld  = fldStart  + fldDelta  * t;
                float fb   = fbStart   + fbDelta   * t;
                float gOut = outStart  + outDelta  * t;
                float mix  = mixStart  + mixDelta  * t;
                float bias = biasStart + biasDelta * t;
                float lg   = lgStart   + lgDelta   * t;
                float hg   = hgStart   + hgDelta   * t;

                // Normalise input ±32768 → ±1.0 (PedalComp §1).
                float inL = input[i].L * SCALE;
                float inR = input[i].R * SCALE;

                // Pre-fold tilt EQ. Runs at source rate (before upsampling)
                // — tilting at OS rate would require pole frequency scaling
                // that buys us nothing, since 800 Hz is far below source
                // Nyquist already.
                float preL = _preTiltL.Process(inL, lpCoef, lg, hg);
                float preR = _preTiltR.Process(inR, lpCoef, lg, hg);

                // Upsample, fold each OS sample, downsample. For OS=1 these
                // are passthrough no-ops on the buffer, so the code path is
                // uniform across factors.
                osL.Up(preL);
                osR.Up(preR);
                for (int k = 0; k < osFactor; k++)
                {
                    osL.Buffer[k] = _wfL.Process(osL.Buffer[k], drv, fld, fb, bias, shape);
                    osR.Buffer[k] = _wfR.Process(osR.Buffer[k], drv, fld, fb, bias, shape);
                }
                float wetL = osL.Down();
                float wetR = osR.Down();

                // Apply post-fold trim and dry/wet mix. Dry path uses the
                // RAW input, not the PreTilt'd one — PreTilt is a fold-
                // sculpting tool, not a tone EQ on the dry signal.
                float outL = inL * (1f - mix) + wetL * gOut * mix;
                float outR = inR * (1f - mix) + wetR * gOut * mix;

                // De-normalise ±1.0 → ±32768. PedalComp §1 idiom: assign a
                // fresh Sample struct rather than mutating fields in place.
                output[i] = new Sample(outL * INV_SCALE, outR * INV_SCALE);
            }

            return true;
        }
    }
}
