# Origuma Stage Beam

Standalone volumetric stage-light beams for URP. A `ScriptableRendererFeature` raymarches
additive light cones with smooth edges, scene-depth occlusion, gobo projection and optional
half-resolution rendering — no external dependencies beyond URP.

## Features

- Volumetric raymarched cone beams with soft edges, hotspot/field angle control, axial falloff
  and root-glare (source flare).
- Henyey-Greenstein scattering anisotropy — beams pointing at the camera flare up like real haze.
- Scene-depth occlusion so beams stop at walls/floors instead of shining through geometry.
- Optional volumetric shadows: screen-space, or a shared world-space occupancy volume that
  auto-discovers occluders (oriented boxes from renderer bounds; per-bone spheres for skinned
  meshes) — both zero scene setup (one dropdown on the renderer feature), cost independent of
  beam count.
- Single or dual gobo projection per beam, with independent rotation and animation-wheel scroll.
- Optional half-resolution rendering for a big fill-rate win on heavy scenes.
- Optional volumetric haze noise (bundled, auto-loaded) and surface (light pool) projection.
- Drop-in `StageBeamLight` component — add it to any GameObject, no MVR/DMX knowledge needed.
- One-click setup: a `Window > Origuma > Stage Beam Setup` window adds/removes the renderer
  feature on any active URP renderer (also reachable from the `StageBeamLight` inspector); the
  driver and its material are auto-created at runtime.
- Generic `IStageBeamSource` pipeline underneath, so data-driven rigs (e.g. MVR/DMX/GDTF) can
  feed the same renderer programmatically.
- Fully self-contained: shaders/compute/noise ship in the package's `Resources` and load
  automatically, so player builds work with no extra project setup.

## Getting started

See [Documentation~/QuickStart.md](Documentation~/QuickStart.md) for installation, renderer
feature setup, adding a beam, the full parameter reference, and a performance guide.

## Two integration levels

- **Drop-in**: add a `Stage Beam Light` component (`GameObject > Stage Beam > Stage Beam Light`, or
  `Add Component`). Every parameter is a plain Inspector field with sensible defaults.
- **Programmatic**: implement `IStageBeamSource` to feed beams computed elsewhere (e.g. an
  MVR/DMX rig) into a `StageBeamDriver` — this is how `com.origuma.mvr-toolkit`'s `MvrBeamSource`
  integrates. StageBeam has no dependency in that direction; it only depends on the interface.

## Requirements

- Unity 6000.0+
- Universal Render Pipeline (URP)

## Package stack (Origuma lighting suite)

This package is the bottom rendering layer of a four-package suite, and is fully usable on
its own. Each package depends only on the layer directly below it (declared in its
`package.json`):

```
com.origuma.show-control    cues / effects / programmer — rewrites fixture state
        │ depends on
com.origuma.mvr-toolkit     MVR/GDTF rig, DMX → fixture state, MvrBeamSource
        │ depends on          ┆ optional bridge (auto-enabled when installed)
        │                     └──► com.origuma.dmx-toolkit    Art-Net / sACN I/O
com.origuma.stage-beam      URP volumetric beam renderer  ← this package
        │ depends on
Universal Render Pipeline
```

Every level renders through the same `StageBeamRendererFeature` + `StageBeamDriver`; only
the **beam source** (`IStageBeamSource`) differs:

- **Standalone** — hand-placed `StageBeamLight` components, one per beam (this package).
- **MVR rig** — one `MvrBeamSource` (mvr-toolkit) feeds every fixture's beam from DMX/GDTF.
- **Show control** — adds no render components at all; it rewrites `MvrFixtureState`
  upstream and the same `MvrBeamSource` renders the result.

Naming note: MVR Toolkit's `StageBeamLightSync` is unrelated to `StageBeamLight` — it pools
real Unity spot lights for surface illumination, independent of this volumetric renderer.
