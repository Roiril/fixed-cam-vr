# revision-02 / independent smoke fields

`smoke-fields.js` exports `drawSmokeAt(t, width=720, height=540, options={})`.
The first four return members are canvases:

- `oldVisibilityCanvas`: scalar old-view transmission in RGB, alpha 255.
- `newVisibilityCanvas`: scalar new-view transmission in RGB, alpha 255.
- `smokeDensityCanvas`: scalar opacity in RGB, alpha 255.
- `edgeParticlesCanvas`: white RGB, particle intensity in alpha. Add separately; this is not the smoke opacity.

`options.diagnostics=true` also returns `fields.entryA`, `fields.oldWindow`, `fields.exitB`, `fields.endingC`.
All coordinates use the reference active 1440×1080 rectangle normalized to 720×540.
The fields describe screen-space smoke. Geometry-dependent background warping is supplied by the parent renderer.

Compose old-view RGB × old visibility + new-view RGB × new visibility. Clamp the sum of the two byte-quantized transmissions to one if necessary. Add the particle layer separately. No gray source/scatter color is baked into the opacity. The module intentionally accepts separate old and new views; the proof uses the same adopted B pixels for both, so the proof does not claim a new scene transition.

`options.enabled=false` gives old=0, new=1, density=0, particles=0. This is an identity operation for the parent's selected new view. Keep the parent's black-hold envelope outside the smoke ON/OFF control. With smoke enabled, the module also sets both views and particles to zero for [13.28,13.82).

Canvases are cached by size and overwritten on the next call. Upload/copy them immediately when two timestamps must coexist. `sampleSmokeFields` returns independent Float32Array samples for numerical diagnostics.

The material uses four fixed seeded noise grids. Continuous advection changes sampling coordinates; no frame-index seed or per-frame random regeneration is used. Temporal interpolation changes radii and signed distances before converting them to opacity. No alpha-frame crossfade is used. The procedural material contains no reference RGB, source silhouette or extracted alpha. The reference hash in `smokeFieldProvenance` identifies the timing/appearance observations only.

## Verification outputs

`G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/smoke-proof/` contains:

- 80 individual field PNGs at 720×540 across 10 requested times.
- 30 composites over black / neutral / white flat backgrounds.
- 10 decompositions with all eight fields at native resolution.
- 10 before/after comparisons preserving the adopted B input at 1280×1030. Only the scalar field is resized for this normalized-UV diagnostic; camera/CRT mapping is not claimed.
- `manifest.json` with raw field SHA256, intensity/partial pixel quantities and deterministic-seek, OFF-identity and black-hold checks.

Reproduce with `node inspect-smoke.mjs`, then `py -3.11 inspect-smoke.py` from this source directory or using their absolute paths. The scripts do not start a browser/server or touch shared ports.

At 4.1 seconds the old upper window and central lower new aperture coexist. The left bottom stays shut until the later floor opening. At 12.4 and 12.6 the interior vertical density divides the lower remnants. The previous broad gray crossfade plates are absent in the scalar/material proof. The new contours remain a procedural approximation; reference local velocities and exact edge shapes have not been fitted.
