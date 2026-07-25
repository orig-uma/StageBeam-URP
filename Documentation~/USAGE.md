# Stage Beam — 使い方 (Usage)

Volumetric stage-light beams for URP. This guide gets a beam on screen in two steps, with no
MVR/DMX/GDTF knowledge required.

## 1. Install

This package lives at `Packages/com.origuma.stage-beam` and has no external dependencies beyond
URP (Unity 6000.0+; URP is declared as a package dependency, so Unity resolves it automatically).
If it isn't already referenced by your project's `manifest.json`, add it as a local/embedded
package.

Everything the package needs at runtime (shaders, the occlusion compute shader, the bundled haze
noise) ships in a `Resources` folder inside the package, so it is included in player builds
automatically — no "Always Included Shaders" or other build setup required.

## 2. Add the renderer feature

The beams are drawn by a `ScriptableRendererFeature`, so your URP Renderer asset needs it once,
project-wide. Open the setup window:

- **Menu**: `Window > Origuma > Stage Beam Setup`, or
- **Inspector button**: when a `StageBeamLight` is selected and the feature is missing, its
  inspector shows a warning with an **Open Stage Beam Setup** button.

The window lists every Renderer Data used by the active URP assets (default pipeline + all
quality levels) with an **Add** / **Remove** / **Active** control per renderer, plus a manual
Renderer Data field for assets it can't discover. It also warns if Render Graph Compatibility
Mode is enabled (the beam pass is Render Graph only).

(Manual alternative: select your URP Renderer asset and **Add Renderer Feature > Stage Beam
Renderer Feature** in the Inspector.)

Without this step beams are simulated but nothing is drawn.

## 3. Add a beam

Two equivalent ways:

- **GameObject menu**: `GameObject > Stage Beam > Stage Beam Light`.
- **Add Component**: select any GameObject, `Add Component > Stage Beam / Stage Beam Light`.

That's it — a `Stage Beam Driver` GameObject is created automatically the first time a
`StageBeamLight` is enabled, and its beam material is auto-created on first use (the runtime
instance shows up in the driver's **Material** field; assign your own only to override the
shared cone material). Move/rotate the object to aim the beam; by default the beam exits
along local **-Y** (the fixture convention: local **+Y** is "up"). Set the component's **Beam
Axis** field to **Positive Z** if you'd rather aim it like a Unity spotlight, down
`transform.forward`.

## Parameter guide

| Field | What it does |
|---|---|
| Beam Axis | `NegativeY` = fixture convention, beam exits local -Y. `PositiveZ` = aim like a spotlight, down `transform.forward`. |
| Color | Beam colour. |
| Intensity | Overall brightness multiplier. |
| Conserve Flux | Real-fixture zoom behaviour (default on): the same total light spread over the cone's solid angle, so widening Spot Angle dims the haze per unit volume — a character inside a wide-zoom beam stays visible — and a tight beam gets hot. |
| Flux Reference Angle | Spot Angle at which Intensity applies exactly ×1 (default 30°). |
| Range | Maximum throw distance, in metres. |
| Spot Angle | Full field (outer) cone angle in degrees — total angular width of the beam. |
| Beam Angle | Full beam (hotspot) cone angle in degrees — the brighter core; clamped to ≤ Spot Angle. |
| Start Radius | Lens radius in metres — the beam's width at the source. |
| Edge Softness | Softness of the beam's outer edge. |
| Density | Overall volume density (raymarch accumulation strength). |
| Anisotropy | Henyey-Greenstein scattering anisotropy *g*. 0 = uniform brightness from every viewing angle (legacy look). 0.5–0.7 = forward scattering: beams pointing at the camera flare up, the way real haze reads under stage lighting. Negative = back scattering. |
| Axial Falloff | How strongly the beam fades along its length; 0 = no fade, 2 = fades fast. |
| Hotspot | Strength of the bright core inside the beam-angle cone. |
| Root Boost | Extra brightness near the lens (source flare/glare); 0 = off. |
| Root Boost Frac | Length of the root glare, as a fraction of Range. |
| Root White | How much the root glare desaturates toward white vs. tints with Color. |
| Raymarch Steps | Sample count per pixel. Higher = smoother gradients, more GPU cost. |
| Depth Occlude | Clip the beam against scene depth so it stops at walls/floors. |
| Gobo | Optional texture projected through the beam. None = plain cone. |
| Gobo Rotation Deg | Static rotation offset of the gobo. |
| Gobo Rotation Speed Deg Per Sec | Continuous gobo spin; 0 = static. |

