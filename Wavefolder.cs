// Wavefolder.cs — per-channel stateful feedback wavefolder.
//
// Algorithm (per Jatin Chowdhury, "Complex Nonlinearities — Wavefolder",
// https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html),
// extended with v1.1 bias offset and selectable fold shape:
//
//     x_drv = x · drive + bias                    // pre-fold drive + DC offset
//     z     = tanh(x_drv) + fb · tanh(y_prev)     // feedforward sat + feedback
//     y     = z − G · S(x_drv)                    // wavefolder injection
//     y_prev = y                                   // state for next sample
//
// where:
//   drive : Drive parameter (≥ 1.0 = pre-fold gain)
//   bias  : Bias parameter (~−2.0 .. +2.0, added AFTER drive so it's
//           independent of drive level — cleaner UX than the pre-drive
//           position)
//   G     : Fold parameter (0..1)
//   fb    : Feedback parameter (0..0.95 — resonance)
//   S(·)  : selected fold shape (Sine, Triangle, or Wrap — see below)
//
// Fold shapes (all are 2-periodic in x, so they wrap as drive pushes
// x_drv past ±1, ±2, ±3 etc. — that wrapping IS the wavefolder action):
//
//   Sine (shape=0) — S(x) = sin(π·x)
//     The canonical Jatin curve. Smooth, bell-shaped harmonic envelope,
//     evenly-spaced harmonic ratios. The "musical" default.
//
//   Triangle (shape=1) — piecewise linear triangle, period 2, peaks ±1
//     Sharper attack at the fold corners → richer high harmonics. The
//     linear ramps create odd-harmonic-dominated spectra. Buzzier than
//     sine. The peaks are unsmoothed, so aliasing is meaningfully worse
//     than sine — use oversampling when pushing this one.
//
//   Wrap (shape=2) — sawtooth wrap, S(x) = x − 2·floor((x+1)/2)
//     Hard discontinuities at every ±1 boundary. Industrial/glitchy
//     character — closer to digital integer overflow than acoustic
//     folding. Aliases prolifically without oversampling. 4× OS is
//     strongly recommended.
//
// Note on the third shape: an earlier plan called this slot "Hyperbolic",
// which was a misnomer — sinh/cosh/tanh aren't periodic in x so they
// can't fold (they just saturate). Wrap is the genuinely-distinct third
// option among periodic fold curves.
//
// One Wavefolder instance per audio channel. State is the single y_prev
// float plus a tiny denormal-flush guard. No allocations after construction.
//
// Per Build §6.3, this file has no SDK usings — it's pure DSP and never
// touches IBuzzMachine or any host type. System is needed for MathF.

using System;

namespace PedalFolder
{
    public sealed class Wavefolder
    {
        // Per-channel state. The single sample of memory IS the wavefolder's
        // feedback delay; everything else is recomputed from parameters.
        private float _yPrev;

        // Denormal guard — see Core §30. The y_prev state decays exponentially
        // toward zero when input goes silent (because fb < 1.0). Without a
        // flush-to-zero check the state slides into denormal range and the
        // FPU drops to microcode, spiking CPU during quiet passages.
        private const float DENORMAL_THRESHOLD = 1e-20f;

        // Shape IDs — must match the Shape parameter's ValueDescription order.
        public const int SHAPE_SINE     = 0;
        public const int SHAPE_TRIANGLE = 1;
        public const int SHAPE_WRAP     = 2;

        public void Reset()
        {
            _yPrev = 0f;
        }

        /// <summary>
        /// Process one sample. All gains and coefficients are pre-computed
        /// by the caller; this is the per-sample hot path.
        /// </summary>
        /// <param name="x">Input sample (normalised, typically ±1.0 range).</param>
        /// <param name="drive">Pre-fold input gain (≥ 1.0).</param>
        /// <param name="foldAmt">Wavefolder injection coefficient (G, 0..1).</param>
        /// <param name="fbAmt">Feedback coefficient (0..0.95).</param>
        /// <param name="bias">DC offset added after drive (±~2.0 useful range).</param>
        /// <param name="shape">Fold curve selector (0=Sine, 1=Triangle, 2=Wrap).</param>
        /// <returns>Folded sample.</returns>
        public float Process(float x, float drive, float foldAmt, float fbAmt, float bias, int shape)
        {
            // Bias is added AFTER drive so the user can dial in asymmetry
            // independently of how hard they're pushing the input. (The
            // alternative — bias before drive — couples the two controls,
            // which feels worse under finger control.)
            float xDrv = x * drive + bias;

            // Feedforward saturation + tanh'd feedback.
            float z = MathF.Tanh(xDrv) + fbAmt * MathF.Tanh(_yPrev);

            // Wavefolder injection. We negate the fold amount internally so
            // the user-facing Fold parameter stays a positive 0..1 quantity
            // (Jatin's reference uses G ∈ [−0.5, −0.1] by convention; the
            // sign flip here matches the same sign convention).
            //
            // Switch is inside the inner loop, but Shape is constant across
            // the whole buffer so the branch predictor sees one outcome and
            // mispredicts at most once per buffer. Hoisting the switch above
            // the loop would triple the loop body for ~0% perceptible gain.
            float fold;
            switch (shape)
            {
                case SHAPE_TRIANGLE:
                    fold = -foldAmt * Triangle(xDrv);
                    break;
                case SHAPE_WRAP:
                    fold = -foldAmt * Wrap(xDrv);
                    break;
                default: // SHAPE_SINE
                    fold = -foldAmt * MathF.Sin(MathF.PI * xDrv);
                    break;
            }

            float y = z + fold;

            // Flush to zero if the state is sliding into denormal range.
            // Cheap (one compare + assign in the not-taken branch) and saves
            // 30-100x slowdowns during silent passages with fb > 0.
            if (MathF.Abs(y) < DENORMAL_THRESHOLD) y = 0f;

            _yPrev = y;
            return y;
        }

        // ── Fold-shape helpers ───────────────────────────────────────────────
        //
        // Both shapes are 2-periodic in x and produce a value in [−1, +1]
        // for input in [−∞, +∞]. Matched range with sin(π·x) so the Fold
        // amount control behaves consistently across all three shapes.

        // Triangle wave, period 2, peaks +1 at x=0.5, valley −1 at x=1.5,
        // zero-crossings at integer x. Derived via period-normalisation
        // then a 3-piece linear ramp. No conditionals on the fast path
        // beyond the two comparisons — branch-predictor-friendly.
        private static float Triangle(float x)
        {
            float p = x * 0.5f;
            p -= MathF.Floor(p);             // p ∈ [0, 1)
            if (p < 0.25f) return  4f * p;          // rising 0 → +1
            if (p < 0.75f) return  2f - 4f * p;     // falling +1 → −1
            return                4f * p - 4f;      // rising −1 → 0
        }

        // Sawtooth wrap: maps x to [−1, +1) by subtracting the appropriate
        // multiple of 2. Equivalent to integer-overflow wrap behaviour. The
        // result has hard discontinuities at every odd integer of x, which
        // is what makes the spectrum so bright and aliasing-prone — handle
        // with care (and with oversampling).
        private static float Wrap(float x)
        {
            return x - 2f * MathF.Floor((x + 1f) * 0.5f);
        }
    }
}
