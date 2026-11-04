# motion pass-02

Generated artifacts are at `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-02`.

This candidate changes `smoke-fields.js` only. `motion-revision.js`, `effects.js`, and the source UI template are byte-identical to pass-01. Packaging corrects the diagnostic link for the extra directory. Spatial sources and assets are read-only and hash-checked. Same-B/two-state and estimated-depth limitations remain.

Reproduce in order with existing dependencies:

1. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-02/package.py`
2. `node tools/screen-effect-study-20261101/revision-02/motion/pass-02/fields.mjs`
3. `node tools/screen-effect-study-20261101/revision-02/motion/pass-02/capture.mjs --stills-only`
4. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-02/measure.py`
5. View all 13 Stage1 native stills, neutral/black flats and actual B contributions.
6. `node tools/screen-effect-study-20261101/revision-02/motion/pass-02/capture.mjs`
7. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-02/compare.py`
8. `py -3.11 tools/screen-effect-study-20261101/revision-02/motion/pass-02/finalize.py`

`prepare.py` was used once to record predictions and freeze the pre-existing 376 pass-01 files. Do not rerun it after editing the pass-02 scripts: it is provenance of initialization, not a build step. `pass-01-freeze.json` is checked at finalization. Capture owns a temporary port0 server and an isolated headless Chrome; it closes both. Existing servers and Unity are untouched.

Measurements use scalar alpha area and mean-RGB>20 bright pixels of the **actual independent-display B multiplied by new visibility**. They exclude the old scene, foil, glyph, red and white foreground. Thus dark floor/curtain content cannot pass a flat-only opening check.

The front's nominal full width is 3–9px in 720-wide UV distance units; geometric front orientation affects its raster width. The seeded multiscale material is advected continuously. No reference RGB or alpha frames are effect assets.