## Haze noise

The renderer feature's **Haze Strength** slider adds drifting-atmosphere turbulence to every
beam. You don't need to assign a texture: with **Haze Noise** left empty, raising the strength
above 0 automatically uses the tileable 3D noise bundled with the package. Assign your own
`Texture3D` only if you want a different look.

## Volumetric shadows

Beams can self-shadow against performers/props standing in the light. On the Stage Beam Renderer
Feature set **Shadows**:

- **Off** — no shadowing (default, zero cost).
- **Screen Space** — marches the camera depth buffer toward the light. Only occluders visible on
  screen can shadow.
- **Light Shadow Map** — **highest quality**: each beam samples the URP shadow map of a real
  spot light co-located with the fixture, giving geometry-exact silhouettes (limbs, fingers,
  cloth — no sphere/voxel approximation, no dither noise; the projected floor pool is masked
  by the same shadow). Requirements: the fixture needs a real shadowed spot Light — the MVR
  path gets this automatically from `StageBeamLightSync` (set its **Shadows** to Soft and keep
  **Max Lights** modest); a standalone `StageBeamLight` uses its **Shadow Light** slot (or any
  child Light). URP asset: additional light shadows must be enabled. Beams without a visible
  shadowed light render unshadowed in this mode.
- **Volume** — shared world-space occupancy volume, **built automatically — no scene setup**,
  and the cost is independent of the light count (the mode for BIG rigs — hundreds of beams).
  With **Mesh Voxelize** (default on) the occluders' REAL meshes are rasterized into the
  volume from three axes: silhouettes are the actual render geometry — skinned poses and
  cloth deformation included — at voxel resolution (~3–4 cm at the default fit), then softly
  dilated so the shadow march can't miss thin features. No spheres, no capsules, no per-object
  setup. (Mesh Voxelize off = legacy sphere/box approximation, cheaper on very weak GPUs.)
  Accuracy has a price: rasterizing costs one draw call **per renderer per axis**, so a scene of
  animated characters runs into the hundreds — see the Occluder Hint note below for the lever.
  Occluders on the feature's **Occluder Mask** layers are discovered every frame and voxelized
  once per frame; the cost is independent of the number of beams. Static meshes become oriented
  boxes straight from their renderer bounds (no setup needed — walls, risers, panels and cases
  occlude with their real silhouette); skinned meshes become per-bone spheres so dancers read
  as limbs — sized from the skeleton's actual bone positions, so deliberately inflated
  renderer bounds (anti-culling-pop workarounds) neither fatten the shadow nor get the
  character rejected as oversized. A Sphere/Capsule/Box **collider** on the renderer's GameObject is used as a better
  shape hint when present (bounds can't tell a sphere mesh from a cube — Unity's primitive
  objects already ship with the right collider). Tune the look with **Strength** (1 = pitch
  black in shadow) and **Density** (occluder opacity per metre).

Optional override: if you need manual control (box placement/size, resolution, discovery
tuning, debug gizmos), add `GameObject > Stage Beam > Occlusion Volume (Override)`. While
one is enabled it owns the volume and the feature's automatic build (and its Volume-mode
fields) steps aside.

**Seeing what occludes**: add `GameObject > Stage Beam > Occlusion Debug View` — it draws
the current frame's occluder shapes (orange spheres/boxes) and the volume box (blue) in the
Scene view, for both the automatic and the override path. This is the first thing to reach
for when a shadow looks wrong.

**Characters with cloth simulation (e.g. MagicaCloth)**: cloth bones and proxy renderers
skew the automatic bone sampling and inflate bounds. Put a **Stage Beam Occluder Hint**
component on the character's root instead: every renderer under it then occludes as the
authored shape, drawn as gizmos — what you see in the Scene view is exactly what shadows.
The default **Humanoid Capsules** mode builds capsules along the Humanoid avatar's skeleton
(torso, head, arms, legs, plus armpit/crotch connector segments so no false light slivers
leak between limbs and torso under an overhead light): it follows animation exactly and the
Humanoid mapping never includes cloth/hair bones, so cloth sims can't skew it; **Limb
Radius** scales the whole set. It covers **every humanoid below the hint**, so one component on
a cast's root represents the whole cast. With no humanoid Animator it falls back to the single
vertical **Capsule** (height/radius fields). **Box** and **Ignore** cover set pieces and
exclusions.

