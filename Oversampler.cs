// Oversampler.cs — 2× and 4× polyphase-bypassing half-band FIR oversamplers.
//
// Why oversampling: the wavefolder's sin(π·xDrv) injection (and the
// Triangle/Wrap alternates) generates harmonic content well above the
// source-rate Nyquist when xDrv gets large. Without anti-alias filtering
// those high harmonics reflect back into the audible band as inharmonic
// "fizz" — the characteristic ugly side of a wavefolder pushed hard.
//
// The approach: run the fold at 2× or 4× the host sample rate, so the
// nonlinearity has more headroom before aliasing. The hot path:
//
//   source sample  →  Up()  →  Buffer[0..F−1] (at OS rate)
//                                ↓
//                              caller folds each Buffer[k] in place
//                                ↓
//                   Down()  →  one source-rate output sample
//
// Filter design: 31-tap symmetric half-band FIR, coefficients computed
// at type-init by windowed sinc (Blackman window). Half-band property
// means every even-offset tap is zero except the centre (which is 0.5)
// — so DC gain is 1.0 and stopband attenuation is ~70 dB with this
// window choice. We don't exploit the polyphase zeros structurally
// (the inner loop just multiplies through; the dozen-odd zero taps
// are cheap), which keeps the code readable at the cost of a few
// percent CPU vs. a strict polyphase implementation.
//
// 4× oversampling cascades two independent 2× stages. Each stage owns
// its own up- and down-FIR delay lines; the shared H[] coefficient table
// is static (one allocation for the whole machine).
//
// Latency: the FIR has linear phase with group delay = (TAPS−1)/2 = 15
// samples at the OS rate, i.e. 7.5 source samples per 2× stage. For 4×
// (two cascaded stages), that's ~11.25 source samples one-way. Effects
// machines in ReBuzz don't report latency to the host, so this manifests
// as a small audible pre-delay on the wet path — usually unnoticeable
// at ~250 µs (48 kHz) but worth being aware of when timing is critical.

using System;

namespace PedalFolder
{
    // ── HalfBandFir ──────────────────────────────────────────────────────────
    //
    // A length-31 symmetric half-band FIR with computed Blackman-windowed
    // sinc coefficients. One instance = one filter delay line. Shared static
    // H[] holds the taps.
    public sealed class HalfBandFir
    {
        public const int TAPS = 31;
        private const int CENTER = (TAPS - 1) / 2; // = 15

        // Coefficient table computed once at first class load.
        private static readonly float[] H = new float[TAPS];

        // Static constructor: derive the coefficients from math rather than
        // pasting opaque magic numbers, so they're auditable and parameter
        // tweaks (TAPS, window choice) just work.
        //
        // h_ideal[k] = 0.5 · sinc(0.5·(k−CENTER))   (half-band LP, fc = Fs/4)
        // Then multiplied by a Blackman window.
        // Finally normalised so the coefficients sum to 1.0 — Blackman
        // attenuates the outer taps and would otherwise shave ~0.5 dB off
        // the DC response.
        static HalfBandFir()
        {
            float sum = 0f;
            for (int k = 0; k < TAPS; k++)
            {
                int c = k - CENTER;
                float h;
                if (c == 0)
                {
                    h = 0.5f;
                }
                else if ((c & 1) == 0)
                {
                    // Half-band: every even-offset tap (except centre) is exactly 0
                    // — the sinc happens to evaluate to zero at integer arguments,
                    // and our arg = π·c/2 is an integer multiple of π when c is even.
                    h = 0f;
                }
                else
                {
                    float arg = MathF.PI * c * 0.5f;
                    float sinc = MathF.Sin(arg) / arg;
                    h = 0.5f * sinc;
                }

                // Blackman window — wider main lobe than Hamming but ~10 dB
                // deeper stopband, which matters more for an oversampler than
                // transition sharpness.
                float pos = k / (float)(TAPS - 1);
                float win = 0.42f
                          - 0.50f * MathF.Cos(2f * MathF.PI * pos)
                          + 0.08f * MathF.Cos(4f * MathF.PI * pos);

                H[k] = h * win;
                sum += H[k];
            }

            // Normalise to DC gain 1.0.
            if (sum > 1e-6f)
            {
                float inv = 1f / sum;
                for (int k = 0; k < TAPS; k++) H[k] *= inv;
            }
        }

        // Ring-buffer delay line. _delayPos is the slot where the NEXT input
        // will be written; the slot just before it (mod TAPS) holds the most
        // recent input. Convolution walks backwards from _delayPos to read
        // newest-first matching H[0..TAPS-1].
        private readonly float[] _delay = new float[TAPS];
        private int _delayPos = 0;

        public void Reset()
        {
            Array.Clear(_delay, 0, TAPS);
            _delayPos = 0;
        }

