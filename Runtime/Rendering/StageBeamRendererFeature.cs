using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Origuma.StageBeam
{
    public static class StageBeamQueue
    {
        public struct Item
        {
            public Matrix4x4 Matrix;
            public MaterialPropertyBlock Props;
            public Light ShadowLight;   // real light whose URP shadow map shadows this beam
            public Vector3 BoundsCenter; // world-space bounding sphere (for frustum/area culling)
            public float   BoundsRadius;
            // GPU-instanced path only (filled when Instancing is on): the packed per-beam record
            // and its gobo arrays (the batch key). The legacy DrawMesh path ignores these.
            public GpuBeam Raw;
            public Texture2DArray GoboA;
            public Texture2DArray GoboB;
        }

        public static Mesh Mesh;
        public static Material Material;
        public static readonly List<Item> Items = new List<Item>(128);

        /// <summary>Driver-published: the beam data is also packed as <see cref="GpuBeam"/> records
        /// (Item.Raw/GoboA/GoboB) so the feature can draw via GPU instancing instead of per-beam
        /// DrawMesh. Off = only the MPB path is populated.</summary>
        public static bool Instancing;

        /// <summary>Soft Additive (published by the driver): beams accumulate raw HDR into an
        /// offscreen buffer and the composite saturates the TOTAL toward
        /// <see cref="SoftCeiling"/> — stacked beams approach a chosen thinness instead of
        /// piling up to white, so whatever stands inside them stays readable.</summary>
        public static bool SoftComposite;
        public static float SoftCeiling = 0.5f;

        public static void Begin() => Items.Clear();
        public static void Add(Matrix4x4 m, MaterialPropertyBlock props, Light shadowLight = null,
            Vector3 boundsCenter = default, float boundsRadius = 0f,
            GpuBeam raw = default, Texture2DArray goboA = null, Texture2DArray goboB = null) =>
            Items.Add(new Item
            {
                Matrix = m, Props = props, ShadowLight = shadowLight,
                BoundsCenter = boundsCenter, BoundsRadius = boundsRadius,
                Raw = raw, GoboA = goboA, GoboB = goboB,
            });
    }

    public sealed class StageBeamRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private RenderPassEvent _event = RenderPassEvent.AfterRenderingTransparents;

        [Tooltip("Render beams into a downscaled target and upsample — the biggest fill-rate lever " +
                 "when beams cover the screen. Raymarch pixels: Half = 1/4, Third = 1/9, " +
                 "Quarter = 1/16. Third sits between Half and Quarter: reach for it when Quarter " +
                 "bands/moires at high output resolutions but Half costs too much. Beams soften " +
                 "slightly (depth-aware upsample keeps object edges clean).")]
        [SerializeField] private ResolutionScale _resolutionScale = ResolutionScale.Quarter;

        [Tooltip("Depth-aware smoothing of the beam buffer before compositing. Averages away " +
                 "the raymarch/shadow jitter grain — most visible in shadowed regions at Half/" +
                 "Quarter resolution — at the cost of a slightly softer fog. Near-free.")]
        [SerializeField] private bool _noiseSmoothing = true;

        [Tooltip("Anti-banding dither applied when the beam is composited into the camera target, " +
                 "as a fraction of each pixel's value. Beams are wide, smooth, low-slope ramps — " +
                 "exactly what quantizes into Mach bands in the camera's low-mantissa HDR format " +
                 "(B10G11R11, 32-bit HDR). Raise until the contours break up; lower if it reads as " +
                 "grain. 0 = off (and free — the branch is skipped).")]
        [Range(0f, 0.5f)] [SerializeField] private float _dither = 0.15f;

        [Tooltip("On: the raymarch dither DRIFTS with the haze (screen-space, matched to the " +
                 "haze scroll) so the grain reads as the fog moving rather than a separate " +
                 "crawling noise, and still decorrelates per frame for TAA to fully resolve " +
                 "(TAA recommended). Off: a STATIC dither — no motion, but low step counts leave " +
                 "a faint screen-fixed grain (raise Raymarch Steps to hide it).")]
        [SerializeField] private bool _temporalJitter = true;

        [Header("Culling (fill-rate)")]
        [Tooltip("Skip beams whose bounding sphere is entirely outside the camera frustum — " +
                 "they cost nothing when off-screen. Free win; leave on.")]
        [SerializeField] private bool _frustumCull = true;
        [Tooltip("Skip beams whose on-screen footprint is smaller than this many pixels " +
                 "(radius). 0 = draw everything. A few pixels culls distant/tiny beams whose " +
                 "contribution isn't visible anyway.")]
        [Range(0f, 32f)] [SerializeField] private float _minScreenRadiusPx = 0f;

        // Ordered coarsest-to-finest as it reads in the dropdown. NOTE: these serialize by index,
        // so this ordering is not append-safe — any asset previously saved as Quarter (2) now loads
        // as Third. Re-check the Resolution Scale on existing Renderer Feature assets.
        public enum ResolutionScale { Full, Half, Third, Quarter }

        // Back-compat: the old bool field is migrated to the enum on first load (see OnEnable).
        [SerializeField, HideInInspector] private bool _halfResolution = false;
        [SerializeField, HideInInspector] private bool _resolutionMigrated = false;

        [Tooltip("ONE dial for \"beams vs scene\": multiplies every beam's volumetric brightness " +
                 "(and the projected pools) without touching per-fixture intensities. The low " +
                 "default keeps performers/characters readable inside beams; raise for a " +
                 "heavier haze look.")]
        [Range(0f, 2f)] [SerializeField] private float _masterIntensity = 0.05f;

        [Header("Haze Noise (optional)")]
        [Tooltip("Tileable 3D noise that modulates beam density to look like drifting atmosphere. " +
                 "Leave empty to auto-use the noise bundled with the package when Strength > 0.")]
        [SerializeField] private Texture3D _hazeNoise;
        private Texture3D _hazeNoiseFallback;
        private bool _hazeNoiseLoadTried;
        [Tooltip("Haze turbulence amount. 0 = off (uniform beam).")]
        [Range(0f, 1f)] [SerializeField] private float _hazeStrength = 0f;
        [Tooltip("World units → noise size. Smaller = larger, softer blobs.")]
        [SerializeField] private float _hazeScale = 0.3f;
        [Tooltip("Drift velocity of the haze in world units per second.")]
        [SerializeField] private Vector3 _hazeScrollSpeed = new Vector3(0.01f, 0.04f, 0f);

        [Header("Surface Projection (light pools / gobo)")]
        [Tooltip("Project each beam's gobo × colour onto the surfaces it hits, as a decal.")]
        [SerializeField] private bool _projectOntoSurfaces = false;
        [Tooltip("Brightness of the projected pool.")]
        [SerializeField] private float _projectionSurfaceBoost = 1f;
        [Tooltip("Surfaces steeper than this (cosine of angle to the beam) fade out. " +
                 "Lower = only near-perpendicular surfaces catch light.")]
        [Range(0f, 1f)] [SerializeField] private float _projectionNormalCull = 0.25f;
        [Tooltip("Only surfaces on these layers receive the projected pool/gobo — exclude a " +
                 "character's layer so beams don't paint light onto performers. Everything = " +
                 "no filtering (and no extra cost).")]
        [SerializeField] private LayerMask _projectionReceiverLayers = ~0;
        [Tooltip("Extra hardness on the FLOOR POOL's shadow only. 0 (default) = the pool's " +
                 "shadow matches the volumetric beam's soft residual, so the two stay consistent " +
                 "(the light reaching the floor equals the light left in the beam above it). " +
                 "Raise it for a crisper floor shadow, but note it then reads DARKER than the " +
                 "faint beam residual above — a stylistic choice, not physical. To deepen the " +
                 "occlusion of BOTH the beam and the pool together, raise Volume ▸ Density instead.")]
        [Range(0f, 0.9f)] [SerializeField] private float _projectionShadowHardness = 0.1f;

        [Header("Volumetric Shadows")]
        [Tooltip("Off = none. ScreenSpace = free, on-screen occluders only (camera-dependent). " +
                 "Volume = approximate world-space occupancy, zero scene setup. " +
                 "LightShadowMap = HIGHEST quality: beams with a real (URP) spot light sample " +
                 "that light's shadow map — geometry-exact silhouettes (limbs, cloth, props), " +
                 "no sphere/voxel approximation. Needs the fixture light's Shadows enabled and " +
                 "URP additional-light shadows on.")]
        [SerializeField] private ShadowMode _shadows = ShadowMode.Off;

        [Header("Volume Shadows (automatic)")]
        [Tooltip("Layers whose renderers cast volumetric shadows (performers, set, props).")]
        [SerializeField] private LayerMask _occluderMask = ~0;
        [Tooltip("Voxelize the occluders' REAL meshes (skinned poses and cloth included) " +
                 "instead of sphere/box approximations — geometry-true silhouettes at voxel " +
                 "resolution, cost still independent of the light count.")]
        [SerializeField] private bool _volMeshVoxelize = true;
        [Tooltip("Mesh-voxelization orthographic passes (X/Y/Z). 3 = fully conservative. 2 drops " +
                 "the top-down pass — the biggest CPU cut to the occlusion build (recorded draw " +
                 "count scales with this) and usually enough for upright performers. Lower if the " +
                 "build's CPU cost matters more than catching perfectly horizontal surfaces.")]
        [Range(1, 3)] [SerializeField] private int _volVoxelizeAxes = 3;
        [Tooltip("Voxelize skinned occluders by COMPUTE instead of rasterization: same geometry, " +
                 "same shadow, zero draw calls (the raster path costs one draw + SetPass per " +
                 "renderer per axis — a cast of performers is hundreds per build). Needs GPU " +
                 "skinning; renderers whose skinned buffer is unavailable fall back to raster.")]
        [SerializeField] private bool _volComputeSkinned = true;
        [Tooltip("Static/dynamic occluder split (perf). Voxelizes non-moving rigid occluders once " +
                 "into a cached volume and re-voxelizes only dynamic ones (skinned performers + " +
                 "anything that moved) each build — killing the per-build draw spike from static " +
                 "set geometry. Fully automatic (no layers). MeshVoxelize only.")]
        [SerializeField] private bool _volStaticDynamicSplit;
        [Tooltip("0 = no shadow, 1 = fully black in occluded regions.")]
        [Range(0f, 1f)] [SerializeField] private float _volStrength = 1f;
        [Tooltip("Occluder opacity per metre along the shadow ray (Beer-Lambert extinction). " +
                 "8 makes a ~0.5 m body block ~98%; lower for softer, gauzier shadows.")]
        [Range(1f, 16f)] [SerializeField] private float _volDensity = 8f;
        [Tooltip("Samples along the part of the shadow ray that crosses the occlusion volume. " +
                 "Raise only if thin occluders shimmer or band.")]
        [Range(1, 48)] [SerializeField] private int _volSteps = 16;
        [SerializeField] private float _volMaxDistance = 25f;
        [Tooltip("Temporal smoothing of the shadow volume (0 = off/instant, 1 = heavy). Absorbs " +
                 "the frame-to-frame voxel chatter a MOVING occluder (a dancer) causes, so shadow " +
                 "edges glide instead of flickering. Higher = smoother but the shadow lags moving " +
                 "occluders more. Near-free (one compute pass).")]
        [Range(0f, 0.95f)] [SerializeField] private float _volTemporal = 0.6f;
        [Tooltip("Rebuild the shadow volume only every N frames (1 = every frame). 2 halves the " +
                 "occlusion-build GPU cost; the shadow is up to N-1 frames stale, which the " +
                 "Temporal smoothing above already absorbs for moving occluders. Raise for a " +
                 "cheaper build when shadow latency doesn't matter.")]
        [Range(1, 4)] [SerializeField] private int _volUpdateInterval = 1;

        [Header("Screen-Space Shadows")]
        [Tooltip("Screen-space mode only.")]
        [SerializeField] private float _ssStrength = 0.85f;
        [Tooltip("Near-range march steps toward the light. Covers Steps × Step Size metres of the " +
                 "near range where the occluder actually is; raise for longer/softer shadow shafts.")]
        [SerializeField] private int   _ssSteps = 16;
        [Tooltip("World size of each near-range step (metres). Small = catches thin occluders " +
                 "(dancers, limbs); the fixture can be far away without making steps coarse.")]
        [SerializeField] private float _ssStep = 0.2f;
        [SerializeField] private float _ssMaxDist = 25f;
        [SerializeField] private float _ssBias = 0.05f;
        [Tooltip("Assumed occluder thickness (metres): surfaces the ray passes further behind than " +
                 "this are treated as background and don't shadow. Raise for thick set pieces.")]
        [SerializeField] private float _ssThickness = 1.5f;
        [Tooltip("Debug (Volume mode): tint the beam RED where the occupancy volume has data. " +
                 "No red = volume not bound/empty or the box doesn't cover the beam.")]
        [SerializeField] private bool _shadowDebug = false;

        public enum ShadowMode { Off, ScreenSpace, Volume, LightShadowMap }

        private StageBeamPass _pass;
        private Material _upsampleMat;
        private Material _projectionMat;
        private StageBeamOcclusionBuilder _occlusionBuilder;
        private int _occlusionBuiltFrame = -1;
        private static bool _warnedInstancingBypass;   // LightShadowMap + instancing warn-once
        private bool _warnedOverride;                   // scene OcclusionVolume overriding the feature

        private static readonly int IdBeamRTParams      = Shader.PropertyToID("_BeamRTParams");
        private static readonly int IdUpsampleTexelSize = Shader.PropertyToID("_BeamUpsampleTexelSize");
        private static readonly int IdBeamSoftCeiling   = Shader.PropertyToID("_BeamSoftCeiling");
        private static readonly int IdBeamDither        = Shader.PropertyToID("_BeamDither");
        private static readonly int IdTemporalJitter    = Shader.PropertyToID("_BeamTemporalJitter");
        private static readonly int IdJitterScroll      = Shader.PropertyToID("_BeamJitterScroll");
        private static readonly int IdHazeNoise         = Shader.PropertyToID("_HazeNoise");
        private static readonly int IdHazeStrength      = Shader.PropertyToID("_HazeStrength");
        private static readonly int IdHazeScale         = Shader.PropertyToID("_HazeScale");
        private static readonly int IdHazeScroll        = Shader.PropertyToID("_HazeScroll");
        private static readonly int IdProjSurfaceBoost  = Shader.PropertyToID("_ProjSurfaceBoost");
        private static readonly int IdProjNormalCull    = Shader.PropertyToID("_ProjNormalCull");
        private static readonly int IdProjShadowHardness = Shader.PropertyToID("_ProjShadowHardness");
        private static readonly int IdProjReceiverMask  = Shader.PropertyToID("_ProjReceiverMaskOn");
        private static readonly int IdProjReceiverDepth = Shader.PropertyToID("_StageBeamReceiverDepth");
        private static readonly int IdMasterIntensity   = Shader.PropertyToID("_StageBeamMaster");

        public override void Create()
        {
            // Migrate the retired _halfResolution bool to the resolution enum, once.
            if (!_resolutionMigrated)
            {
                if (_halfResolution) _resolutionScale = ResolutionScale.Half;
                _resolutionMigrated = true;
            }
            _pass = new StageBeamPass { renderPassEvent = _event };
            _upsampleMat   = CoreUtils.CreateEngineMaterial("Origuma/StageBeamUpsample");
            _projectionMat = CoreUtils.CreateEngineMaterial("Origuma/StageBeamProjection");
        }

        private int ResolutionDivisor => _resolutionScale switch
        {
            ResolutionScale.Half => 2,
            ResolutionScale.Third => 3,
            ResolutionScale.Quarter => 4,
            _ => 1,
        };

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_upsampleMat);
            CoreUtils.Destroy(_projectionMat);
            _occlusionBuilder?.Release();
            _occlusionBuilder = null;
            _pass?.DisposeBatcher();
        }

        private static readonly int IdShadowStrength = Shader.PropertyToID("_BeamShadowStrength");
        private static readonly int IdShadowSteps    = Shader.PropertyToID("_BeamShadowSteps");
        private static readonly int IdShadowMaxDist  = Shader.PropertyToID("_BeamShadowMaxDist");
        private static readonly int IdShadowBias     = Shader.PropertyToID("_BeamShadowBias");
        private static readonly int IdShadowThick    = Shader.PropertyToID("_BeamShadowThickness");
        private static readonly int IdShadowStep     = Shader.PropertyToID("_BeamShadowStep");
        private static readonly int IdShadowDebug    = Shader.PropertyToID("_BeamShadowDebug");
        private const string KwScreen = "_STAGEBEAM_SHADOWS_SCREEN";
        private const string KwVolume = "_STAGEBEAM_SHADOWS_VOLUME";
        private const string KwLight  = "_STAGEBEAM_SHADOWS_LIGHT";
        private static readonly int IdShadowLightIndex = Shader.PropertyToID("_BeamShadowLightIndex");
        // GPU-instanced beam path.
        private static readonly int IdStageBeams    = Shader.PropertyToID("_StageBeams");
        private static readonly int IdStageBeamBase = Shader.PropertyToID("_StageBeamBase");
        private static readonly int IdGoboArray     = Shader.PropertyToID("_GoboArray");
        private static readonly int IdGoboArray2    = Shader.PropertyToID("_GoboArray2");

        private void ApplyShadowMode()
        {
            Shader.SetGlobalFloat(IdShadowDebug, _shadowDebug ? 1f : 0f);
            Shader.DisableKeyword(KwScreen);
            Shader.DisableKeyword(KwVolume);
            Shader.DisableKeyword(KwLight);
            if (_shadows == ShadowMode.ScreenSpace)
            {
                Shader.EnableKeyword(KwScreen);
                Shader.SetGlobalFloat(IdShadowStrength, _ssStrength);
                Shader.SetGlobalFloat(IdShadowSteps,    _ssSteps);
                Shader.SetGlobalFloat(IdShadowStep,     _ssStep);
                Shader.SetGlobalFloat(IdShadowMaxDist,  _ssMaxDist);
                Shader.SetGlobalFloat(IdShadowBias,     _ssBias);
                Shader.SetGlobalFloat(IdShadowThick,    _ssThickness);
            }
            else if (_shadows == ShadowMode.Volume)
            {
                Shader.EnableKeyword(KwVolume);
                // Volume globals are bound by the occlusion-build render-graph pass (see
                // StageBeamPass), fed by PrepareOcclusion — feature builder or override.
            }
            else if (_shadows == ShadowMode.LightShadowMap)
            {
                Shader.EnableKeyword(KwLight);
                Shader.SetGlobalFloat(IdShadowStrength, _volStrength);
            }
        }

        // LightShadowMap mode: resolve each beam's real Light to URP's "additional light index"
        // (its position among the visible lights, skipping the main light) so the cone shader
        // can call AdditionalLightRealtimeShadow with it. Beams without a visible shadow light
        // get -1 and render unshadowed.
        private static void AssignShadowLightIndices(ref RenderingData renderingData)
        {
            var visible = renderingData.cullResults.visibleLights;
            int mainIndex = renderingData.lightData.mainLightIndex;
            var items = StageBeamQueue.Items;

            for (int i = 0; i < items.Count; i++)
            {
                int idx = -1;
                var light = items[i].ShadowLight;
                if (light != null && light.isActiveAndEnabled &&
                    light.shadows != LightShadows.None)
                {
                    for (int v = 0; v < visible.Length; v++)
                    {
                        if (visible[v].light != light) continue;
                        if (v == mainIndex) break;              // main light: not "additional"
                        idx = v < mainIndex || mainIndex < 0 ? v : v - 1;
                        break;
                    }
                }
                items[i].Props.SetFloat(IdShadowLightIndex, idx);
            }
        }

        // Runs the CPU half of the occlusion build (discovery/box fit) and returns the builder
        // whose GPU half must be RECORDED into the render graph right before the beams draw —
        // the only execution point that's correct for every camera (immediate execution from
        // update code happened to work for the Scene view but left the Game view unbuilt).
        // Null = nothing to record for this camera. LightShadowMap is a HYBRID: beams without
        // a shadow-mapped light march the shared volume, so it needs the volume too.
        private StageBeamOcclusionBuilder PrepareOcclusion()
        {
            bool volumeMode = _shadows == ShadowMode.Volume || _shadows == ShadowMode.LightShadowMap;
            var overrideVol = StageBeamOcclusionVolume.Active;

            if (!volumeMode)
            {
                if (_occlusionBuilder != null)
                {
                    _occlusionBuilder.Release(neutraliseGlobals: overrideVol == null);
                    _occlusionBuilder = null;
                }
                return null;
            }

#if UNITY_EDITOR
            // THE reason the Frame Debugger never shows this build — and why Set Pass Calls
            // drops in the Statistics panel the moment the debugger pauses the game: while the
            // editor is paused, this returns null and the build is skipped ON PURPOSE.
            //
            // The GPU half executes through Graphics.ExecuteCommandBuffer outside the render
            // graph, so during a pause it would be re-submitted on every editor repaint — work
            // the Frame Debugger cannot attribute to any pass and which destabilises its
            // capture. Skipping keeps the last-bound volume + globals live (the shadow holds its
            // final state, so the paused image still looks right) and the capture stable, at the
            // price of the build being invisible in it.
            //
            // Moving the build into the render graph is the real fix; two attempts failed and
            // were reverted (see the note at the BuildAndBind call site). Until then,
            // Window > Origuma > Stage Beam > Log Occlusion Build Cost — run while PLAYING —
            // is the accounting for what this skipped work costs.
            if (UnityEditor.EditorApplication.isPaused) return null;
#endif

            // Rebuild at most once per Volume Update Interval frames in play mode (every camera
            // in edit mode — frameCount stalls there). On skipped frames we return null WITHOUT
            // releasing/neutralising, so last build's bound volume + globals stay live: the
            // shadow is up to (interval-1) frames stale, which the temporal smoothing already
            // absorbs. Interval 1 = every frame; 2 = half the occlusion-build GPU cost.
            //
            // `elapsed >= 0` guards against Time.frameCount RESETTING below the stored build
            // frame (it resets on Play enter while this feature instance — and its
            // _occlusionBuiltFrame — persist across sessions): a negative elapsed must NOT be
            // read as "within the interval" or the volume would never rebuild. The equality
            // half also dedupes multiple cameras in the same frame — a visible Scene view
            // renders the same frameCount and must not trigger a second build.
            if (Application.isPlaying)
            {
                int elapsed = Time.frameCount - _occlusionBuiltFrame;
                if (elapsed >= 0 && elapsed < Mathf.Max(1, _volUpdateInterval)) return null;
                _occlusionBuiltFrame = Time.frameCount;
            }

            // An override component owns the build (its settings replace the feature's). It
            // returns its configured builder; BuildAndBind (called by AddRenderPasses) then
            // runs the CPU + GPU build.
            if (overrideVol != null)
            {
                if (_occlusionBuilder != null) { _occlusionBuilder.Release(false); _occlusionBuilder = null; }
                var ob = overrideVol.ConfigureBuilder();   // component pins the box + advanced knobs
                if (overrideVol.Mode == StageBeamOcclusionVolume.SettingsMode.UseRendererFeatureSettings)
                {
                    // Default: the main shadow behaviour still comes from the feature, so editing
                    // the feature keeps working even with an override present (it only pins the box).
                    ApplyFeatureOcclusionSettings(ob);
                    _warnedOverride = false;
                }
                else if (!_warnedOverride)
                {
                    _warnedOverride = true;
                    // Info, not a warning — this is an intentional mode, so don't look like an error.
                    Debug.Log("<color=#5aa9e6>[StageBeam]</color> A StageBeamOcclusionVolume is in " +
                        "Override mode — it owns the occlusion settings and the Renderer Feature's " +
                        "occlusion fields are ignored. Set it to \"Use Renderer Feature Settings\" to " +
                        "drive occlusion from the feature.", overrideVol);
                }
                return ob;
            }

            _warnedOverride = false;   // no override active → re-arm the override-mode notice

            _occlusionBuilder ??= new StageBeamOcclusionBuilder();
            var b = _occlusionBuilder;
            ApplyFeatureOcclusionSettings(b);
            return b;
        }

        // The feature's occlusion behaviour, applied to a builder. Shared by the auto path and by
        // an override component running in "Use Renderer Feature Settings" mode (which pins only the
        // box, then lets these drive the shadow behaviour).
        private void ApplyFeatureOcclusionSettings(StageBeamOcclusionBuilder b)
        {
            b.OccluderMask           = _occluderMask;
            b.MeshVoxelize           = _volMeshVoxelize;
            b.VoxelizeAxisCount      = _volVoxelizeAxes;
            b.ComputeSkinnedVoxelize = _volComputeSkinned;
            b.StaticDynamicSplit     = _volStaticDynamicSplit;
            b.Strength           = _volStrength;
            b.ShadowDensity      = _volDensity;
            b.ShadowSteps        = _volSteps;
            b.MaxShadowDistance  = _volMaxDistance;
            b.TemporalSmoothing  = _volTemporal;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            ApplyShadowMode();
            // KNOWN LIMITATION: this executes via Graphics.ExecuteCommandBuffer, which bypasses the
            // SRP render loop — so the Frame Debugger has nowhere to attribute the build's hundreds
            // of SetPass calls, and anyone investigating a high SetPass count finds nothing that
            // explains them. Its home is a RenderGraph pass; two attempts at moving it there failed
            // (the first aliased the voxelize temp RT onto the beam buffer and corrupted the image,
            // the second rendered correctly only while paused and washed out during play) and both
            // were reverted rather than left half-working. Use
            // Window > Origuma > Stage Beam > Log Occlusion Build Cost to read the cost meanwhile.
            PrepareOcclusion()?.BuildAndBind();
            Shader.SetGlobalFloat(IdMasterIntensity, _masterIntensity);
            Shader.SetGlobalFloat(IdTemporalJitter, _temporalJitter ? 1f : 0f);

            // Screen-space velocity (pixels/sec) the raymarch dither drifts at, matched to the
            // haze scroll so the animated grain reads as the fog moving rather than a separate
            // noise crawl. The haze PATTERN drifts in world at -scrollSpeed/scale (a fixed haze
            // feature sits where uvw = pWS·scale + scroll); project that to the camera's screen
            // axes at a representative fog depth. Approximate on purpose — coherent DIRECTION
            // with the haze is what sells it, not an exact speed match.
            Vector2 jitterScroll = Vector2.zero;
            var jcam = renderingData.cameraData.camera;
            if (_temporalJitter && jcam != null && _hazeStrength > 0f)
            {
                Vector3 hazeWorldVel = -_hazeScrollSpeed / Mathf.Max(_hazeScale, 1e-4f);
                Vector3 viewVel = jcam.worldToCameraMatrix.MultiplyVector(hazeWorldVel);
                const float repDepth = 8f;   // representative fog distance
                float pxPerUnit = jcam.pixelHeight * 0.5f /
                    Mathf.Max(Mathf.Tan(jcam.fieldOfView * 0.5f * Mathf.Deg2Rad) * repDepth, 1e-3f);
                // View +Y is up; screen pixel Y grows downward → flip Y.
                jitterScroll = new Vector2(viewVel.x, -viewVel.y) * pxPerUnit;
            }
            else if (_temporalJitter)
            {
                // No haze: fall back to a gentle fixed upward drift so the grain still glides
                // smoothly instead of the old chaotic per-frame jump.
                jitterScroll = new Vector2(0f, -18f);
            }
            Shader.SetGlobalVector(IdJitterScroll, new Vector4(jitterScroll.x, jitterScroll.y, 0f, 0f));

            if (StageBeamQueue.Mesh == null || StageBeamQueue.Material == null) return;
            if (StageBeamQueue.Items.Count == 0) return;

            if (_shadows == ShadowMode.LightShadowMap)
                AssignShadowLightIndices(ref renderingData);

            // Publish the (shared) haze noise as global shader state for this frame.
            // No texture assigned but strength raised: fall back to the bundled noise.
            if (_hazeNoise == null && _hazeStrength > 0f && !_hazeNoiseLoadTried)
            {
                _hazeNoiseLoadTried = true;
                _hazeNoiseFallback = Resources.Load<Texture3D>("HazeFBM3D");
            }
            var hazeTex = _hazeNoise != null ? _hazeNoise : _hazeNoiseFallback;
            var haze = hazeTex != null ? _hazeStrength : 0f;
            Shader.SetGlobalFloat(IdHazeStrength, haze);
            if (haze > 0f)
            {
                Shader.SetGlobalTexture(IdHazeNoise, hazeTex);
                Shader.SetGlobalFloat(IdHazeScale, _hazeScale);
                var off = _hazeScrollSpeed * Time.time;
                Shader.SetGlobalVector(IdHazeScroll, new Vector4(off.x, off.y, off.z, 0f));
            }

            _pass.ResolutionDivisor   = _upsampleMat != null ? ResolutionDivisor : 1;
            _pass.NoiseSmoothing      = _noiseSmoothing;
            _pass.Dither              = _dither;
            _pass.UpsampleMat         = _upsampleMat;
            _pass.ProjectOntoSurfaces = _projectOntoSurfaces && _projectionMat != null;
            _pass.ProjectionMat       = _projectionMat;
            _pass.ProjectionBoost     = _projectionSurfaceBoost * _masterIntensity;
            _pass.ProjectionReceivers = _projectionReceiverLayers;
            _pass.ProjectionNormalCull = _projectionNormalCull;
            _pass.ProjectionShadowHardness = _projectionShadowHardness;

            // GPU instancing carries no per-beam light index (it's a single global), so it can't
            // drive LightShadowMap correctly — bypass to the per-beam DrawMesh path there. Warn
            // once so a surprising perf/behaviour change isn't silent.
            _pass.AllowInstancing = _shadows != ShadowMode.LightShadowMap;
            if (StageBeamQueue.Instancing && !_pass.AllowInstancing && !_warnedInstancingBypass)
            {
                _warnedInstancingBypass = true;
                Debug.LogWarning("[StageBeam] GPU Instancing is not supported with the " +
                    "LightShadowMap shadow mode (a beam's shadow-map light index can't be " +
                    "per-instance) — falling back to the per-beam draw path. Switch Shadows to " +
                    "Volume or ScreenSpace to use instancing.");
            }

            // Per-camera cull data: frustum planes + a screen-size factor so the draw loop can
            // skip off-screen and sub-pixel beams. The planes come from the camera this pass
            // renders (AddRenderPasses runs per camera).
            var cam = renderingData.cameraData.camera;
            _pass.CullBeams = _frustumCull || _minScreenRadiusPx > 0f;
            if (_pass.CullBeams && cam != null)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _pass.FrustumPlanes);
                _pass.DoFrustumCull = _frustumCull;
                _pass.CamPosition = cam.transform.position;
                _pass.MinScreenRadiusPx = _minScreenRadiusPx;
                // pixels-per-world-unit at distance d ≈ (pixelHeight / 2) / (d·tan(fov/2)).
                _pass.ScreenRadiusFactor = _minScreenRadiusPx > 0f
                    ? (cam.pixelHeight * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                    : 0f;
                _pass.CamOrthographic = cam.orthographic;
                _pass.OrthoPixelsPerUnit = cam.orthographic
                    ? cam.pixelHeight * 0.5f / Mathf.Max(cam.orthographicSize, 1e-3f) : 0f;
            }
            // Build the GPU-instance batches now (CPU, cull data is set) so the buffer upload
            // happens before the graph executes — not inside a render func (avoids a stall) — and
            // both the beam and projection instanced draws consume the same batches.
            _pass.BuildInstanceBatches();
            renderer.EnqueuePass(_pass);
        }

        private sealed class StageBeamPass : ScriptableRenderPass
        {
            public float     Dither = 0.02f;          // anti-banding at the composite write
            public int       ResolutionDivisor = 1;   // 1 = full, 2 = half, 3 = third, 4 = quarter
            public bool      NoiseSmoothing = true;
            public bool      AllowInstancing = true;   // false in LightShadowMap mode (see feature)
            public Material  UpsampleMat;
            public bool      ProjectOntoSurfaces;
            public Material  ProjectionMat;
            public float     ProjectionBoost;
            public float     ProjectionNormalCull;
            public float     ProjectionShadowHardness;
            public LayerMask ProjectionReceivers = ~0;

            // Per-camera culling (set from AddRenderPasses).
            public bool         CullBeams;
            public bool         DoFrustumCull;
            public readonly Plane[] FrustumPlanes = new Plane[6];
            public Vector3      CamPosition;
            public float        MinScreenRadiusPx;
            public float        ScreenRadiusFactor;   // persp: px per world unit at unit distance
            public bool         CamOrthographic;
            public float        OrthoPixelsPerUnit;

            // Returns true if the beam should be DRAWN (survives culling).
            private bool Visible(in StageBeamQueue.Item it)
            {
                if (!CullBeams || it.BoundsRadius <= 0f) return true;

                if (DoFrustumCull)
                {
                    for (int p = 0; p < 6; p++)
                        if (FrustumPlanes[p].GetDistanceToPoint(it.BoundsCenter) < -it.BoundsRadius)
                            return false;   // sphere entirely outside this plane → off-screen
                }

                if (MinScreenRadiusPx > 0f)
                {
                    float screenPx = CamOrthographic
                        ? it.BoundsRadius * OrthoPixelsPerUnit
                        : it.BoundsRadius * ScreenRadiusFactor /
                          Mathf.Max((it.BoundsCenter - CamPosition).magnitude, 1e-3f);
                    if (screenPx < MinScreenRadiusPx) return false;
                }
                return true;
            }

            private class ConeData { public int W, H; }
            private class BlitData
            {
                public TextureHandle Source;
                public Material Mat;
                public int W, H;
                public float SoftCeiling;
                public float Dither;
            }
            private class ProjData
            {
                public Material Mat;
                public float Boost, NormalCull, ShadowHardness;
                public bool MaskOn;
                public TextureHandle ReceiverDepth;
            }
            private class RecvData { public RendererListHandle List; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var camera    = frameData.Get<UniversalCameraData>();
                if (!resources.activeColorTexture.IsValid()) return;

                // Soft Additive needs the composite to see the SUMMED beam total (the ceiling
                // curve can't be expressed per-beam), so it always routes through the offscreen
                // buffer — at full resolution unless the resolution scale asked for less.
                int divisor = ResolutionDivisor;
                if (divisor > 1 || StageBeamQueue.SoftComposite)
                    RecordOffscreen(renderGraph, resources, camera, divisor);
                else
                    RecordFullRes(renderGraph, resources, camera);

                if (ProjectOntoSurfaces)
                    RecordProjection(renderGraph, frameData, resources, camera);
            }

            // Screen-space decal projection of each beam's gobo × colour onto opaque surfaces.
            // With a receiver layer mask set, a depth-only pre-pass of ONLY the receiver layers
            // lets the projection reject pixels whose visible surface is a non-receiver (e.g. a
            // character standing in the pool).
            private void RecordProjection(RenderGraph renderGraph, ContextContainer frameData,
                UniversalResourceData resources, UniversalCameraData camera)
            {
                if (!resources.cameraDepthTexture.IsValid()) return; // needs scene depth

                bool maskOn = ProjectionReceivers.value != ~0 && ProjectionReceivers.value != 0;
                TextureHandle receiverDepth = default;
                if (maskOn)
                {
                    var depthDesc = camera.cameraTargetDescriptor;
                    depthDesc.colorFormat = RenderTextureFormat.Depth;
                    depthDesc.depthBufferBits = 32;
                    depthDesc.msaaSamples = 1;
                    receiverDepth = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph, depthDesc, "StageBeamReceiverDepth", true);

                    var renderingData = frameData.Get<UniversalRenderingData>();
                    var listDesc = new RendererListDesc(new ShaderTagId("DepthOnly"),
                        renderingData.cullResults, camera.camera)
                    {
                        sortingCriteria = SortingCriteria.CommonOpaque,
                        renderQueueRange = RenderQueueRange.opaque,
                        layerMask = ProjectionReceivers,
                    };

                    using var rb = renderGraph.AddRasterRenderPass<RecvData>(
                        "Stage Beam Projection Receivers", out var rd);
                    rd.List = renderGraph.CreateRendererList(listDesc);
                    rb.UseRendererList(rd.List);
                    rb.SetRenderAttachmentDepth(receiverDepth, AccessFlags.Write);
                    rb.AllowPassCulling(false);
                    rb.SetRenderFunc((RecvData d, RasterGraphContext ctx) =>
                        ctx.cmd.DrawRendererList(d.List));
                }

                // Additive mode: draw pools straight into the camera (unbounded HDR, as chosen).
                // Soft Additive: accumulate pools RAW into an offscreen buffer, then composite
                // the SUMMED total through the ceiling curve — so overlapping pools stop at the
                // same thin ceiling K as the beams instead of piling up to white (which bloom
                // and the ACES tonemapper then blow out / hue-shift). Per-pool bounding can't do
                // this; only the summed total can be clamped to K.
                if (!StageBeamQueue.SoftComposite)
                {
                    RecordProjectionDraw(renderGraph, resources.activeColorTexture,
                        resources.cameraDepthTexture, receiverDepth, maskOn);
                    return;
                }

                var decalDesc = camera.cameraTargetDescriptor;
                decalDesc.depthBufferBits = 0;
                decalDesc.msaaSamples     = 1;
                decalDesc.colorFormat     = RenderTextureFormat.ARGBHalf;   // needs alpha (luminance)
                var decalRT = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, decalDesc, "StageBeamDecal", true, FilterMode.Bilinear);

                RecordProjectionDraw(renderGraph, decalRT,
                    resources.cameraDepthTexture, receiverDepth, maskOn);

                // Composite: reuse the depth-aware upsample + ceiling. The pool buffer's alpha is
                // its own luminance, so the composite's modulation ratio is ≈1 and the ceiling
                // K·(1−exp(−sum/K)) applies straight to the summed pool colour.
                using var cb = renderGraph.AddRasterRenderPass<BlitData>(
                    "Stage Beam Decal Composite", out var cdata);
                cdata.Source = decalRT;
                cdata.Mat    = UpsampleMat;
                cdata.W = decalDesc.width; cdata.H = decalDesc.height;
                cdata.SoftCeiling = StageBeamQueue.SoftCeiling;
                cdata.Dither = Dither;
                cb.UseTexture(decalRT);
                cb.UseAllGlobalTextures(true);
                if (resources.cameraDepthTexture.IsValid())
                    cb.UseTexture(resources.cameraDepthTexture);
                cb.SetRenderAttachment(resources.activeColorTexture, 0);
                cb.AllowPassCulling(false);
                cb.AllowGlobalStateModification(true);
                cb.SetRenderFunc((BlitData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalVector(IdUpsampleTexelSize,
                        new Vector4(1f / d.W, 1f / d.H, d.W, d.H));
                    ctx.cmd.SetGlobalFloat(IdBeamSoftCeiling, d.SoftCeiling);
                    ctx.cmd.SetGlobalFloat(IdBeamDither, d.Dither);
                    Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1f, 1f, 0f, 0f), d.Mat, 0);
                });
            }

            // Draws every beam's projection pool (gobo × colour, receiver-masked, shadowed) into
            // <paramref name="target"/> with plain additive blend and no per-pool ceiling (the
            // ceiling, if any, is applied by the caller's composite over the summed result).
            private void RecordProjectionDraw(RenderGraph renderGraph, TextureHandle target,
                TextureHandle sceneDepth, TextureHandle receiverDepth, bool maskOn)
            {
                using var builder = renderGraph.AddRasterRenderPass<ProjData>(
                    "Stage Beam Projection", out var data);
                data.Mat = ProjectionMat;
                data.Boost = ProjectionBoost;
                data.NormalCull = ProjectionNormalCull;
                data.ShadowHardness = ProjectionShadowHardness;
                data.MaskOn = maskOn;
                data.ReceiverDepth = receiverDepth;
                builder.SetRenderAttachment(target, 0);
                builder.UseAllGlobalTextures(true);
                if (sceneDepth.IsValid()) builder.UseTexture(sceneDepth);
                if (maskOn) builder.UseTexture(receiverDepth);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ProjData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalFloat(IdProjSurfaceBoost, d.Boost);
                    ctx.cmd.SetGlobalFloat(IdProjNormalCull, d.NormalCull);
                    ctx.cmd.SetGlobalFloat(IdProjShadowHardness, d.ShadowHardness);
                    ctx.cmd.SetGlobalFloat(IdProjReceiverMask, d.MaskOn ? 1f : 0f);
                    if (d.MaskOn) ctx.cmd.SetGlobalTexture(IdProjReceiverDepth, d.ReceiverDepth);

                    if (StageBeamQueue.Instancing && AllowInstancing)
                    {
                        DrawProjectionInstanced(ctx, d.Mat);
                        return;
                    }

                    var items = StageBeamQueue.Items;
                    for (var i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        if (!Visible(in it)) continue;
                        ctx.cmd.DrawMesh(StageBeamQueue.Mesh, it.Matrix, d.Mat, 0, 0, it.Props);
                    }
                });
            }

            // GPU-instanced beam path (StageBeamQueue.Instancing): one DrawMeshInstancedProcedural
            // per gobo batch, per-beam data uploaded to a StructuredBuffer the shader indexes by
            // instance id. Falls back to null/empty gracefully.
            private StageBeamInstanceBatcher _batcher;
            private int _instancedPass = -1;
            // One reusable MPB per batch (carries the buffer, base offset and gobo textures —
            // RenderGraph's RasterCommandBuffer can't SetGlobal a raw texture/buffer, so per-draw
            // material property blocks are the way to bind them).
            private readonly List<MaterialPropertyBlock> _mpbPool = new List<MaterialPropertyBlock>();

            internal void DisposeBatcher() { _batcher?.Dispose(); _batcher = null; }

            private void DrawBeams(RasterGraphContext ctx, int w, int h)
            {
                ctx.cmd.SetGlobalVector(IdBeamRTParams, new Vector4(1f / w, 1f / h, w, h));

                if (StageBeamQueue.Instancing && AllowInstancing)
                {
                    DrawBeamsInstanced(ctx);
                    return;
                }

                var items = StageBeamQueue.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (!Visible(in it)) continue;
                    ctx.cmd.DrawMesh(StageBeamQueue.Mesh, it.Matrix,
                        StageBeamQueue.Material, 0, 0, it.Props);
                }
            }

            private bool _batchesReady;
            private int _projInstancedPass = -1;

            // Builds the per-gobo instance batches from the visible beams ONCE per frame, on the CPU
            // in AddRenderPasses (before the graph executes) — so the GraphicsBuffer upload isn't a
            // stall inside a render func, and BOTH the beam pass and the projection pass draw from
            // the same batches (the projection reuses the identical per-beam GpuBeam data).
            internal void BuildInstanceBatches()
            {
                _batchesReady = false;
                if (!(StageBeamQueue.Instancing && AllowInstancing)) return;
                var items = StageBeamQueue.Items;
                _batcher ??= new StageBeamInstanceBatcher();
                _batcher.Begin();
                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (!Visible(in it)) continue;
                    _batcher.Add(in it.Raw, it.GoboA, it.GoboB);
                }
                _batcher.BuildBatches();
                _batchesReady = _batcher.Count > 0 && _batcher.Buffer != null;
            }

            // Draws the prebuilt batches with the given material + pass (cone or projection). The
            // per-batch MPB carries the shared buffer, the batch's base offset and its gobo arrays.
            private void DrawInstancedBatches(RasterGraphContext ctx, Material material, int pass)
            {
                if (!_batchesReady) return;
                var batches = _batcher.Batches;
                for (var b = 0; b < batches.Count; b++)
                {
                    var batch = batches[b];
                    while (_mpbPool.Count <= b) _mpbPool.Add(new MaterialPropertyBlock());
                    var mpb = _mpbPool[b];
                    mpb.Clear();
                    mpb.SetBuffer(IdStageBeams, _batcher.Buffer);
                    mpb.SetInt(IdStageBeamBase, batch.Start);
                    if (batch.GoboA != null) mpb.SetTexture(IdGoboArray, batch.GoboA);
                    if (batch.GoboB != null) mpb.SetTexture(IdGoboArray2, batch.GoboB);
                    ctx.cmd.DrawMeshInstancedProcedural(StageBeamQueue.Mesh, 0, material, pass, batch.Count, mpb);
                }
            }

            private void DrawBeamsInstanced(RasterGraphContext ctx)
            {
                if (_instancedPass < 0)
                    _instancedPass = Mathf.Max(0, StageBeamQueue.Material.FindPass("StageBeamInstanced"));
                DrawInstancedBatches(ctx, StageBeamQueue.Material, _instancedPass);
            }

            // GPU-instanced projection (decal) draw — same batches as the beam pass, drawn with the
            // projection material's instanced pass. Collapses the per-beam projection DrawMesh loop
            // (its own pass in the Frame Debugger, ~one draw per beam) to one draw per gobo batch.
            private void DrawProjectionInstanced(RasterGraphContext ctx, Material mat)
            {
                if (_projInstancedPass < 0)
                    _projInstancedPass = Mathf.Max(0, mat.FindPass("StageBeamProjectionInstanced"));
                DrawInstancedBatches(ctx, mat, _projInstancedPass);
            }

            private void RecordFullRes(RenderGraph renderGraph, UniversalResourceData resources,
                                       UniversalCameraData camera)
            {
                var w = camera.cameraTargetDescriptor.width;
                var h = camera.cameraTargetDescriptor.height;

                using var builder = renderGraph.AddRasterRenderPass<ConeData>("Stage Beams", out var data);
                data.W = w; data.H = h;
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.UseAllGlobalTextures(true);
                if (resources.cameraDepthTexture.IsValid())
                    builder.UseTexture(resources.cameraDepthTexture);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ConeData d, RasterGraphContext ctx) => DrawBeams(ctx, d.W, d.H));
            }

            // Offscreen accumulation + composite: divisor 2 = the half-res path, divisor 1 =
            // full-res (used by Soft Additive, whose ceiling curve needs the summed total).
            private void RecordOffscreen(RenderGraph renderGraph, UniversalResourceData resources,
                                         UniversalCameraData camera, int divisor)
            {
                var desc = camera.cameraTargetDescriptor;
                var offW = Mathf.Max(1, desc.width  / divisor);
                var offH = Mathf.Max(1, desc.height / divisor);

                var offDesc = desc;
                offDesc.width           = offW;
                offDesc.height          = offH;
                offDesc.depthBufferBits = 0;
                offDesc.msaaSamples     = 1;
                // ARGBHalf, NOT the camera's format: the camera target is usually B10G11R11
                // (no alpha), and the Soft Additive composite needs the alpha channel — it
                // carries the haze-free total for the modulation-preserving ceiling.
                offDesc.colorFormat     = RenderTextureFormat.ARGBHalf;

                var offRes = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, offDesc, "StageBeamOffscreen", true, FilterMode.Bilinear);

                // Pass 1 — raymarch the beams into the offscreen target.
                {
                    using var builder = renderGraph.AddRasterRenderPass<ConeData>(
                        "Stage Beams (offscreen)", out var data);
                    data.W = offW; data.H = offH;
                    builder.SetRenderAttachment(offRes, 0);
                    builder.UseAllGlobalTextures(true);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((ConeData d, RasterGraphContext ctx) => DrawBeams(ctx, d.W, d.H));
                }

                // Optional pass — depth-aware 3×3 smoothing of the beam buffer. Averages the
                // stochastic jitter grain (worst in shadowed regions at Half/Quarter, where one
                // noisy texel covers a 2×2/4×4 pixel block) before it gets magnified.
                var compositeSource = offRes;
                if (NoiseSmoothing)
                {
                    var denoised = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph, offDesc, "StageBeamDenoised", false, FilterMode.Bilinear);
                    using var db = renderGraph.AddRasterRenderPass<BlitData>(
                        "Stage Beams (denoise)", out var dd);
                    dd.Source = offRes;
                    dd.Mat    = UpsampleMat;
                    dd.W = offW; dd.H = offH;
                    db.UseTexture(offRes);
                    db.UseAllGlobalTextures(true);
                    if (resources.cameraDepthTexture.IsValid())
                        db.UseTexture(resources.cameraDepthTexture);
                    db.SetRenderAttachment(denoised, 0);
                    db.AllowPassCulling(false);
                    db.AllowGlobalStateModification(true);
                    db.SetRenderFunc((BlitData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalVector(IdUpsampleTexelSize,
                            new Vector4(1f / d.W, 1f / d.H, d.W, d.H));
                        Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1f, 1f, 0f, 0f), d.Mat, 1);
                    });
                    compositeSource = denoised;
                }

                // Pass 2 — depth-aware additive upsample composite into the camera colour
                // (with the Soft Additive ceiling curve applied to the summed total).
                {
                    using var builder = renderGraph.AddRasterRenderPass<BlitData>(
                        "Stage Beams (composite)", out var data);
                    data.Source = compositeSource;
                    data.Mat    = UpsampleMat;
                    data.W = offW; data.H = offH;
                    data.SoftCeiling = StageBeamQueue.SoftComposite ? StageBeamQueue.SoftCeiling : 0f;
                    data.Dither = Dither;
                    builder.UseTexture(compositeSource);
                    builder.UseAllGlobalTextures(true);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((BlitData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalVector(IdUpsampleTexelSize,
                            new Vector4(1f / d.W, 1f / d.H, d.W, d.H));
                        ctx.cmd.SetGlobalFloat(IdBeamSoftCeiling, d.SoftCeiling);
                        ctx.cmd.SetGlobalFloat(IdBeamDither, d.Dither);
                        Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1f, 1f, 0f, 0f), d.Mat, 0);
                    });
                }
            }
        }
    }
}
