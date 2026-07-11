# Pedal Folder

A stereo wavefolding distortion effect for ReBuzz.

## What it does

Wavefolding is a flavour of distortion where the signal **reflects over
itself** as it grows past a threshold — rather than clipping flat
(saturation) it bounces back, creating bright, complex harmonics that
sit somewhere between a fuzz, a ring modulator, and an FM operator.
The classic analog versions live in West Coast synths (Buchla 259,
Make Noise DPO, etc.); this is a digital take that follows Jatin
Chowdhury's "saturating feedback wavefolder" topology.

## Algorithm

Per sample, per channel:

```
in     = x                                          ; raw input
pre    = PreTilt(in)                                ; tilt EQ at 800 Hz
x_drv  = pre · Drive  +  Bias                       ; pre-fold gain + DC offset
z      = tanh(x_drv) + Feedback · tanh(y_prev)      ; sat'd feedforward + feedback
y      = z − Fold · S(x_drv)                        ; wavefolder injection
y_prev ← y                                          ; one-sample state
out    = (1 − Mix) · in  +  Mix · Output · y        ; trim and dry/wet
```

`S(·)` is the **Shape**-selected fold curve (Sine / Triangle / Wrap);
the whole inner loop optionally runs at 2× or 4× the host sample rate
under **OS**.

A few useful degenerate cases fall out for free:

| Drive | Fold | Feedback | Sound                          |
|-------|------|----------|--------------------------------|
| low   | 0    | 0        | clean (unity tanh)             |
| high  | 0    | 0        | hard tanh saturation           |
| high  | high | 0        | classic saturating wavefolder  |
| high  | high | high     | feedback wavefolder (resonant) |

## Parameters

### Core (v1.0)

- **Drive** (0–127, default 32) — pre-fold input gain, exponentially
  mapped from 1× (clean) to ~20×. Higher = more folds per cycle and
  more harmonic content.

- **Fold** (0–127, default 64) — wavefolder injection amount. 0 mutes
  the sin-fold term so you get plain tanh saturation; 64 hits the
  "G ≈ 0.5" recommendation from the source paper; 127 is maximum fold
  (gnarly).

- **Feedback** (0–127, default 0) — feedback coefficient. 0 gives the
  static saturating wavefolder. Increasing it adds a resonance-like
  movement that tracks the wavefolder curve. Capped internally at 0.95
  for stability.

- **Output** (0–127, default 64 = unity) — post-fold trim. 0 = −24 dB,
  64 = 0 dB, 127 = +12 dB.

- **Mix** (0–127, default 127 = wet) — dry/wet blend. The dry path
  uses the raw input (no PreTilt), so parallel processing keeps the
  original tone intact while adding fold harmonics.

### v1.1 additions

- **Bias** (0–127, default 64) — pre-fold DC offset, sweeping from
  ~−2.0 to ~+2.0. 64 is centred (no bias). Off-centre values shove
  the input into the *asymmetric* part of the fold curve, which
  unlocks even harmonics (the classic "third harmonic of the third
  harmonic"-style spectrum that pure symmetric wavefolders won't
  give you). Wide range — even at low Drive, Bias near ±1.0 already
  sits at the first fold lobe.

- **PreTilt** (0–127, default 64) — tilt EQ before the folder, pivoting
  at 800 Hz. Below 64, lows are boosted and highs cut, so the fold
  responds mostly to bass content. Above 64, the reverse — fold reacts
  to the brighter half of the spectrum. ±12 dB per side (24 dB total
  tilt). The dry path is *not* affected; PreTilt only sculpts what
  goes into the folder. Great for keeping percussion attacks intact
  while still folding the sustain.

- **Shape** (Sine / Triangle / Wrap, default Sine) — the fold curve.
  - **Sine** is the canonical wavefolder shape — smooth, bell-shaped
    harmonic envelope. The "musical" default.
  - **Triangle** is a piecewise-linear triangle wave at the same period.
    Sharper corners → richer high harmonics, an odd-harmonic-leaning
    spectrum. Buzzier than sine.
  - **Wrap** is a sawtooth wrap (think integer-overflow): hard
    discontinuities at every ±1 boundary. Industrial / glitchy /
    bitcrush-adjacent. Aliases aggressively at 1× OS — pair with OS=2×
    or 4× when you actually want it to sound musical rather than
    indiscriminately fizzy.

  (An earlier plan called the third shape "Hyperbolic"; that was a
  misnomer — sinh/tanh aren't periodic in their argument so they can't
  fold, they just saturate. Wrap is the actually-distinct third
  periodic fold curve.)

