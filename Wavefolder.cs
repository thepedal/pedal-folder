// Wavefolder.cs — per-channel stateful feedback wavefolder.
//
// Algorithm (per Jatin Chowdhury, "Complex Nonlinearities — Wavefolder",
// https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html):
//
//     z = tanh(x_drv) + fb * tanh(y_prev)        // feedforward sat + feedback
//     y = z + G * sin(π · x_drv)                 // wavefolder injection
//     y_prev = y                                  // state for next sample
//
// where:
//   x_drv = x · drive          (Drive parameter, ≥ 1.0 = pre-fold gain)
//   G     = fold               (Fold parameter, 0..1 — fold mix amount)
//   fb    = feedback           (Feedback parameter, 0..0.95 — resonance)
//
// Degenerate cases of interest:
//   • Drive=1, Fold=0, Feedback=0      → straight tanh saturation
//   • Drive>1, Fold=0, Feedback=0      → harder tanh distortion
//   • Drive>1, Fold>0, Feedback=0      → "saturating wavefolder" from §Mod1
//   • Drive>1, Fold>0, Feedback>0      → "feedback wavefolder" from §Mod2
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
        /// <returns>Folded sample.</returns>
        public float Process(float x, float drive, float foldAmt, float fbAmt)
        {
            float xDrv = x * drive;

            // Feedforward saturation + tanh'd feedback.
            float z = MathF.Tanh(xDrv) + fbAmt * MathF.Tanh(_yPrev);

            // Wavefolder injection. sin(π·xDrv) is the canonical "fold-back"
            // curve: as xDrv grows past ±1, the sine wraps and the output
            // reverses direction — the audible "fold" of wavefolding.
            //
            // We negate G internally so the user-facing Fold parameter is a
            // positive 0..1 quantity (the source material uses G ∈ [−0.5, −0.1]
            // by convention; flipping the sign here keeps the user-facing
            // parameter monotonic-positive).
            float fold = -foldAmt * MathF.Sin(MathF.PI * xDrv);

            float y = z + fold;

            // Flush to zero if the state is sliding into denormal range.
            // Cheap (one compare + assign in the not-taken branch) and saves
            // 30-100x slowdowns during silent passages with fb > 0.
            if (MathF.Abs(y) < DENORMAL_THRESHOLD) y = 0f;

            _yPrev = y;
            return y;
        }
    }
}