        /// <summary>
        /// Push one sample through the FIR. Returns the filtered sample
        /// (one output per input — the rate-change happens in the
        /// containing Oversampler, not here).
        /// </summary>
        public float Process(float x)
        {
            _delay[_delayPos] = x;

            // y[n] = Σ H[k] · x[n−k]
            // _delay[_delayPos] is x[n] (just written), so walk index backwards
            // (with wrap) to read x[n], x[n−1], … x[n−TAPS+1] paired with
            // H[0], H[1], … H[TAPS−1].
            float sum = 0f;
            int idx = _delayPos;
            for (int k = 0; k < TAPS; k++)
            {
                sum += H[k] * _delay[idx];
                idx--;
                if (idx < 0) idx = TAPS - 1;
            }

            _delayPos++;
            if (_delayPos >= TAPS) _delayPos = 0;

            return sum;
        }
    }


    // ── Oversampler ──────────────────────────────────────────────────────────
    //
    // Façade for 1×/2×/4× oversampling. Factor is fixed at construction;
    // PedalFolder owns one instance per (channel × factor) and dispatches
    // by reference based on the active OS parameter. Factor 1 is a passthrough
    // — included so callers can use the same Up()/Down() shape regardless of
    // whether oversampling is active.
    public sealed class Oversampler
    {
        public readonly int Factor;          // 1, 2, or 4

        // Buffer[k] holds the k-th OS-rate sample for the current source
        // step. Callers read from / write to it between Up() and Down().
        // Length = Factor; allocated once at construction.
        public readonly float[] Buffer;

        // Stage 1 owns the source⇄2× rate transition (used for both 2× and 4×).
        // Stage 2 owns the 2×⇄4× transition (used only for 4×).
        // Up and Down filters are independent instances — they have separate
        // delay-line state for the two directions.
        private readonly HalfBandFir _upStage1, _downStage1;
        private readonly HalfBandFir _upStage2, _downStage2;

        public Oversampler(int factor)
        {
            if (factor != 1 && factor != 2 && factor != 4)
                throw new ArgumentException("Factor must be 1, 2, or 4.", nameof(factor));

            Factor = factor;
            Buffer = new float[factor];

            if (factor >= 2)
            {
                _upStage1   = new HalfBandFir();
                _downStage1 = new HalfBandFir();
            }
            if (factor >= 4)
            {
                _upStage2   = new HalfBandFir();
                _downStage2 = new HalfBandFir();
            }
        }

        public void Reset()
        {
            Array.Clear(Buffer, 0, Buffer.Length);
            _upStage1?.Reset();
            _downStage1?.Reset();
            _upStage2?.Reset();
            _downStage2?.Reset();
        }

        /// <summary>
        /// Upsample one source-rate input into Factor OS-rate samples,
        /// written into Buffer[0..Factor−1].
        ///
        /// For Factor=2: standard zero-stuff + half-band LP. The ×2 gain on
        /// the live sample compensates the inherent halving from zero-stuffing,
        /// so DC throughput is unity.
        ///
        /// For Factor=4: two cascaded 2× stages. Each level applies its own
        /// ×2 gain on its live sample.
        /// </summary>
        public void Up(float input)
        {
            if (Factor == 1)
            {
                Buffer[0] = input;
                return;
            }

            if (Factor == 2)
            {
                Buffer[0] = _upStage1.Process(input * 2f);
                Buffer[1] = _upStage1.Process(0f);
                return;
            }

            // Factor == 4: stage 1 produces two 2×-rate samples, each of
            // which is then zero-stuffed and run through stage 2 to give
            // four 4×-rate samples.
            float s1a = _upStage1.Process(input * 2f);
            float s1b = _upStage1.Process(0f);

            Buffer[0] = _upStage2.Process(s1a * 2f);
            Buffer[1] = _upStage2.Process(0f);
            Buffer[2] = _upStage2.Process(s1b * 2f);
            Buffer[3] = _upStage2.Process(0f);
        }

        /// <summary>
        /// Downsample the OS-rate samples currently in Buffer to a single
        /// source-rate output. All OS samples are run through the LP first
        /// to anti-alias; the second of each pair is retained on decimation
        /// (the choice is arbitrary as long as it's consistent — affects
        /// only the through-path's fractional-sample delay, which is fixed).
        /// </summary>
        public float Down()
        {
            if (Factor == 1)
            {
                return Buffer[0];
            }

            if (Factor == 2)
            {
                _downStage1.Process(Buffer[0]);          // pre-filter; discard
                return _downStage1.Process(Buffer[1]);   // pre-filter; keep
            }

            // Factor == 4: stage 2 reduces 4 → 2, stage 1 reduces 2 → 1.
            _downStage2.Process(Buffer[0]);
            float t0 = _downStage2.Process(Buffer[1]);
            _downStage2.Process(Buffer[2]);
            float t1 = _downStage2.Process(Buffer[3]);

            _downStage1.Process(t0);
            return _downStage1.Process(t1);
        }
    }
}