- **OS** (1× / 2× / 4×, default 2×) — oversampling factor. The fold
  step can generate harmonics well above the host's Nyquist, and
  without oversampling those reflect back into the audible band as
  inharmonic aliasing. The defaults sound notably cleaner than the
  v1.0 no-oversample behaviour. **1×** preserves the v1.0 character
  (handy if you want the aliasing fuzz as an effect in itself); **2×**
  is the sweet spot for most material; **4×** is the strict choice for
  Wrap/Triangle at high Drive. CPU cost scales roughly linearly with
  the factor.

  Implementation: 31-tap symmetric half-band FIR with Blackman-windowed
  sinc coefficients (computed at type-init — auditable, no magic
  numbers). ~70 dB stopband, group delay ≈ 7.5 source samples per 2×
  stage (~11 samples one-way for 4× cascade).

## Tips

- **Bias + low Drive** is its own world. With Drive at default (32)
  and Bias swept toward 127, the input gets pushed into the fold from
  the offset alone — you get fold harmonics with way less audible
  gain than cranking Drive does.

- **PreTilt down + Wrap shape** keeps the worst aliasing out of the
  audible band by pre-filtering before the harsh fold curve sees it.
  Combine with 4× OS for the cleanest "glitch" tone.

- **Try Triangle on bass.** Sine is too smooth for some kick-drum and
  bass-synth duty; Triangle's sharper folds add the bite of analog
  fuzz without going full Wrap.

- **Modulating Bias from an LFO** is the wobbliest motion this
  machine can produce on its own — much more interesting than
  modulating Drive at the same depth. Smoothing is 10 ms, so LFOs
  up to ~25 Hz pass through without colouration.

- **Watch your output levels.** Wavefolders sit in the same "broken
  fuzz" tonal area as ring mod and FM — easy to push above 0 dBFS,
  and Bias can make that worse by pushing the operating point off
  centre.

## Known limitations

- **OS changes can briefly click.** When you switch OS factor
  mid-playback, the previously-inactive oversampler's FIR delay line
  is in a stale state; resuming it produces a short transient. OS is
  meant as a setup-time decision, not an automation target.

- **Feedback character moves with OS.** The Feedback path uses the
  wavefolder's one-sample state, which lives at the OS rate. At 4×
  OS, the resonance-like motion sits four times higher in frequency
  than at 1× — usually subtle, but if you've tuned a patch at 1× the
  feedback flavour will change when you switch to 4×. (Solution:
  pick OS first, dial Feedback last.)

- **No DC blocker.** Bias intentionally puts DC on the wet signal;
  blocking it inside the machine would fight the parameter. If you
  need DC-clean output downstream, follow with a high-pass.

## Credits

Algorithm based on Jatin Chowdhury's article
["Complex Nonlinearities Episode 1: Wavefolder"](https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html),
which itself draws on the Buchla 259 wavefolder analysis and standard
digital wavefolding techniques. Bias, PreTilt, and the Triangle/Wrap
shapes are local additions; the oversampler is a straightforward
windowed-sinc FIR.

## Build

```
dotnet build "Pedal Folder.NET.csproj" -c Release
```

The post-build target copies the DLL to
`C:\Program Files\ReBuzz\Gear\Effects\` automatically. Close ReBuzz
before rebuilding — the running process holds a lock on the DLL and
the copy will fail silently otherwise (the build will still succeed
because of `ContinueOnError`, but the gear folder won't update).

## Version

v1.1 — adds Bias, PreTilt, Shape (Sine/Triangle/Wrap), and OS (1×/2×/4×).
Preset-compatible with v1.0: old presets load with the new parameters
at their defaults, which preserves v1.0 character except that **OS
defaults to 2×** (the only audible change). Set OS to 1× to recover
exact v1.0 behaviour.

---

## License & credits

Licensed under the **GNU General Public License v3.0** — see the `LICENSE`
file for the full text.

The core fold algorithm follows Jatin Chowdhury's *Complex Nonlinearities:
Wavefolder* (https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html),
extended here with a bias offset, selectable fold shapes, pre-fold tilt EQ
and oversampling.
