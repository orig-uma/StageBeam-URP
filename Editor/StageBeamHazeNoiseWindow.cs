// =============================================================================
//  StageBeamHazeNoiseWindow.cs
// -----------------------------------------------------------------------------
//  One-click generator for the tileable 3D noise the haze uses (StageBeamCone
//  samples _HazeNoise in world space to make the beam look like drifting fog).
//
//  Why a bespoke generator instead of a stock texture:
//   * The noise MUST tile seamlessly. The cone shader samples it in WORLD space
//     with WrapMode.Repeat, so any non-periodic noise shows a hard grid seam
//     every _HazeScale units. Every octave here wraps modulo its own frequency,
//     so the whole volume is periodic over [0,1)^3 by construction.
//   * Plain single-frequency (or naive multi-octave) noise reads as round
//     "cotton balls". Real atmosphere is wispy and filamentary. We DOMAIN-WARP
//     the fbm sample position by a low-frequency vector field, which shears the
//     blobs into drifting streaks — the main fix for the "cheap looking" haze.
//   * It writes the texture with Trilinear filtering + Repeat. The old asset was
//     Point-filtered 32^3, so the raw voxels were visible (blocky). Trilinear +
//     a higher resolution removes that on its own.
//
//  A live preview (a scrubbable Z-slice) lets you tune the look before writing
//  the asset. Output is a single-channel R8 Texture3D. The cone shader only reads
//  .r and remaps it around 0.5 (hazeF = 1 + Strength*(n-0.5)*2), so we normalise
//  the volume to [0,1] and pull contrast around 0.5 to keep the beam unbiased.
// =============================================================================
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    public sealed class StageBeamHazeNoiseWindow : EditorWindow
    {
        // ---- Exposed parameters ------------------------------------------------
        // 32^3 is the default on purpose: what made the old noise look blocky was POINT filtering,
        // not resolution. With trilinear, 32^3 reads the same as 64^3 — and it is 32 KB instead of
        // 256 KB, which matters because the haze is sampled inside the raymarch hot loop, where
        // texture-cache residency is worth more than detail nobody can see.
        private int   _resolution   = 32;    // voxels per axis (32 / 64 / 128)
        private int   _baseFrequency = 4;    // large cells across the volume (lowest octave)
        // 3 octaves keeps the finest one at frequency 4*2^2 = 16 = 32/2, i.e. exactly Nyquist for a
        // 32^3 volume. More octaves here would only add aliased noise, and would trip the warning.
        private int   _octaves      = 3;     // fbm octave count (detail layers)
        private float _gain         = 0.5f;  // amplitude falloff per octave (persistence)
        private float _warpAmount   = 0.35f; // domain warp strength (0 = plain fbm)
        private int   _warpFrequency = 2;    // frequency of the warp field (low = broad drift)
        private float _contrast     = 1.25f; // >1 sharpens wisps around the 0.5 midpoint
        private int   _seed         = 12345;
        private string _outputPath;

        private static readonly int[] Resolutions = { 32, 64, 128 };
        private static readonly string[] ResolutionLabels = { "32", "64", "128" };

        // Lacunarity is fixed at 2. Tileability requires every octave frequency to be an
        // integer multiple of the base (so it wraps over [0,1)); doubling keeps that exact.
        private const int Lacunarity = 2;

        // ---- Live preview state ------------------------------------------------
        // The preview only ever displays ONE Z-slice, so it computes only that slice (size^2
        // fbm evals), never the whole volume — cheap enough to stay live while dragging.
        private const int PreviewDisplayPx = 260;
        // Fixed normalisation range for the preview so scrubbing Z doesn't flicker in brightness
        // (per-slice min/max would). Amplitude-normalised fbm lives roughly within ±this.
        private const float PreviewHalfRange = 0.7f;
        private float   _previewZ = 0.5f;    // slice depth [0,1]
        private Texture2D _previewSlice;
        private bool    _previewDirty = true; // params or Z changed → rebuild the slice bitmap

        [MenuItem("Window/Origuma/Haze Noise Generator")]
        public static void Open()
        {
            var window = GetWindow<StageBeamHazeNoiseWindow>(false, "Haze Noise Generator");
            window.minSize = new Vector2(460, 720);
            window.Show();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_outputPath))
                _outputPath = ResolveDefaultOutputPath();
            _previewDirty = true;
        }

        private void OnDisable()
        {
            if (_previewSlice != null) DestroyImmediate(_previewSlice);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tileable 3D Haze Noise", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Generates the seamless 3D noise the haze samples in world space. Domain-warped " +
                "fbm gives drifting, filamentary fog instead of round blobs. Written with " +
                "Trilinear filtering + Repeat wrap.",
                MessageType.Info);

            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();

            int resIndex = Mathf.Max(0, System.Array.IndexOf(Resolutions, _resolution));
            resIndex = EditorGUILayout.Popup(
                new GUIContent("Resolution", "Voxels per axis. R8, so 64^3 = 256 KB, 128^3 = 2 MB."),
                resIndex, ResolutionLabels);
            _resolution = Resolutions[resIndex];

            _baseFrequency = EditorGUILayout.IntSlider(
                new GUIContent("Base Frequency", "Large cells across the volume (lowest octave). " +
                    "Higher = smaller base blobs."), _baseFrequency, 1, 16);

            _octaves = EditorGUILayout.IntSlider(
                new GUIContent("Octaves", "Detail layers. Each doubles frequency and scales " +
                    "amplitude by Gain."), _octaves, 1, 8);

            _gain = EditorGUILayout.Slider(
                new GUIContent("Gain", "Amplitude falloff per octave (persistence). ~0.5 is natural."),
                _gain, 0.2f, 0.8f);

            EditorGUILayout.Space(2);
            _warpAmount = EditorGUILayout.Slider(
                new GUIContent("Domain Warp", "Shears the blobs into wispy streaks. 0 = plain fbm."),
                _warpAmount, 0f, 1f);

            using (new EditorGUI.DisabledScope(_warpAmount <= 0f))
            {
                _warpFrequency = EditorGUILayout.IntSlider(
                    new GUIContent("Warp Frequency", "Frequency of the warp field. Low = broad, " +
                        "slow drift; high = turbulent."), _warpFrequency, 1, 8);
            }

            _contrast = EditorGUILayout.Slider(
                new GUIContent("Contrast", "Pulls values away from 0.5 to sharpen the wisps."),
                _contrast, 0.5f, 3f);

            _seed = EditorGUILayout.IntField(new GUIContent("Seed"), _seed);

            if (EditorGUI.EndChangeCheck())
                _previewDirty = true;

            // Nyquist sanity: an octave finer than half the resolution just aliases into noise.
            int maxFreq = _baseFrequency * Mathf.RoundToInt(Mathf.Pow(Lacunarity, _octaves - 1));
            if (maxFreq > _resolution / 2)
            {
                EditorGUILayout.HelpBox(
                    $"Finest octave frequency ({maxFreq}) exceeds half the resolution " +
                    $"({_resolution / 2}). It will alias — raise Resolution or lower Octaves/Base " +
                    "Frequency for clean detail.",
                    MessageType.Warning);
            }

            DrawPreview();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputPath = EditorGUILayout.TextField(new GUIContent("Asset Path"), _outputPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var picked = EditorUtility.SaveFilePanelInProject(
                        "Save Haze Noise", Path.GetFileNameWithoutExtension(_outputPath),
                        "asset", "Choose where to save the Texture3D asset.");
                    if (!string.IsNullOrEmpty(picked)) _outputPath = picked;
                }
            }

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputPath)))
            {
                if (GUILayout.Button("Generate", GUILayout.Height(30)))
                    Generate();
            }
        }

        // -----------------------------------------------------------------------
        //  Live preview
        // -----------------------------------------------------------------------
        private void DrawPreview()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _previewZ = EditorGUILayout.Slider(new GUIContent("Z Slice"), _previewZ, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                _previewDirty = true;

            // Only rebuild on change, and only during Repaint (Layout fires first with the same
            // state — no need to compute twice). One slice = size^2 evals, so this stays live.
            if (_previewDirty && Event.current.type == EventType.Repaint)
            {
                RebuildPreviewSlice();
                _previewDirty = false;
            }

            if (_previewSlice != null)
            {
                var rect = GUILayoutUtility.GetRect(PreviewDisplayPx, PreviewDisplayPx,
                    GUILayout.ExpandWidth(false));
                rect.x = (position.width - PreviewDisplayPx) * 0.5f; // centre it
                rect.width = PreviewDisplayPx;
                EditorGUI.DrawPreviewTexture(rect, _previewSlice);
                int size = _previewSlice.width;
                EditorGUILayout.LabelField(
                    $"{size}^2 slice · z {Mathf.RoundToInt(_previewZ * (size - 1))}/{size - 1}",
                    EditorStyles.centeredGreyMiniLabel);
            }
        }

        // Computes just the current Z-slice through the full noise pipeline (domain warp +
        // fbm), normalised with a fixed range so scrubbing doesn't flicker. This is the whole
        // reason the preview is cheap: one slice instead of the entire volume.
        private void RebuildPreviewSlice()
        {
            int size = _resolution;
            BuildPermutation(_seed);

            if (_previewSlice == null || _previewSlice.width != size)
            {
                if (_previewSlice != null) DestroyImmediate(_previewSlice);
                _previewSlice = new Texture2D(size, size, TextureFormat.RGB24, false)
                {
                    filterMode = FilterMode.Bilinear, // smooth like the trilinear runtime sample
                    wrapMode   = TextureWrapMode.Repeat,
                };
            }

            float pz = (Mathf.Clamp(Mathf.RoundToInt(_previewZ * (size - 1)), 0, size - 1) + 0.5f) / size;
            float toUnit = 0.5f / PreviewHalfRange; // raw ±HalfRange → [0,1]
            var px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float pxc = (x + 0.5f) / size;
                float pyc = (y + 0.5f) / size;

                float wx = pxc, wy = pyc, wz = pz;
                if (_warpAmount > 0f)
                {
                    wx = pxc + _warpAmount * Fbm(pxc, pyc, pz, _warpFrequency, 3);
                    wy = pyc + _warpAmount * Fbm(pxc + 5.2f, pyc + 1.3f, pz + 2.7f, _warpFrequency, 3);
                    wz = pz  + _warpAmount * Fbm(pxc + 9.2f, pyc + 8.1f, pz + 4.4f, _warpFrequency, 3);
                }

                float raw = Fbm(wx, wy, wz, _baseFrequency, _octaves);
                float n = raw * toUnit + 0.5f;               // fixed-range normalise
                n = 0.5f + (n - 0.5f) * _contrast;           // same contrast as the asset
                byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(n * 255f), 0, 255);
                px[x + y * size] = new Color32(b, b, b, 255);
            }
            _previewSlice.SetPixels32(px);
            _previewSlice.Apply(false);
        }

        // -----------------------------------------------------------------------
        //  Generation
        // -----------------------------------------------------------------------
        private void Generate()
        {
            int size = _resolution;

            int maxFreq = _baseFrequency * Mathf.RoundToInt(Mathf.Pow(Lacunarity, _octaves - 1));
            if (maxFreq > 256) // 256 = tiling limit for the 512-entry perm table
            {
                EditorUtility.DisplayDialog("Haze Noise",
                    $"Octave frequency ({maxFreq}) exceeds the 256 tiling limit. Lower Octaves or " +
                    "Base Frequency.", "OK");
                return;
            }

            var raw = ComputeRawVolume(size, out float min, out float max);
            if (raw == null) return;

            var bytes = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                float n = MapValue(raw[i], min, max);
                bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(n * 255f), 0, 255);
            }
            SaveAsset(bytes, size);
        }

        // Fills a raw fbm volume (~[-1,1]) and reports its true min/max for normalisation.
        // Used by the final asset build; the preview computes single slices instead.
        private float[] ComputeRawVolume(int size, out float min, out float max)
        {
            BuildPermutation(_seed);
            var raw = new float[size * size * size];
            min = float.MaxValue; max = float.MinValue;

            try
            {
                for (int z = 0; z < size; z++)
                {
                    if ((z & 3) == 0)
                        EditorUtility.DisplayProgressBar("Haze Noise",
                            "Generating tileable 3D noise…", (float)z / size);

                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        // Sample at voxel centres in [0,1). Centre sampling keeps the volume
                        // symmetric so the Repeat seam lands exactly between voxels.
                        float px = (x + 0.5f) / size;
                        float py = (y + 0.5f) / size;
                        float pz = (z + 0.5f) / size;

                        float wx = px, wy = py, wz = pz;
                        if (_warpAmount > 0f)
                        {
                            // Decorrelated offsets so the three warp channels don't line up.
                            wx = px + _warpAmount * Fbm(px, py, pz, _warpFrequency, 3);
                            wy = py + _warpAmount * Fbm(px + 5.2f, py + 1.3f, pz + 2.7f, _warpFrequency, 3);
                            wz = pz + _warpAmount * Fbm(px + 9.2f, py + 8.1f, pz + 4.4f, _warpFrequency, 3);
                        }

                        float v = Fbm(wx, wy, wz, _baseFrequency, _octaves); // ~[-1,1]
                        int idx = x + y * size + z * size * size;
                        raw[idx] = v;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return raw;
        }

        // Raw fbm → [0,1]: min/max normalise, then pull contrast around the 0.5 midpoint the
        // cone shader expects (hazeF = 1 + Strength*(n-0.5)*2).
        private float MapValue(float raw, float min, float max)
        {
            float inv = max > min ? 1f / (max - min) : 0f;
            float n = (raw - min) * inv;
            return 0.5f + (n - 0.5f) * _contrast;
        }

        private void SaveAsset(byte[] bytes, int size)
        {
            var tex = new Texture3D(size, size, size, TextureFormat.R8, false)
            {
                name = Path.GetFileNameWithoutExtension(_outputPath),
                wrapMode   = TextureWrapMode.Repeat,   // world-space tiling — must wrap
                filterMode = FilterMode.Trilinear,     // the fix for blocky/point-sampled haze
                anisoLevel = 0,
            };
            tex.SetPixelData(bytes, 0);
            tex.Apply(false, true); // no mipmaps; drop the CPU copy (matches the shipped asset)

            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                Directory.CreateDirectory(dir);

            var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(_outputPath);
            Texture3D saved;
            if (existing != null)
            {
                // Overwrite the EXISTING object rather than the path. AssetDatabase.CreateAsset
                // DELETES whatever is at the path first, which mints a new GUID and silently breaks
                // every reference to the old one — a Renderer Feature's Haze Noise slot, anything
                // pointing at it from a scene. CopySerialized writes the new contents (including the
                // resolution and format, so a size change is fine) into the object that is already
                // there, so the .meta and its GUID survive — the asset can be re-imported
                // without orphaning scene references.
                EditorUtility.CopySerialized(tex, existing);
                DestroyImmediate(tex);              // in-memory temp, never persisted
                EditorUtility.SetDirty(existing);
                saved = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(tex, _outputPath);
                saved = tex;
            }
            AssetDatabase.SaveAssets();

            EditorGUIUtility.PingObject(saved);
            Selection.activeObject = saved;
            Debug.Log($"<color=#5aa9e6>[StageBeam]</color> Generated haze noise {size}^3 " +
                      $"(Trilinear/Repeat) → {_outputPath}" +
                      (existing != null ? " (existing asset updated in place — GUID preserved)" : ""),
                      saved);
        }

        // -----------------------------------------------------------------------
        //  Tileable Perlin fbm
        // -----------------------------------------------------------------------

        // Fractal sum of periodic Perlin octaves. baseFreq is the lowest octave's period;
        // each octave doubles it. Every octave wraps modulo its own frequency, so the result
        // is seamless over [0,1)^3. Returned roughly in [-1,1] (normalised by total amplitude).
        private float Fbm(float x, float y, float z, int baseFreq, int octaves)
        {
            float sum = 0f, amp = 1f, ampSum = 0f;
            int freq = baseFreq;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Perlin(x * freq, y * freq, z * freq, freq);
                ampSum += amp;
                amp *= _gain;
                freq *= Lacunarity;
            }
            return ampSum > 0f ? sum / ampSum : 0f;
        }

        // Classic Perlin gradient noise with a periodic lattice (period = wrap). Lattice cells
        // are wrapped modulo `wrap` before hashing, so noise(p) == noise(p + wrap) on every axis.
        private float Perlin(float x, float y, float z, int wrap)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;

            int X0 = Mod(xi, wrap), X1 = Mod(xi + 1, wrap);
            int Y0 = Mod(yi, wrap), Y1 = Mod(yi + 1, wrap);
            int Z0 = Mod(zi, wrap), Z1 = Mod(zi + 1, wrap);

            float u = Fade(xf), v = Fade(yf), w = Fade(zf);

            int aaa = _perm[_perm[_perm[X0] + Y0] + Z0];
            int aba = _perm[_perm[_perm[X0] + Y1] + Z0];
            int aab = _perm[_perm[_perm[X0] + Y0] + Z1];
            int abb = _perm[_perm[_perm[X0] + Y1] + Z1];
            int baa = _perm[_perm[_perm[X1] + Y0] + Z0];
            int bba = _perm[_perm[_perm[X1] + Y1] + Z0];
            int bab = _perm[_perm[_perm[X1] + Y0] + Z1];
            int bbb = _perm[_perm[_perm[X1] + Y1] + Z1];

            float x1 = Lerp(Grad(aaa, xf, yf, zf),       Grad(baa, xf - 1, yf, zf),       u);
            float x2 = Lerp(Grad(aba, xf, yf - 1, zf),   Grad(bba, xf - 1, yf - 1, zf),   u);
            float y1 = Lerp(x1, x2, v);
            x1 = Lerp(Grad(aab, xf, yf, zf - 1),         Grad(bab, xf - 1, yf, zf - 1),   u);
            x2 = Lerp(Grad(abb, xf, yf - 1, zf - 1),     Grad(bbb, xf - 1, yf - 1, zf - 1), u);
            float y2 = Lerp(x1, x2, v);
            return Lerp(y1, y2, w);
        }

        private int[] _perm; // 512-entry doubled permutation (avoids index wrap on lookups)

        private void BuildPermutation(int seed)
        {
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            var rng = new System.Random(seed);
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            _perm = new int[512];
            for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
        }

        private static int Mod(int a, int m) => ((a % m) + m) % m;
        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        private static float Lerp(float a, float b, float t) => a + t * (b - a);

        private static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        // -----------------------------------------------------------------------
        private static string ResolveDefaultOutputPath()
        {
            // Reuse the shipped asset's location so a regenerate overwrites it in place.
            var guids = AssetDatabase.FindAssets("HazeFBM3D t:Texture3D");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (path.EndsWith("HazeFBM3D.asset")) return path;
            }
            return "Packages/com.origuma.stage-beam/Runtime/Rendering/Resources/HazeFBM3D.asset";
        }
    }
}
