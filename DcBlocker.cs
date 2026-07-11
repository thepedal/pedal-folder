// DcBlocker.cs — one-pole DC-blocking high-pass (Core §43.2b)
//
// Why this machine needs one:
//
// With Bias at its default (64 → 0.0) the folder is DC-free by construction.
// Every element of the transfer is an ODD function — tanh, and all three fold
// shapes (sin(π·x), Triangle, Wrap) — so a symmetric input maps to a zero-mean
// output. Measured across shape × drive × fold × feedback with bias = 0: DC is
// exactly 0.0000 in every case.
//
// Bias breaks that on purpose. It is added after drive:
//
//     x_drv = x · drive + bias
//
// which is precisely what makes the transfer asymmetric and generates the even
// harmonics the control exists for. The DC offset is the unwanted by-product of
// the same asymmetry. Measured (220 Hz sine in, all three shapes):
//
//     bias = 0.0 (default)  →  DC  0.0000
//     bias = 1.0            →  DC +0.35 .. +0.69
//     bias = 2.0 (max)      →  DC +0.72 .. +1.34
//
// The feedback path makes it worse rather than better: `fb · tanh(y_prev)`
// re-injects the offset, so at fb = 0.9 and max bias the DC reaches ~1.3 — past
// full scale on its own, before the signal is even added.
//
// There is no analytic fix here (unlike a pulse's 2·pw−1). The offset is
// tanh(x·drive + bias) averaged over the *input's own distribution*, so it
// depends on the incoming signal's level and waveform — it is the classic
// level-dependent asymmetric-nonlinearity case (Core §43.1), which is exactly
// what a blocker is for.
//
// Removing DC does NOT undo the Bias control's purpose: the blocker takes out
// only the 0 Hz component and leaves the even harmonics — the audible result of
// the asymmetry — completely intact. Sibling precedent: Pedal Shaper already
// carries an automatic output DC blocker for exactly its Bias/asymmetric modes.
//
// Placement (see PedalFolder.Work): applied to the WET signal after downsampling
// and before the dry/wet mix, so the machine removes the DC *it* generates and
// stays transparent to the user's dry path — an effect shouldn't silently
// high-pass audio that is merely passing through.
//
// y[n] = x[n] − x[n−1] + R·y[n−1];  zero at DC (z=1), pole at z=R.
//
// Per Build §6.3, no SDK usings — pure DSP. System is needed for MathF.

using System;

namespace PedalFolder
{
    public sealed class DcBlocker
    {
        private float _xPrev;
        private float _yPrev;
        private float _r  = 0.9999f;   // sane default until SetSampleRate runs
        private float _sr;

        // 10 Hz: clears the bias offset while leaving the lowest musical
        // fundamentals untouched (a first-order HP at 10 Hz costs ~0.1 dB at
        // 60 Hz).
        public const float CORNER_HZ = 10f;

        private const float DENORMAL_THRESHOLD = 1e-20f;

        /// <summary>
        /// Recompute the pole for the current sample rate (Core §29). No-op when
        /// unchanged, so it's safe to call every Work. State is deliberately not
        /// cleared — the held sample stays valid across a rate change.
        /// </summary>
        public void SetSampleRate(float sr)
        {
            if (sr <= 0f || sr == _sr) return;
            _sr = sr;
            _r  = MathF.Exp(-2f * MathF.PI * CORNER_HZ / sr);
        }

        public void Reset()
        {
            _xPrev = 0f;
            _yPrev = 0f;
        }

        public float Process(float x)
        {
            float y = x - _xPrev + _r * _yPrev;
            _xPrev = x;

            // Denormal guard (Core §30) — same rationale as Wavefolder._yPrev:
            // the pole at R < 1 decays y toward zero through silent passages.
            if (MathF.Abs(y) < DENORMAL_THRESHOLD) y = 0f;

            _yPrev = y;
            return y;
        }
    }
}
