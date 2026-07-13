using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Makes a fixture's lens glow with an HDR emissive colour instead of relying on a billboard
    /// corona sprite — the lens surface itself is bright, and URP Bloom supplies the glare.
    /// Zero MVR/DMX knowledge required: add this to any GameObject with (or without) a
    /// lens-shaped <see cref="Renderer"/> and either drive it manually via the Inspector or call
    /// <see cref="SetState"/> from an external driver (e.g. <c>MvrLensEmissiveSync</c> in
    /// com.origuma.mvr-toolkit) once per frame.
    /// </summary>
    /// <remarks>
    /// Precedence: <see cref="SetState"/> stamps the frame it was called on. <see cref="LateUpdate"/>
    /// applies whatever was stamped this frame; if nothing called <see cref="SetState"/> this
    /// frame, it falls back to the serialized <see cref="Color"/> / <see cref="Intensity"/>
    /// fields (the standalone/manual path). This lets the same component work driven or
    /// undriven without extra wiring.
    ///
    /// Applies via <see cref="MaterialPropertyBlock"/> (materials are never instantiated). If the
    /// target renderer's material isn't using the <c>Origuma/StageBeamLens</c> shader, the
    /// <c>_LensColor</c> / <c>_LensIntensity</c> properties are still set (harmless no-ops), and
    /// the common <c>_EmissionColor</c> property is also set so stock lit/emissive materials
    /// (e.g. Standard, URP Lit with emission enabled) react too.
    /// </remarks>
    [AddComponentMenu("Stage Beam/Stage Beam Lens")]
    public sealed class StageBeamLens : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Renderer to drive. If null, GetComponent<Renderer>() is used; if that also " +
                 "finds nothing, a small lens disc is generated as a child GameObject.")]
        public Renderer TargetRenderer;

        [Header("Manual / standalone values")]
        [Tooltip("Lens colour, used when nothing calls SetState() this frame.")]
        public Color Color = UnityEngine.Color.white;

        [Tooltip("Lens intensity, used when nothing calls SetState() this frame.")]
        public float Intensity = 1f;

        [Header("Look")]
        [Tooltip("HDR multiplier applied on top of Color * Intensity so URP Bloom picks up the " +
                 "lens as a bright source. Bloom thresholds against display-referred brightness, " +
                 "so a boost is needed even at Intensity = 1 to push the lens over threshold.")]
        public float EmissiveBoost = 4f;

        [Tooltip("Radius (metres) of the generated fallback lens disc. Unused if a renderer is " +
                  "found or assigned.")]
        public float DiscRadius = 0.06f;

        [Tooltip("View-dependent falloff power for the generated disc's material instance. " +
                  "Higher = tighter hot-spot facing the camera.")]
        public float FresnelPower = 2f;

        private static readonly int IdLensColor = Shader.PropertyToID("_LensColor");
        private static readonly int IdLensIntensity = Shader.PropertyToID("_LensIntensity");
        private static readonly int IdFresnelPower = Shader.PropertyToID("_FresnelPower");
        private static readonly int IdEmissionColor = Shader.PropertyToID("_EmissionColor");

        private static Material _sharedLensMaterial;
        private static Shader _lensShader;

        private MaterialPropertyBlock _mpb;

        private Color _stateColor;
        private float _stateIntensity;
        private int _stateFrame = -1;

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();
        }

        // Deferred to Start (not Awake) so a caller that does
        // gameObject.AddComponent&lt;StageBeamLens&gt;() and then sets DiscRadius/TargetRenderer
        // in the same frame (e.g. MvrLensEmissiveSync.GetOrAddLens) still has its values honoured
        // — AddComponent runs Awake synchronously, but Start is deferred to before the next
        // Update, after the caller has finished configuring the component.
        private void Start()
        {
            if (TargetRenderer == null) TargetRenderer = GetComponent<Renderer>();
            if (TargetRenderer == null) TargetRenderer = GenerateLensDisc();
        }

        /// <summary>
        /// Drives the lens from an external system (e.g. a per-frame DMX/GDTF sync component).
        /// Takes precedence over the serialized <see cref="Color"/>/<see cref="Intensity"/>
        /// fields for the frame it's called on.
        /// </summary>
        public void SetState(Color color, float intensity)
        {
            _stateColor = color;
            _stateIntensity = intensity;
            _stateFrame = Time.frameCount;
        }

        private void LateUpdate()
        {
            if (TargetRenderer == null) return;

            Color color;
            float intensity;
            // Accept a SetState from this frame OR the previous one: LateUpdate script order
            // between this component and its driver is unspecified, so a driver whose LateUpdate
            // runs after ours would otherwise never win (its stamp would always look one frame
            // old). Worst case this trades one frame of latency when a driver stops.
            if (Time.frameCount - _stateFrame <= 1)
            {
                color = _stateColor;
                intensity = _stateIntensity;
            }
            else
            {
                color = Color;
                intensity = Intensity;
            }

            Apply(color, intensity);
        }

        private void Apply(Color color, float intensity)
        {
            var hdr = color * (intensity * EmissiveBoost);
            hdr.a = 1f;

            TargetRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(IdLensColor, hdr);
            _mpb.SetFloat(IdLensIntensity, intensity * EmissiveBoost);
            _mpb.SetFloat(IdFresnelPower, FresnelPower);
            // Also set the common emissive property so stock lit materials react even when not
            // using the StageBeamLens shader.
            _mpb.SetColor(IdEmissionColor, hdr);
            TargetRenderer.SetPropertyBlock(_mpb);
        }

        private Renderer GenerateLensDisc()
        {
            var go = new GameObject("Lens Disc");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = BuildDiscMesh(DiscRadius, 16);

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetSharedLensMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return renderer;
        }

        private static Material GetSharedLensMaterial()
        {
            if (_sharedLensMaterial != null) return _sharedLensMaterial;

            if (_lensShader == null) _lensShader = Shader.Find("Origuma/StageBeamLens");
            if (_lensShader == null)
            {
                Debug.LogWarning("[StageBeam] Shader 'Origuma/StageBeamLens' not found; the " +
                                  "generated lens disc will not render correctly.");
                return null;
            }

            _sharedLensMaterial = new Material(_lensShader) { name = "StageBeamLens (Generated)" };
            return _sharedLensMaterial;
        }

        /// <summary>
        /// Flat disc facing local -Y (the package's beam exit axis, matching
        /// <see cref="StageBeamInstance"/>'s documented convention), so a generated lens sits
        /// flush with a fixture whose beam exits downward in local space.
        /// </summary>
        private static Mesh BuildDiscMesh(float radius, int segments)
        {
            var vertices = new Vector3[segments + 1];
            var normals = new Vector3[segments + 1];
            var triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            normals[0] = Vector3.down;

            for (var i = 0; i < segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                normals[i + 1] = Vector3.down;
            }

            for (var i = 0; i < segments; i++)
            {
                var a = 0;
                var b = i + 1;
                var c = (i + 1) % segments + 1;
                // Wound so the face normal points down local -Y.
                triangles[i * 3 + 0] = a;
                triangles[i * 3 + 1] = c;
                triangles[i * 3 + 2] = b;
            }

            var mesh = new Mesh { name = "StageBeamLensDisc" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
