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
x_drv = x · Drive                                   ; pre-fold gain
z     = tanh(x_drv) + Feedback · tanh(y_prev)       ; sat'd feedforward + feedback
y     = z − Fold · sin(π · x_drv)                   ; wavefolder injection
y_prev ← y                                          ; one-sample state
out   = (1 − Mix) · x  +  Mix · Output · y          ; trim and dry/wet
```

Three useful degenerate cases fall out for free:

| Drive | Fold | Feedback | Sound                          |
|-------|------|----------|--------------------------------|
| low   | 0    | 0        | clean (unity tanh)             |
| high  | 0    | 0        | hard tanh saturation           |
| high  | high | 0        | classic saturating wavefolder  |
| high  | high | high     | feedback wavefolder (resonant) |

## Parameters

- **Drive** (0–127, default 32) — pre-fold input gain, exponentially
  mapped from 1× (clean) to ~20×. Higher = more folds per cycle and
  more harmonic content.

- **Fold** (0–127, default 64) — wavefolder injection amount. 0 mutes
  the sin-fold term so you get plain tanh saturation; 64 hits the
  "G ≈ 0.5" recommendation from the source paper; 127 is maximum fold
  (gnarly).

- **Feedback** (0–127, default 0) — feedback coefficient. 0 gives the
  static saturating wavefolder. Increasing it adds a resonance-like
  movement that tracks the wavefolder curve — subtle but distinctive,
  the most "alive" of the three modes. Capped internally at 0.95 for
  stability; values near max are where the character is strongest.

- **Output** (0–127, default 64 = unity) — post-fold trim. The fold
  step can boost peak level substantially (heavy fold can easily
  produce +12 dB peaks from −6 dB input), so this defaults to unity
  and the useful range skews toward attenuation: 0 = −24 dB,
  64 = 0 dB, 127 = +12 dB.

- **Mix** (0–127, default 127 = wet) — dry/wet blend. Parallel
  processing is worth trying for percussion or mix-bus duty where you
  want the harmonics added in without losing the original transient
  shape.

## Tips

- **Start with Fold ≈ 64 and Drive ≈ 32**, then push Drive up. The
  number of folds you hear scales with Drive, not Fold; Fold sets the
  intensity of each fold.
- **Add a touch of Feedback** (~16–32) for a slightly haunted,
  resonant quality. Past ~96 it becomes very obvious.
- **Bass loses fundamentals fast** under heavy fold. Mix in some dry
  signal to keep the low end intact.
- **Watch your output levels.** Wavefolders sit in the same "broken
  fuzz" tonal area as ring mod and FM — easy to push above 0 dBFS.

## Known limitations

- **No oversampling in v1.0.** The sin-fold harmonics extend high
  enough to alias at the host's native sample rate, especially at
  high Drive values. Per the source page, the feedback path's mild
  integrating effect cancels some of the worst high-frequency content,
  but you'll still hear aliasing on bright sources at high drive
  settings. Pragmatic workaround for now: run at 96 kHz, or back off
  Drive on bright material. Oversampling is on the roadmap for v1.1
  (4× polyphase is the planned starting point per Jatin's recommendation
  on the source page).

## Credits

Algorithm based on Jatin Chowdhury's article
["Complex Nonlinearities Episode 1: Wavefolder"](https://ccrma.stanford.edu/~jatin/ComplexNonlinearities/Wavefolder.html),
which itself draws on the Buchla 259 wavefolder analysis and standard
digital wavefolding techniques.

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

v1.0
