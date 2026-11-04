# VirtualWalk visitor assets

These Editor-only assets belong to `VisitorAvatar`. They do not replace the production
`Remy.fbx`, its import settings, or `Resources/ShowActors/Remy.prefab`.

- `Walking.fbx` is the single `Models/Walking.fbx` entry from the owner's existing
  `G:/マイドライブ/shubie/archive/cold/projects/Models.zip` archive. FBX 7700,
  animation stack `mixamo.com`, 32 keys at 30 fps, 0 through 1.033333 seconds.
  SHA-256: `0375fe7c9fc9ad312973f9b4bf30a2341d020b7eb8b906cabe141fb6655ce5b5`.
- `WalkingBind.fbx` has identical bytes and disables animation import. Its static
  T-pose supplies the source bind rotations. The animated model's initial pose is
  already a walking pose in Unity; using it as a bind pose cancels the arm lowering.
- Seven PNG files are copied byte-for-byte from the `Video/Content` records of
  `Assets/ThirdParty/Mixamo/Remy.fbx`. Diffuse: Shoes, Top, Bottom, Hair (1024²),
  Body (2048²). Opacity: Hair (1024²), Body (2048², for eyelashes).
  The extraction script prints the byte counts and hashes. It does not extract
  the archive's other entries or overwrite the production source.
- Regenerate with `py -3.11 Assets/Editor/VirtualWalkAssets/extract.py`.
  Python standard library only. The archive must be mounted at the path above.

The walk contains 65 named skeleton bones shared with Remy, but its bone lengths
and pre-rotations differ. `VisitorAvatar` samples the Generic clip on a private
source skeleton from `WalkingBind.fbx` and transfers bind-relative local rotations to Remy's existing
bones. It retains Remy's bone lengths and only transfers the scaled vertical hip
displacement. Source root and horizontal hip displacement never move the visitor.
`NpcWalkPose` supplies the idle stance only; the walking motion comes from the FBX.

The FBX is in place, so it has no root travel from which to read stride. At creation,
the avatar samples one cycle at 120 intervals after transfer to the scaled Remy.
It measures each toe's backwards speed within 12 mm of that toe's minimum height.
The median support speed multiplied by the cycle duration gives its two-step stride
in metres. `Description` reports the resulting stride, clip duration, frame rate,
bone count and measured radius. The caller supplies cumulative path distance in
metres and a continuous standing/walking blend to `Apply`.
Creation checks that the source bind arms are horizontal and that the transferred
walking frame's arms point downwards. The description also gives `tposeRadius` as
an independent broad-pose calibration of the mesh measurement.

CPU skinning with the mesh's original vertices, bone weights, bind matrices and
current bone transforms places the lowest shoe sole on the caller's root floor Y
each sample. This avoids EditMode's cached render skin matrices. `MaxBodyRadiusM`
measures the furthest skinned vertex from the root in
the horizontal plane over 32 walking poses and the idle stance, including hands
and feet. It is a geometric envelope rather than a torso-only collision radius.
`IdleBodyRadiusM` measures the same vertices after `SamplePose(0, 0)`. The description
reports it as `idleRadius`. `Footprint()` reads the current pose, including the
latest `Apply` sole correction, and returns the convex hull of all skinned vertices
in world XZ coordinates. The vertices are counterclockwise with no repeated closing
point. Duplicate and collinear interior points are removed; non-finite XZ values
fail explicitly. Its working lists are reused; the returned array belongs to the
caller. Navigation can use the idle radius while testing this actual oriented
full-body hull against obstacles at each sample.

All seven Remy meshes receive temporary URP/Lit materials. Eyes share the body
atlas. Hair and eyelashes combine diffuse RGB with the corresponding opacity map
and use alpha clipping. Temporary objects are registered in the caller's `trash`
list. `Dispose` marks the avatar unusable; that shared list owns Unity destruction.