A hint applies with **Mesh Voxelize either on or off**: the hinted subtree is splatted as its
authored shape and taken *out* of the raster set. That makes it a **cost lever** as well as an
accuracy fix — mesh voxelization spends a draw call per renderer per axis, so a cast of dancers
is hundreds of them, while the splat costs none. Watch the shape budget: **Max Occluders**
(articulated humanoid ≈ 17 shapes each); a warning is logged if shapes are dropped.
`Window > Origuma > Stage Beam > Log Occlusion Build Cost` prints the current counts.

**Shadow edges chattering on moving characters**: the voxel grid quantizes a moving occluder's
silhouette, so it snaps between cells frame to frame. The feature's **Volume ▸ Temporal**
slider (default 0.6) exponentially blends the shadow volume across frames so edges glide instead
of flickering — near-free (one compute pass). Higher = smoother but the shadow lags the dancer
more; drop it toward 0 if the lag is visible on fast motion. Pairs with **Noise Smoothing** (the
spatial denoise) — temporal kills the frame-to-frame chatter, spatial kills the per-pixel grain.

**Beams overpowering performers/characters**: the feature's **Master Intensity** slider
scales every beam's volumetric brightness (and the projected pools) with one dial — the
default is deliberately low (0.05) so characters inside beams stay readable; raise it for a
heavier haze look. Note: a feature added before this default existed keeps its serialized
value — adjust the slider on the asset.

**Keeping projected pools off performers**: the feature's **Projection Receiver Layers**
mask restricts the projected pool/gobo to chosen layers (an extra depth-only pass over the
receiver geometry; `Everything` = no filtering, no extra cost). Put characters on their own
layer and exclude it, and the pool stays on the floor instead of painting them white.

**Projection Shadow Hardness** (feature, default 0.1): how strongly performers occlude the
floor pool. The pool is shadowed by the same volume as the beam, but a beam stays faintly lit
where occluded (Beer-Lambert never reaches 0), so fully clamping the pool to black would
contradict the beam. Hardness remaps the pool's transmittance `saturate((s − h)/(1 − h))`: 0
leaves the pool too bright (no occlusion feel), high values contradict the beam's residual —
0.1 is the middle ground. For deeper occlusion raise **Volume ▸ Density** instead: it deepens
beam and pool together so they stay consistent.

**Soft Additive** (Stage Beam Driver ▸ Blend, the default): overlapping beams saturate softly
toward a ceiling instead of blowing out to white/bloom/ACES-yellow, so characters inside stay
readable and shadows/haze are preserved. Implemented as an offscreen HDR accumulation whose
*summed* value is passed through a ceiling curve `K·(1 − exp(−sum/K))` at composite (a naive
screen-blend inverts in HDR and makes beams vanish). **Soft Max Brightness** (`K`) is the
ceiling: lower = thinner/flatter overlaps, higher = brighter before saturating. Pools use the
same treatment.

## Performance guide

The two things that drive cost are **overlap** (how many beams stack on a pixel) and **screen
coverage** (how many pixels each beam paints) — cost ≈ covered pixels × overlap × steps. The
levers below attack those.

- **Resolution Scale** (Renderer Feature, **default Quarter**): **Full / Half / Third / Quarter** —
  raymarch pixel counts of 1/1, 1/4, **1/9** and 1/16 — composited with a depth-aware (joint
  bilateral) upsample so object silhouettes stay clean instead of stair-stepping. This is the
  **biggest fill-rate lever when beams cover the screen**, and the core look/perf tradeoff: lower =
  far cheaper, higher = crisper beam edges. Quarter is the default as a good balance.
  **Third** exists for the middle ground: the depth-aware upsample only rescues *object*
  silhouettes (depth discontinuities) — a beam's own soft edge has no depth step, so it is simply
  magnified, and gets visibly coarse as resolution drops. At high output resolutions Quarter can
  read as too coarse while Half costs 4× more pixels; Third splits that. (The old *Half Resolution*
  checkbox migrates to this enum automatically.)
- **Dither** (Renderer Feature, **default 0.15**): anti-banding applied where the beam is
  composited into the camera target. Beams are wide, smooth, low-slope ramps — exactly the signal
  that shows Mach banding once written to the camera's low-mantissa HDR format (B10G11R11, i.e.
  32-bit HDR precision). Raise until the contours break up, lower if it reads as grain; 0 = off.
  On 64-bit HDR precision there is nothing to fix and this can stay at 0.
