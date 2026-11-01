# motion pass-03

Artifacts: `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-03`.

Pass-02 smoke and frozen foreground are byte-identical. The spatial renderer supplies combined motion: global reference timing and a depth proxy plus internal world deformation. Ownership boundaries follow the global similarity while the interior blends toward the depth projection. This is a camera-dependent approximation, not pure 3D recovery.

All `display:true` modes share the same1.35 crop and fixed independent display/glass/scanlines. Calibration with `display:false` has no display crop and must be reported separately. The all-off comparison uses the new cropped independent-display baseline; it does not claim pixel identity to pass-02.

The main mode is `combined`. The controls and two native four-way sheets include `combined/global/local/none`. Same-B view states at t and t+1.7 retain the pass-02 smoke timing. Original source rooms are outside scope.

Build order after the spatial source hashes are frozen:

1. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-03/package.py`
2. `node tools/screen-effect-study-20261101/revision-02/motion/pass-03/fields.mjs`
3. `node tools/screen-effect-study-20261101/revision-02/motion/pass-03/capture.mjs --stills-only`
4. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-03/measure.py`
5. Inspect native stills and four-way controls. Once accepted, run capture without `--stills-only`.
6. Run `compare.py`, then `finalize.py` in this folder.

`prepare.py` and `configure.py` record one-time initialization and should not be rerun after subsequent edits. `pass-02-freeze.json` stores407 unchanged source and artifact files from pass-02. The original pass-01 freeze is also verified during finalization.
