// PreTilt.cs — per-channel tilt EQ inserted before the wavefolder.
//
// A "tilt EQ" is a single-control shelving filter that simultaneously
// boosts one end of the spectrum and cuts the other, pivoting around a
// fixed corner frequency. It's the analog-console answer to "make it
// brighter / make it darker" without dialing two separate shelves.
//
// We use the simplest stable construction: one first-order low-pass at
// a fixed corner, with the complementary high-pass derived by subtraction
// (HP = input − LP). Then the two bands are scaled by independent gains
// and summed. By design lowGain * 1 + highGain * 1 == 2 only when both
// gains are 1.0; for the symmetric tilt scheme used by PedalFolder
// (lowGain = 10^(−tilt·k), highGain = 10^(+tilt·k)) the gains are
// reciprocals and the geometric mean stays at 1.0, so unity tilt is
// genuinely flat.
//
// Corner frequency is supplied per-call as an exponential one-pole
// coefficient (the caller computes it once per Work from the current
// sample rate, per Core §29). lowGain and highGain are also supplied
// per-sample so the caller can lerp them through the buffer for
// zipper-free PreTilt modulation (Core §32.7).
//
// Per Build §6.3, this file has no SDK usings — pure DSP, never touches
// IBuzzMachine or host types.

using System;

namespace PedalFolder
{
    public sealed class PreTilt
    {
        // Single one-pole state. The HP band is recovered each sample as
        // (input − _lpState), so we don't need a second state variable.
        private float _lpState;

        // Denormal guard. The LP state decays with the input; if the input
        // goes silent the state can creep into denormal range and the FPU
        // stalls. Same threshold as Wavefolder, per Core §30.
        private const float DENORMAL_THRESHOLD = 1e-20f;

        public void Reset()
        {
            _lpState = 0f;
        }

        /// <summary>
        /// Process one sample through the tilt EQ.
        /// </summary>
        /// <param name="x">Input sample (normalised).</param>
        /// <param name="lpCoef">
        /// One-pole low-pass coefficient = 1 − exp(−2π·fc/sr). Caller
        /// computes once per Work from the host sample rate.
        /// </param>
        /// <param name="lowGain">Linear gain applied to the low-band (LP) signal.</param>
        /// <param name="highGain">Linear gain applied to the high-band (HP = x − LP) signal.</param>
        /// <returns>Tilt-EQ'd sample.</returns>
        public float Process(float x, float lpCoef, float lowGain, float highGain)
        {
            // Standard one-pole LP: y[n] = y[n-1] + α · (x[n] − y[n-1])
            _lpState += lpCoef * (x - _lpState);

            // Flush near-denormal LP state. The HP band is regenerated from
            // input each sample so it can't get stuck in denormal land.
            if (MathF.Abs(_lpState) < DENORMAL_THRESHOLD) _lpState = 0f;

            float lp = _lpState;
            float hp = x - lp;
            return lp * lowGain + hp * highGain;
        }
    }
}
