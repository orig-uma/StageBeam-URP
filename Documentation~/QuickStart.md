# Stage Beam — Quick Start

Volumetric stage-light beams for URP. This guide gets a beam on screen in two steps, with no
MVR/DMX/GDTF knowledge required.

## 1. Install

This package lives at `Packages/com.origuma.stage-beam` and has no external dependencies beyond
URP (Unity 6000.0+). If it isn't already referenced by your project's `manifest.json`, add it as
a local/embedded package.

## 2. Add the renderer feature

The beams are drawn by a `ScriptableRendererFeature`, so your URP Renderer asset needs it once,
project-wide:

1. Select your URP Renderer asset (e.g. `Assets/Settings/URP-Renderer.asset`, or whichever
   `ScriptableRendererData` your `UniversalRenderPipelineAsset` points at).
2. In the Inspector, **Add Renderer Feature > Stage Beam Renderer Feature**.

Without this step beams are simulated but nothing is drawn — the `StageBeamLight` inspector will
warn you if it detects this is missing.

## 3. Add a beam

Two equivalent ways:

- **GameObject menu**: `GameObject > Light > Stage Beam Light`.
- **Add Component**: select any GameObject, `Add Component > Stage Beam / Stage Beam Light`.

That's it — a `Stage Beam Driver` GameObject is created automatically the first time a
`StageBeamLight` is enabled. Move/rotate the object to aim the beam; by default the beam exits
along local **-Y** (the fixture convention: local **+Y** is "up"). Set the component's **Beam
Axis** field to **Positive Z** if you'd rather aim it like a Unity spotlight, down
`transform.forward`.

## Parameter guide

| Field | What it does |
|---|---|
| Beam Axis | `NegativeY` = fixture convention, beam exits local -Y. `PositiveZ` = aim like a spotlight, down `transform.forward`. |
| Color | Beam colour. |
| Intensity | Overall brightness multiplier. |
| Range | Maximum throw distance, in metres. |
| Spot Angle | Full field (outer) cone angle in degrees — total angular width of the beam. |
| Beam Angle | Full beam (hotspot) cone angle in degrees — the brighter core; clamped to ≤ Spot Angle. |
| Start Radius | Lens radius in metres — the beam's width at the source. |
| Edge Softness | Softness of the beam's outer edge. |
| Density | Overall volume density (raymarch accumulation strength). |
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

## Performance guide

- **Half-resolution rendering**: the Stage Beam Renderer Feature has a **Half Resolution**
  toggle. It raymarches beams into a quarter-pixel-count off-screen target and upsamples, which
  roughly quarters the raymarch cost at the price of slightly softer edges. Enable it if beams are
  a fill-rate bottleneck.
- **Raymarch Steps** is the single biggest per-beam cost lever — it's a linear multiplier on
  raymarch work per pixel covered by that beam. Lower it for background/distant beams; 24 (the
  default) is a good balance, 12-16 is usually still smooth for less prominent beams.
- **Density** is free to tweak (it's just a brightness scale on the accumulated samples) — it
  does not affect cost, only Raymarch Steps does.
- **How many beams**: `StageBeamLoadTest` (used for stress-testing this renderer) defaults to 200
  animated, screen-filling beams at `RaymarchSteps = 24` for a worst-case fill-rate benchmark.
  That's comfortably more than a typical stage lighting rig needs — dozens of beams (not hundreds)
  is a reasonable working budget for real scenes, especially if some use Half Resolution and/or
  reduced Raymarch Steps.
- Other feature-level costs to be aware of: **Haze Noise** (3D texture sample per raymarch
  sample when enabled) and **Project Onto Surfaces** (an extra full-screen decal pass) are both
  off by default and only cost what you opt into.

## Two integration levels

- **Drop-in** (`StageBeamLight`): the component covered by this guide. Zero external knowledge —
  every value is a plain Inspector field.
- **Programmatic** (`IStageBeamSource`): implement the interface yourself to feed beams computed
  from another system (e.g. an MVR/DMX rig) into a `StageBeamDriver`. See
  `Runtime/Driver/IStageBeamSource.cs` and `Runtime/Driver/StageBeamInstance.cs`.
