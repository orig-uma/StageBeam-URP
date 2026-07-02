using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Origuma.StageBeam
{
    public static class StageBeamQueue
    {
        public struct Item { public Matrix4x4 Matrix; public MaterialPropertyBlock Props; }

        public static Mesh Mesh;
        public static Material Material;
        public static readonly List<Item> Items = new List<Item>(128);

        public static void Begin() => Items.Clear();
        public static void Add(Matrix4x4 m, MaterialPropertyBlock props) =>
            Items.Add(new Item { Matrix = m, Props = props });
    }

    public sealed class StageBeamRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private RenderPassEvent _event = RenderPassEvent.AfterRenderingTransparents;

        [Tooltip("Render beams into a half-resolution target and upsample. " +
                 "Quarters the raymarch pixel count; slight softening of the beams.")]
        [SerializeField] private bool _halfResolution = false;

        [Header("Haze Noise (optional)")]
        [Tooltip("Tileable 3D noise that modulates beam density to look like drifting atmosphere.")]
        [SerializeField] private Texture3D _hazeNoise;
        [Tooltip("Haze turbulence amount. 0 = off (uniform beam).")]
        [Range(0f, 1f)] [SerializeField] private float _hazeStrength = 0f;
        [Tooltip("World units → noise size. Smaller = larger, softer blobs.")]
        [SerializeField] private float _hazeScale = 0.15f;
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

        [Header("Volumetric Shadows")]
        [Tooltip("Off = none. ScreenSpace = free, on-screen occluders only (camera-dependent). " +
                 "Volume = robust world-space; drop a StageBeamOcclusionVolume in the scene.")]
        [SerializeField] private ShadowMode _shadows = ShadowMode.Off;
        [Tooltip("Screen-space mode only (Volume gets these from StageBeamOcclusionVolume).")]
        [SerializeField] private float _ssStrength = 0.85f;
        [SerializeField] private int   _ssSteps = 6;
        [SerializeField] private float _ssMaxDist = 25f;
        [SerializeField] private float _ssBias = 0.05f;
        [SerializeField] private float _ssThickness = 1.5f;
        [Tooltip("Debug (Volume mode): tint the beam RED where the occupancy volume has data. " +
                 "No red = volume not bound/empty or the box doesn't cover the beam.")]
        [SerializeField] private bool _shadowDebug = false;

        public enum ShadowMode { Off, ScreenSpace, Volume }

        private StageBeamPass _pass;
        private Material _upsampleMat;
        private Material _projectionMat;

        private static readonly int IdBeamRTParams      = Shader.PropertyToID("_BeamRTParams");
        private static readonly int IdUpsampleTexelSize = Shader.PropertyToID("_BeamUpsampleTexelSize");
        private static readonly int IdHazeNoise         = Shader.PropertyToID("_HazeNoise");
        private static readonly int IdHazeStrength      = Shader.PropertyToID("_HazeStrength");
        private static readonly int IdHazeScale         = Shader.PropertyToID("_HazeScale");
        private static readonly int IdHazeScroll        = Shader.PropertyToID("_HazeScroll");
        private static readonly int IdProjSurfaceBoost  = Shader.PropertyToID("_ProjSurfaceBoost");
        private static readonly int IdProjNormalCull    = Shader.PropertyToID("_ProjNormalCull");

        public override void Create()
        {
            _pass = new StageBeamPass { renderPassEvent = _event };
            _upsampleMat   = CoreUtils.CreateEngineMaterial("Origuma/StageBeamUpsample");
            _projectionMat = CoreUtils.CreateEngineMaterial("Origuma/StageBeamProjection");
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_upsampleMat);
            CoreUtils.Destroy(_projectionMat);
        }

        private static readonly int IdShadowStrength = Shader.PropertyToID("_BeamShadowStrength");
        private static readonly int IdShadowSteps    = Shader.PropertyToID("_BeamShadowSteps");
        private static readonly int IdShadowMaxDist  = Shader.PropertyToID("_BeamShadowMaxDist");
        private static readonly int IdShadowBias     = Shader.PropertyToID("_BeamShadowBias");
        private static readonly int IdShadowThick    = Shader.PropertyToID("_BeamShadowThickness");
        private static readonly int IdShadowDebug    = Shader.PropertyToID("_BeamShadowDebug");
        private const string KwScreen = "_STAGEBEAM_SHADOWS_SCREEN";
        private const string KwVolume = "_STAGEBEAM_SHADOWS_VOLUME";

        private void ApplyShadowMode()
        {
            Shader.SetGlobalFloat(IdShadowDebug, _shadowDebug ? 1f : 0f);
            Shader.DisableKeyword(KwScreen);
            Shader.DisableKeyword(KwVolume);
            if (_shadows == ShadowMode.ScreenSpace)
            {
                Shader.EnableKeyword(KwScreen);
                Shader.SetGlobalFloat(IdShadowStrength, _ssStrength);
                Shader.SetGlobalFloat(IdShadowSteps,    _ssSteps);
                Shader.SetGlobalFloat(IdShadowMaxDist,  _ssMaxDist);
                Shader.SetGlobalFloat(IdShadowBias,     _ssBias);
                Shader.SetGlobalFloat(IdShadowThick,    _ssThickness);
            }
            else if (_shadows == ShadowMode.Volume)
            {
                Shader.EnableKeyword(KwVolume);
                // Volume globals are published by StageBeamOcclusionVolume.LateUpdate().
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            ApplyShadowMode();

            if (StageBeamQueue.Mesh == null || StageBeamQueue.Material == null) return;
            if (StageBeamQueue.Items.Count == 0) return;

            // Publish the (shared) haze noise as global shader state for this frame.
            var haze = _hazeNoise != null ? _hazeStrength : 0f;
            Shader.SetGlobalFloat(IdHazeStrength, haze);
            if (haze > 0f)
            {
                Shader.SetGlobalTexture(IdHazeNoise, _hazeNoise);
                Shader.SetGlobalFloat(IdHazeScale, _hazeScale);
                var off = _hazeScrollSpeed * Time.time;
                Shader.SetGlobalVector(IdHazeScroll, new Vector4(off.x, off.y, off.z, 0f));
            }

            _pass.HalfResolution      = _halfResolution && _upsampleMat != null;
            _pass.UpsampleMat         = _upsampleMat;
            _pass.ProjectOntoSurfaces = _projectOntoSurfaces && _projectionMat != null;
            _pass.ProjectionMat       = _projectionMat;
            _pass.ProjectionBoost     = _projectionSurfaceBoost;
            _pass.ProjectionNormalCull = _projectionNormalCull;
            renderer.EnqueuePass(_pass);
        }

        private sealed class StageBeamPass : ScriptableRenderPass
        {
            public bool     HalfResolution;
            public Material UpsampleMat;
            public bool     ProjectOntoSurfaces;
            public Material ProjectionMat;
            public float    ProjectionBoost;
            public float    ProjectionNormalCull;

            private class ConeData { public int W, H; }
            private class BlitData { public TextureHandle Source; public Material Mat; public int W, H; }
            private class ProjData { public Material Mat; public float Boost, NormalCull; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var camera    = frameData.Get<UniversalCameraData>();
                if (!resources.activeColorTexture.IsValid()) return;

                if (HalfResolution)
                    RecordHalfRes(renderGraph, resources, camera);
                else
                    RecordFullRes(renderGraph, resources, camera);

                if (ProjectOntoSurfaces)
                    RecordProjection(renderGraph, resources);
            }

            // Screen-space decal projection of each beam's gobo × colour onto opaque surfaces.
            private void RecordProjection(RenderGraph renderGraph, UniversalResourceData resources)
            {
                if (!resources.cameraDepthTexture.IsValid()) return; // needs scene depth

                using var builder = renderGraph.AddRasterRenderPass<ProjData>(
                    "Stage Beam Projection", out var data);
                data.Mat = ProjectionMat;
                data.Boost = ProjectionBoost;
                data.NormalCull = ProjectionNormalCull;
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.UseAllGlobalTextures(true);
                builder.UseTexture(resources.cameraDepthTexture);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ProjData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalFloat(IdProjSurfaceBoost, d.Boost);
                    ctx.cmd.SetGlobalFloat(IdProjNormalCull, d.NormalCull);
                    var items = StageBeamQueue.Items;
                    for (var i = 0; i < items.Count; i++)
                        ctx.cmd.DrawMesh(StageBeamQueue.Mesh, items[i].Matrix,
                            d.Mat, 0, 0, items[i].Props);
                });
            }

            private static void DrawBeams(RasterGraphContext ctx, int w, int h)
            {
                ctx.cmd.SetGlobalVector(IdBeamRTParams, new Vector4(1f / w, 1f / h, w, h));
                var items = StageBeamQueue.Items;
                for (var i = 0; i < items.Count; i++)
                    ctx.cmd.DrawMesh(StageBeamQueue.Mesh, items[i].Matrix,
                        StageBeamQueue.Material, 0, 0, items[i].Props);
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

            private void RecordHalfRes(RenderGraph renderGraph, UniversalResourceData resources,
                                       UniversalCameraData camera)
            {
                var desc = camera.cameraTargetDescriptor;
                var halfW = Mathf.Max(1, desc.width  >> 1);
                var halfH = Mathf.Max(1, desc.height >> 1);

                var halfDesc = desc;
                halfDesc.width           = halfW;
                halfDesc.height          = halfH;
                halfDesc.depthBufferBits = 0;
                halfDesc.msaaSamples     = 1;

                var halfRes = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, halfDesc, "StageBeamHalfRes", true, FilterMode.Bilinear);

                // Pass 1 — raymarch the beams into the half-res target.
                {
                    using var builder = renderGraph.AddRasterRenderPass<ConeData>(
                        "Stage Beams (half-res)", out var data);
                    data.W = halfW; data.H = halfH;
                    builder.SetRenderAttachment(halfRes, 0);
                    builder.UseAllGlobalTextures(true);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((ConeData d, RasterGraphContext ctx) => DrawBeams(ctx, d.W, d.H));
                }

                // Pass 2 — additive upsample composite into the camera colour.
                {
                    using var builder = renderGraph.AddRasterRenderPass<BlitData>(
                        "Stage Beams (upsample)", out var data);
                    data.Source = halfRes;
                    data.Mat    = UpsampleMat;
                    data.W = halfW; data.H = halfH;
                    builder.UseTexture(halfRes);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((BlitData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalVector(IdUpsampleTexelSize,
                            new Vector4(1f / d.W, 1f / d.H, d.W, d.H));
                        Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1f, 1f, 0f, 0f), d.Mat, 0);
                    });
                }
            }
        }
    }
}
