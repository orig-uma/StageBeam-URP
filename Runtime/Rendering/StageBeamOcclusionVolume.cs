using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// OPTIONAL manual override for volumetric shadows. You normally don't need this:
    /// with the renderer feature's Shadows = Volume, the occupancy volume is built
    /// automatically every frame with zero scene setup.
    ///
    /// Add this component only when you want explicit control — manual box placement/size,
    /// a different resolution, per-scene occluder discovery tuning, or debug gizmos. While
    /// one is enabled, the renderer feature's automatic build steps aside and this component
    /// owns the volume (its settings replace the feature's Volume-mode fields entirely).
    /// </summary>
    [AddComponentMenu("Stage Beam/Stage Beam Occlusion Volume (Override)")]
    [DefaultExecutionOrder(-50)] // build before StageBeamDriver's LateUpdate pushes beams
    public sealed class StageBeamOcclusionVolume : MonoBehaviour
    {
        /// <summary>The enabled override component the renderer feature defers to (null = the
        /// feature builds the volume itself).</summary>
        public static StageBeamOcclusionVolume Active { get; private set; }

        /// <summary>How this component's shadow settings interact with the Renderer Feature's.</summary>
        public enum SettingsMode
        {
            /// <summary>Default. This component only pins the volume BOX (manual region / no AutoFit)
            /// and the advanced knobs; the MAIN shadow behaviour (occluder mask, density, steps,
            /// mesh voxelize, axis count, static/dynamic split, strength, temporal, max distance)
            /// still comes from the Renderer Feature — so editing the feature keeps working.</summary>
            UseRendererFeatureSettings,
            /// <summary>This component owns EVERYTHING — the Renderer Feature's occlusion settings
            /// are fully ignored while it's present.</summary>
            Override,
        }

        [Tooltip("Use Renderer Feature Settings (default): this component only sets the volume box/" +
                 "region — the main shadow behaviour still comes from the Renderer Feature. " +
                 "Override: this component owns all occlusion settings (feature's are ignored).")]
        public SettingsMode Mode = SettingsMode.UseRendererFeatureSettings;

        [Header("Compute")]
        [Tooltip("StageBeamOcclusion.compute. Leave empty to auto-load the one bundled with " +
                 "the package — no manual assignment needed.")]
        public ComputeShader Occlusion;

        [Header("Volume placement (world space)")]
        [Tooltip("Auto-size the box to cover the collected occluders every frame. No positioning " +
                 "needed — the box always wraps the performers/props. Overrides the manual placement.")]
        public bool AutoFit = true;
        [Tooltip("Padding added around the occluders when Auto Fit is on (metres).")]
        public float AutoFitPadding = 0.75f;
        [Tooltip("Manual mode: the box follows this GameObject's position (+ Center offset).")]
        public bool FollowTransform = true;
        [Tooltip("Centre of the volume (world, or an offset from this transform when Follow Transform).")]
        public Vector3 Center = Vector3.zero;
        [Tooltip("World-space size of the volume. Make it just cover the lit stage volume.")]
        public Vector3 Size = new Vector3(20f, 12f, 20f);
        [Tooltip("Voxels per axis. 64 is a good start; raise for crisper blobs.")]
        public Vector3Int Resolution = new Vector3Int(96, 72, 96);

        [Header("Occluder discovery (no per-object setup)")]
        [Tooltip("Layers whose renderers cast volumetric shadows (performers, set, props).")]
        public LayerMask OccluderMask = ~0;
        [Tooltip("How often to re-scan the scene for occluders, in seconds. Bounds of already-known " +
                 "occluders are refreshed every frame regardless, so moving/skinned meshes stay correct. " +
                 "Lower = runtime-spawned occluders are picked up sooner.")]
        public float RescanInterval = 1.0f;
        [Tooltip("Voxelize the occluders' REAL meshes (skinned poses and cloth included) " +
                 "instead of sphere/box approximations — geometry-true silhouettes at voxel " +
                 "resolution, cost still independent of the light count.")]
        public bool MeshVoxelize = true;
        [Tooltip("Mesh-voxelization passes (X/Y/Z). 3 = fully conservative; 2 drops the top-down " +
                 "pass for a cheaper occlusion build (recorded draw count scales with this).")]
        [Range(1, 3)] public int VoxelizeAxisCount = 3;
        [Tooltip("Static/dynamic split (perf): voxelize non-moving occluders once (cached), " +
                 "re-voxelize only dynamic ones each build. Automatic classification. MeshVoxelize only.")]
        public bool StaticDynamicSplit;
        [Tooltip("Legacy approximation (Mesh Voxelize off): emit one sphere per bone for " +
                 "skinned meshes instead of a single box.")]
        public bool ArticulateSkinnedMeshes = true;
        [Tooltip("Max spheres taken from a skinned mesh's skeleton, sampled evenly across its bones.")]
        [Range(1, 32)] public int BonesPerOccluder = 16;
        [Tooltip("Bone sphere radius = skeleton span * this (bone positions, NOT renderer bounds " +
                 "— inflated culling bounds don't fatten the shadow). ~0.08 reads as limbs.")]
        [Range(0.02f, 0.3f)] public float BoneRadiusScale = 0.12f;
        [Tooltip("Ignore occluders whose bounds are larger than this (floors, walls) so they don't " +
                 "swallow the whole volume. 0 = keep everything.")]
        public float MaxOccluderSize = 8f;
        [Tooltip("Hard cap on occluders uploaded per frame.")]
        public int MaxOccluders = 128;

        [Header("Debug")]
        [Tooltip("Draw the volume box + collected occluder spheres in the Scene view (at runtime).")]
        public bool DebugDraw = true;
        [Tooltip("Log occluder / sphere counts + volume state to the Console when they change.")]
        public bool DebugLog = false;

        [Header("Shadow look (also settable per your taste)")]
        [Range(0f, 1f)] public float Strength = 0.85f;
        [Tooltip("Occluder opacity per metre along the shadow ray (Beer-Lambert extinction). " +
                 "4 makes a ~0.5 m body block ~85%; raise for harder, inkier shadows.")]
        [Range(1f, 16f)] public float ShadowDensity = 8f;
        [Tooltip("Samples along the part of the shadow ray that crosses the occlusion volume. " +
                 "The ray always reaches the light (shafts run the beam's full length); raise " +
                 "this only if thin occluders shimmer or band.")]
        [Range(1, 48)]  public int   ShadowSteps = 16;
        public float MaxShadowDistance = 25f;
        public float Bias = 0.05f;
        [Tooltip("World-space radius around each beam's own apex where the shadow march is skipped. " +
                 "The fixture housing sits right at the apex, so without this the volume self-shadows " +
                 "the beam's own root if the fixture's renderer is (or overlaps) an occluder.")]
        public float LightBias = 0.5f;
        [Range(0f, 1f)] public float EdgeSoftness = 0.5f;   // volume-splat soft shell
        [Tooltip("Temporal smoothing (0 = off, 1 = heavy): absorbs voxel chatter from moving " +
                 "occluders so shadow edges glide instead of flickering. Near-free.")]
        [Range(0f, 0.95f)] public float TemporalSmoothing = 0.6f;
        public float ScreenSpaceThickness = 1.5f;            // only used by the screen-space backend

        private StageBeamOcclusionBuilder _builder;
        private int _lastLogged = -1;

        // Editor-only: called when the component is first added / reset, so the Inspector shows
        // the bundled compute shader instead of an empty field.
        private void Reset()
        {
            Occlusion = Resources.Load<ComputeShader>("StageBeamOcclusion");
        }

        private void OnEnable()
        {
            Active = this;
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
            _builder?.Release();   // also neutralises the shadow globals
            _builder = null;
        }

        /// <summary>
        /// Copies this component's settings into its builder and returns it, for
        /// <see cref="StageBeamRendererFeature"/> to build from INSIDE the render loop
        /// (AddRenderPasses) — NOT from this component's LateUpdate, which made the result
        /// camera-dependent (Scene view worked, Game view didn't).
        /// </summary>
        public StageBeamOcclusionBuilder ConfigureBuilder()
        {
            _builder ??= new StageBeamOcclusionBuilder();
            var b = _builder;

            b.Occlusion              = Occlusion;
            b.OccluderMask           = OccluderMask;
            b.MeshVoxelize           = MeshVoxelize;
            b.VoxelizeAxisCount      = VoxelizeAxisCount;
            b.StaticDynamicSplit     = StaticDynamicSplit;
            b.RescanInterval         = RescanInterval;
            b.ArticulateSkinnedMeshes = ArticulateSkinnedMeshes;
            b.BonesPerOccluder       = BonesPerOccluder;
            b.BoneRadiusScale        = BoneRadiusScale;
            b.MaxOccluderSize        = MaxOccluderSize;
            b.MaxOccluders           = MaxOccluders;
            b.AutoFit                = AutoFit;
            b.AutoFitPadding         = AutoFitPadding;
            b.ManualCenter           = FollowTransform ? transform.position + Center : Center;
            b.ManualSize             = Size;
            b.Resolution             = Resolution;
            b.EdgeSoftness           = EdgeSoftness;
            b.TemporalSmoothing      = TemporalSmoothing;
            b.Strength               = Strength;
            b.ShadowDensity          = ShadowDensity;
            b.ShadowSteps            = ShadowSteps;
            b.MaxShadowDistance      = MaxShadowDistance;
            b.Bias                   = Bias;
            b.LightBias              = LightBias;
            b.ScreenSpaceThickness   = ScreenSpaceThickness;

            if (DebugLog && b.SphereCount + b.BoxCount != _lastLogged)
            {
                _lastLogged = b.SphereCount + b.BoxCount;
                Debug.Log($"[StageBeamOcclusion] occluders={b.OccluderCount} spheres={b.SphereCount} " +
                          $"boxes={b.BoxCount} box(center={b.BoxCenter}, size={b.BoxSize}) " +
                          $"autofit={AutoFit} strength={Strength} steps={ShadowSteps} " +
                          $"density={ShadowDensity}", this);
            }
            return b;
        }

        private void OnDrawGizmos()
        {
            if (!DebugDraw) return;
            // Show the box actually used this frame (auto-fitted / transform-followed at runtime),
            // falling back to the manual placement in edit mode before it's computed.
            bool live = Application.isPlaying && _builder != null;
            Vector3 c = live ? _builder.BoxCenter : (FollowTransform ? transform.position + Center : Center);
            Vector3 s = live ? _builder.BoxSize : Size;
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.4f);
            Gizmos.DrawWireCube(c, s);

            // Collected occluder shapes (populated at runtime) — verify they sit inside the box.
            if (!live) return;
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.7f);
            for (int i = 0; i < _builder.SphereCount; i++)
            {
                var sphere = _builder.GetSphere(i);
                Gizmos.DrawWireSphere(new Vector3(sphere.x, sphere.y, sphere.z), sphere.w);
            }
            var prev = Gizmos.matrix;
            for (int i = 0; i < _builder.BoxCount; i++)
            {
                _builder.GetBox(i, out var bc, out var he, out var rot);
                Gizmos.matrix = Matrix4x4.TRS(bc, rot, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, he * 2f);
            }
            Gizmos.matrix = prev;
        }
    }
}
