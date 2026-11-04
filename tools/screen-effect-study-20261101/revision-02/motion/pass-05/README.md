# motion pass-05

Artifacts: `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-05`.

The main mode is `combined`: the calibrated local world deformation is projected first, then the reference's measured global2D similarity is applied to vertex clip coordinates. There is no depth translation/rotation proxy or qflat blend. Estimated shapes and12px boundary constraints remain. This is not pure3D reconstruction.

Every display mode uses the same1.35 crop and fixed independent display/glass/scanlines. Raw display=false calibration has no display crop. All-off comparison uses the same new crop, not pass02 pixel identity.

Pass02 smoke and frozen foreground are byte-identical. Same-B states at t/t+1.7 retain the timing and exit order. `smoke-measurements.json` measures old/new scalar quadrants and new contribution brightness on the actual current B display. All104 scalar RGBA field proofs are checked against pass02.

`prior-passes-freeze.json` records1087 existing pass02/pass03/pass04 source/output files. The original pass01 freeze376 files is also checked. Pass03 is a rejected depth-proxy experiment with a completed video. Pass04 is a still-only experiment; video was deferred for thin ownership boundary seams.

Build after the renderer and helper hashes are frozen:

1. Run `package.py` with Python3.11.
2. Run `fields.mjs` with Node.
3. Run `capture.mjs --stills-only` with Node.
4. Run `measure.py` with Python3.11 and inspect native output.
5. Parent must inspect actual8.5/10.5 before full469frame capture.
6. Run `capture.mjs` without the still-only flag.
7. Run `compare.py`, then `finalize.py` with Python3.11.

The parent confirmation is recorded in `parent-still-confirmation.json` before full video capture. Finalization checks source hashes, earlier output immutability, toggle differences, same-crop zero/all-off baselines, full black hold, real file:// standalone output, H264 dimensions/frame count/audio absence and every delivered PNG. Existing servers and Unity are untouched; capture owns an isolated browser and port0 server.