- **Temporal Jitter** (on by default): the raymarch jitter scrolls each frame *at the haze's
  flow speed*, so the grain drifts with the fog rather than sitting as screen-fixed dirt. This
  softens the low-step/low-res grain without TAA. If the camera has **Temporal Anti-aliasing**
  on (URP: *Rendering ▸ Anti-aliasing ▸ TAA*) the time integration resolves it further — whether
  it's worth it is down to taste and your other AA. Disabling Temporal Jitter gives a static
  dither instead (raise Raymarch Steps to hide any faint grain).
- **Noise Smoothing** (on by default): a depth-aware 5×5 pass over the beam buffer before
  compositing. It averages away the raymarch/shadow jitter grain — most visible in shadowed
  regions at Half/Quarter resolution (back-lit looks especially) — for a slightly softer fog.
  Near-free; leave it on unless you want the raw grain. (Under TAA this matters less; without
  TAA it's the main grain reducer.)
- **Culling** (Renderer Feature ▸ Culling): **Frustum Cull** (on by default) drops beams whose
  bounding sphere is entirely off-screen — a free win, no visual change. **Min Screen Radius Px**
  (0 = off) additionally drops beams whose on-screen footprint is smaller than N pixels
  (distant/tiny beams whose contribution isn't visible). Both target the coverage side directly.
- **Raymarch Steps** is the single biggest PER-BEAM cost lever — a linear multiplier on raymarch
  work per covered pixel. The MVR source defaults to 10 and it's usually indistinguishable from
  24; lower still for background beams. Zoom-wide beams auto-reduce steps further (Adaptive
  Steps on the driver). Inside the march, the shadow and haze terms are both re-sampled only
  every other step (they vary slowly along the ray) — so their 3D-texture taps are already halved.
- **Volume ▸ Volume Update Interval** (Volume shadows): rebuild the shadow volume every N frames
  instead of every frame. 2 halves the occlusion-build GPU cost (mesh voxelization + compute);
  the shadow is up to N-1 frames stale, which the Temporal smoothing absorbs for moving dancers.
- **Density** is free to tweak (it's just a brightness scale on the accumulated samples) — it
  does not affect cost, only Raymarch Steps does.
- **Stage Beam Driver ▸ Gpu Instancing** (off by default): batches beams into one
  `DrawMeshInstancedProcedural` per gobo group. It only reduces CPU/draw-call cost, not fill rate,
  so it's roughly parity at low counts and a slight win at high counts — enable it only if you push
  into hundreds of beams and see a CPU/render-thread bottleneck. Volume/Screen/no-shadow only.
- **How many beams**: `StageBeamLoadTest` (used for stress-testing this renderer) defaults to 200
  animated, screen-filling beams at `RaymarchSteps = 24` for a worst-case fill-rate benchmark.
  That's comfortably more than a typical stage lighting rig needs — dozens of beams (not hundreds)
  is a reasonable working budget for real scenes, especially with Half/Quarter resolution and/or
  reduced Raymarch Steps. Measured: ~90 FPS at FullHD (RTX 5060 Ti) with 5 skinned dancers +
  120 shadowed MegaPointe beams.
- Other feature-level costs to be aware of: **Haze Noise** (3D texture sample per raymarch
  sample when enabled) and **Project Onto Surfaces** (an extra full-screen decal pass) are both
  off by default and only cost what you opt into.
- **Anisotropy** adds one normalize + pow per raymarch sample when non-zero; at 0 it compiles to
  the untouched fast path.

### Scaling note (hundreds of fully dynamic beams)

The current architecture draws one additive cone mesh per beam, so the practical ceiling is
transparent overdraw where many wide cones overlap on screen — not the light math itself. Half
Resolution + reduced Raymarch Steps push that ceiling a long way (the load test runs 200
screen-filling beams). If a future target needs hundreds of overlapping beams at full quality,
the known next step is a single froxel-grid compute pass (cluster the cones into a camera-aligned
3D grid, raymarch the grid once, composite once) so cost stops scaling with beam count. That is a
renderer replacement, not a tweak — the `IStageBeamSource`/`StageBeamInstance` contract is
deliberately renderer-neutral so it can slot in without touching any beam sources.

## Two integration levels

- **Drop-in** (`StageBeamLight`): the component covered by this guide. Zero external knowledge —
  every value is a plain Inspector field.
- **Programmatic** (`IStageBeamSource`): implement the interface yourself to feed beams computed
  from another system (e.g. an MVR/DMX rig) into a `StageBeamDriver`. See
  `Runtime/Driver/IStageBeamSource.cs` and `Runtime/Driver/StageBeamInstance.cs`.
