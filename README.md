# Origuma Stage Beam

Standalone volumetric stage-light beams for URP. A `ScriptableRendererFeature` raymarches
additive light cones with smooth edges, scene-depth occlusion, gobo projection and optional
half-resolution rendering — no external dependencies beyond URP.

## Features

- Volumetric raymarched cone beams with soft edges, hotspot/field angle control, axial falloff
  and root-glare (source flare).
- Scene-depth occlusion so beams stop at walls/floors instead of shining through geometry.
- Single or dual gobo projection per beam, with independent rotation.
- Optional half-resolution rendering for a big fill-rate win on heavy scenes.
- Optional volumetric haze noise and surface (light pool) projection.
- Drop-in `StageBeamLight` component — add it to any GameObject, no MVR/DMX knowledge needed.
- Generic `IStageBeamSource` pipeline underneath, so data-driven rigs (e.g. MVR/DMX/GDTF) can
  feed the same renderer programmatically.

## Getting started

See [Documentation~/QuickStart.md](Documentation~/QuickStart.md) for installation, renderer
feature setup, adding a beam, the full parameter reference, and a performance guide.

## Two integration levels

- **Drop-in**: add a `Stage Beam Light` component (`GameObject > Light > Stage Beam Light`, or
  `Add Component`). Every parameter is a plain Inspector field with sensible defaults.
- **Programmatic**: implement `IStageBeamSource` to feed beams computed elsewhere (e.g. an
  MVR/DMX rig) into a `StageBeamDriver` — this is how `com.origuma.mvr-toolkit`'s `MvrBeamSource`
  integrates. StageBeam has no dependency in that direction; it only depends on the interface.

## Requirements

- Unity 6000.0+
- Universal Render Pipeline (URP)
