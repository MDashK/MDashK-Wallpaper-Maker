using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MDashKWallpaperMakerMobile
{
    internal enum Waifu2xModel
    {
        /// <summary>swin_unet / art - best quality for anime illustrations (2x and 4x models).</summary>
        SwinUnetArt,

        /// <summary>cunet / art - older, lighter and faster (2x model only).</summary>
        CunetArt,
    }

    internal enum Waifu2xDevice
    {
        Auto,
        Gpu,
        Cpu,
    }

    /// <summary>waifu2x options (the "waifu2x" window). Persisted in the settings.</summary>
    internal sealed class Waifu2xOptions
    {
        public Waifu2xModel Model { get; set; } = Waifu2xModel.SwinUnetArt;

        /// <summary>-1 = no noise reduction, 0..3 = JPEG noise reduction level.</summary>
        public int NoiseLevel { get; set; } = 0;

        public Waifu2xDevice Device { get; set; } = Waifu2xDevice.Auto;

        /// <summary>DXGI adapter index when <see cref="Device"/> is Gpu (-1 = choose automatically).</summary>
        public int GpuAdapter { get; set; } = -1;

        /// <summary>Requested tile size (input pixels); adjusted to the nearest size the model accepts.</summary>
        public int TileSize { get; set; } = 256;

        public Waifu2xOptions Clone() => (Waifu2xOptions)MemberwiseClone();

        public string Describe() =>
            $"{(Model == Waifu2xModel.SwinUnetArt ? "swin_unet/art" : "cunet/art")}, " +
            $"noise {(NoiseLevel < 0 ? "none" : NoiseLevel.ToString())}, {DeviceText}, tile {TileSize}";

        public string DeviceText => Device switch
        {
            Waifu2xDevice.Cpu => "CPU",
            Waifu2xDevice.Gpu when GpuAdapter >= 0 => GpuAdapters.All.FirstOrDefault(a => a.Index == GpuAdapter)?.Display ?? $"GPU {GpuAdapter}",
            _ => "Auto",
        };
    }

    /// <summary>
    /// waifu2x upscaler (official ONNX models from nagadomi/nunif, same as "unlimited:waifu2x").
    /// The image is processed in overlapping tiles with replication padding; tile seams are blended with weights.
    /// Runs on the GPU through DirectML when available, otherwise on the CPU.
    /// </summary>
    internal sealed class Waifu2xUpscaler : IDisposable
    {
        private const int BlendSize = 16; // output pixels blended at tile seams (as in nunif)

        private readonly Dictionary<string, InferenceSession> _sessions = new();
        private readonly object _gate = new();

        public static string ModelsDir => Path.Combine(ModelHub.ModelsDir, "waifu2x");

        public static bool Available => File.Exists(Path.Combine(ModelsDir, "swin_unet", "art", "scale2x.onnx"));

        /// <summary>Device actually used by the last session created ("GPU (DirectML)" or "CPU").</summary>
        public string LastDevice { get; private set; } = "-";

        public static readonly int[] Factors = { 2, 4, 6, 8 };

        private static string ModelFile(Waifu2xModel model, int noise, int scale)
        {
            string dir = model == Waifu2xModel.SwinUnetArt ? Path.Combine("swin_unet", "art") : Path.Combine("cunet", "art");
            string name = (noise >= 0 ? $"noise{noise}_" : "") + $"scale{scale}x.onnx";
            return Path.Combine(ModelsDir, dir, name);
        }

        private static int Offset(Waifu2xModel model, int scale) =>
            model == Waifu2xModel.SwinUnetArt ? (scale == 4 ? 32 : 16) : 36;

        /// <summary>Tile sizes the models accept (same rules as unlimited:waifu2x).</summary>
        public static int ValidTileSize(Waifu2xModel model, int scale, int requested)
        {
            int t = Math.Max(64, requested);
            if (model == Waifu2xModel.SwinUnetArt)
            {
                while ((t - 16) % 12 != 0 || (t - 16) % 16 != 0) t++;
                return t;
            }
            int offset = Offset(model, scale);
            int adj = scale == 1 ? 16 : 32;
            t = (t * scale + offset * 2 - adj) / scale;
            t -= t % 4;
            return t;
        }

        /// <summary>The model passes needed for a total factor (x6 = x8 followed by a high quality reduction).</summary>
        public static int[] Passes(Waifu2xModel model, int factor)
        {
            bool has4x = model == Waifu2xModel.SwinUnetArt;
            return factor switch
            {
                2 => new[] { 2 },
                4 => has4x ? new[] { 4 } : new[] { 2, 2 },
                6 or 8 => has4x ? new[] { 4, 2 } : new[] { 2, 2, 2 },
                _ => throw new ArgumentOutOfRangeException(nameof(factor), "Supported factors: 2, 4, 6, 8."),
            };
        }

        /// <summary>Problems met during the last <see cref="Upscale"/> (GPU not usable, fallback to the CPU...).</summary>
        public string LastWarning { get; private set; }

        /// <param name="progress">(tiles done, total tiles) over all passes.</param>
        public RgbImage Upscale(RgbImage src, int factor, Waifu2xOptions options, Action<int, int> progress = null,
                                CancellationToken token = default)
        {
            _warnings.Clear();
            try
            {
                return UpscaleCore(src, factor, options, progress, token);
            }
            finally
            {
                LastWarning = _warnings.Count == 0 ? null : string.Join(" ", _warnings.Distinct());
            }
        }

        private readonly List<string> _warnings = new();

        private RgbImage UpscaleCore(RgbImage src, int factor, Waifu2xOptions options, Action<int, int> progress, CancellationToken token)
        {
            var passes = Passes(options.Model, factor);

            // Count all tiles for a single progress bar.
            int total = 0, w = src.Width, h = src.Height;
            foreach (int s in passes)
            {
                var p = Layout(w, h, s, Offset(options.Model, s), ValidTileSize(options.Model, s, options.TileSize));
                total += p.HBlocks * p.WBlocks;
                w *= s; h *= s;
            }

            int done = 0;
            RgbImage img = src;
            for (int i = 0; i < passes.Length; i++)
            {
                int noise = i == 0 ? options.NoiseLevel : -1; // noise reduction only on the original pixels
                img = Pass(img, options, passes[i], noise, () => progress?.Invoke(++done, total), token);
            }

            int product = passes.Aggregate(1, (a, b) => a * b);
            if (product != factor)
            {
                int tw = src.Width * factor, th = src.Height * factor;
                img = img.Resize(tw, th);
            }
            return img;
        }

        private sealed class TileLayout
        {
            public int InOffset, InStep, OutStep, HBlocks, WBlocks, PaddedW, PaddedH, Tile, Scale, OutTile;
        }

        private static TileLayout Layout(int xw, int xh, int scale, int offset, int tile)
        {
            var p = new TileLayout { Scale = scale, Tile = tile };
            p.InOffset = (int)Math.Ceiling(offset / (double)scale);
            int inBlend = (int)Math.Ceiling(BlendSize / (double)scale);
            p.InStep = tile - (p.InOffset * 2 + inBlend);
            p.OutStep = p.InStep * scale;
            p.OutTile = tile * scale - offset * 2;
            int inH = 0, inW = 0;
            while (inH < xh + p.InOffset * 2) { inH = p.HBlocks * p.InStep + tile; p.HBlocks++; }
            while (inW < xw + p.InOffset * 2) { inW = p.WBlocks * p.InStep + tile; p.WBlocks++; }
            p.PaddedW = inW;
            p.PaddedH = inH;
            return p;
        }

        private RgbImage Pass(RgbImage src, Waifu2xOptions options, int scale, int noise, Action tileDone, CancellationToken token)
        {
            int offset = Offset(options.Model, scale);
            int tile = ValidTileSize(options.Model, scale, options.TileSize);
            var p = Layout(src.Width, src.Height, scale, offset, tile);
            string file = ModelFile(options.Model, noise, scale);
            var (session, adapter) = GetSession(file, options);
            string inputName = session.InputMetadata.Keys.First();

            // Replication padding: padded pixel (x, y) = source pixel clamped into the image.
            int sw = src.Width, sh = src.Height;
            int outW = sw * scale, outH = sh * scale;
            var acc = new float[3L * outW * outH];
            var wsum = new float[(long)outW * outH];
            var weight = BlendWeights(p.OutTile, BlendSize);
            var input = new DenseTensor<float>(new[] { 1, 3, tile, tile });
            int plane = tile * tile;
            byte[] d = src.Data;

            for (int hi = 0; hi < p.HBlocks; hi++)
            {
                for (int wi = 0; wi < p.WBlocks; wi++)
                {
                    token.ThrowIfCancellationRequested();
                    int y0 = hi * p.InStep - p.InOffset, x0 = wi * p.InStep - p.InOffset; // in source coordinates
                    var buf = input.Buffer.Span;
                    for (int y = 0; y < tile; y++)
                    {
                        int sy = Math.Clamp(y0 + y, 0, sh - 1);
                        for (int x = 0; x < tile; x++)
                        {
                            int sx = Math.Clamp(x0 + x, 0, sw - 1);
                            long s = ((long)sy * sw + sx) * 3;
                            int o = y * tile + x;
                            buf[o] = d[s + 2] / 255f;             // R
                            buf[plane + o] = d[s + 1] / 255f;     // G
                            buf[2 * plane + o] = d[s] / 255f;     // B
                        }
                    }

                    float[] y_;
                    int ot;
                    IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
                    try
                    {
                        results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
                    }
                    catch (OnnxRuntimeException ex) when (adapter >= 0)
                    {
                        // The GPU failed in the middle of the work (driver reset, out of video memory...):
                        // never use this adapter again in this run and finish on the CPU.
                        MarkBroken(adapter, ex);
                        (session, adapter) = GetSession(file, new Waifu2xOptions { Model = options.Model, Device = Waifu2xDevice.Cpu });
                        inputName = session.InputMetadata.Keys.First();
                        results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
                    }
                    using (results)
                    {
                        var t = results.First().AsTensor<float>();
                        ot = t.Dimensions[3];
                        y_ = t.ToArray();
                    }

                    // Output tile row 0 corresponds to source row hi*InStep -> output row hi*OutStep.
                    int oy0 = hi * p.OutStep, ox0 = wi * p.OutStep;
                    int oplane = ot * ot;
                    Parallel.For(0, ot, ty =>
                    {
                        int oy = oy0 + ty;
                        if (oy >= outH) return;
                        for (int tx = 0; tx < ot; tx++)
                        {
                            int ox = ox0 + tx;
                            if (ox >= outW) break;
                            float wgt = weight[ty * ot + tx];
                            long o = (long)oy * outW + ox;
                            int ti = ty * ot + tx;
                            acc[o * 3] += y_[ti] * wgt;                 // R
                            acc[o * 3 + 1] += y_[oplane + ti] * wgt;    // G
                            acc[o * 3 + 2] += y_[2 * oplane + ti] * wgt; // B
                            wsum[o] += wgt;
                        }
                    });
                    tileDone();
                }
            }

            var result = new RgbImage(outW, outH);
            byte[] r = result.Data;
            Parallel.For(0, outH, y =>
            {
                for (int x = 0; x < outW; x++)
                {
                    long o = (long)y * outW + x;
                    float ws = wsum[o] > 0 ? wsum[o] : 1;
                    r[o * 3 + 2] = ToByte(acc[o * 3] / ws);
                    r[o * 3 + 1] = ToByte(acc[o * 3 + 1] / ws);
                    r[o * 3] = ToByte(acc[o * 3 + 2] / ws);
                }
            });
            return result;
        }

        /// <summary>Weights that fade towards the tile borders, so overlapping tiles blend without visible seams.</summary>
        private static float[] BlendWeights(int size, int blend)
        {
            var ramp = new float[size];
            for (int i = 0; i < size; i++)
            {
                int dEdge = Math.Min(i, size - 1 - i);
                ramp[i] = Math.Min(1f, (dEdge + 1f) / (blend + 1f));
            }
            var w = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    w[y * size + x] = ramp[y] * ramp[x];
            return w;
        }

        private static byte ToByte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return i < 0 ? (byte)0 : i > 255 ? (byte)255 : (byte)i;
        }

        // GPU adapters that failed (initialisation error, device hang, wrong results) are not used again.
        private static readonly HashSet<int> BrokenAdapters = new();
        private static readonly Dictionary<string, bool> Verified = new(); // "adapter|model file" -> GPU result == CPU result

        private void MarkBroken(int adapter, Exception ex)
        {
            lock (BrokenAdapters) BrokenAdapters.Add(adapter);
            string name = GpuAdapters.All.FirstOrDefault(a => a.Index == adapter)?.Display ?? $"GPU {adapter}";
            _warnings.Add($"{name} failed ({ShortError(ex)}) - continued on the CPU.");
            lock (_gate)
            {
                foreach (var key in _sessions.Keys.Where(k => k.EndsWith("|gpu" + adapter)).ToList())
                {
                    _sessions[key].Dispose();
                    _sessions.Remove(key);
                }
            }
        }

        internal static string ShortError(Exception ex)
        {
            string m = ex.Message.Split('\n')[0];
            int hr = m.IndexOf(" 887A", StringComparison.Ordinal);
            if (hr < 0) hr = m.IndexOf(" 8007", StringComparison.Ordinal);
            return hr >= 0 ? m.Substring(hr + 1).Trim() : m.Length > 160 ? m.Substring(0, 160) + "..." : m;
        }

        private static SessionOptions CpuOptions() => new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        internal static InferenceSession CreateGpuSession(string file, int adapter)
        {
            var opts = new SessionOptions
            {
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            opts.AppendExecutionProvider_DML(adapter);
            return new InferenceSession(file, opts);
        }

        /// <summary>
        /// Runs one small tile on the GPU session and on the CPU and compares: some drivers produce wrong results
        /// (e.g. zeros) without any error.
        /// </summary>
        internal static bool GpuMatchesCpu(InferenceSession gpu, InferenceSession cpu, int tile)
        {
            var input = new DenseTensor<float>(new[] { 1, 3, tile, tile });
            var rnd = new Random(7);
            for (int i = 0; i < input.Length; i++) input.SetValue(i, (float)(0.3 + 0.4 * rnd.NextDouble()));
            float[] Run(InferenceSession s)
            {
                using var r = s.Run(new[] { NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), input) });
                return r.First().AsTensor<float>().ToArray();
            }
            var a = Run(gpu);
            var b = Run(cpu);
            if (a.Length != b.Length) return false;
            float max = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float d = Math.Abs(a[i] - b[i]);
                if (float.IsNaN(d) || d > max) max = float.IsNaN(d) ? float.MaxValue : d;
            }
            return max < 2e-3f;
        }

        private InferenceSession GetCpuSession(string file)
        {
            string key = file + "|cpu";
            if (!_sessions.TryGetValue(key, out var s)) _sessions[key] = s = new InferenceSession(file, CpuOptions());
            return s;
        }

        /// <returns>The session and the GPU adapter index it runs on (-1 = CPU).</returns>
        private (InferenceSession session, int adapter) GetSession(string file, Waifu2xOptions options)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("waifu2x model not found: " + file);
            lock (_gate)
            {
                if (options.Device != Waifu2xDevice.Cpu)
                {
                    var candidates = options.Device == Waifu2xDevice.Gpu && options.GpuAdapter >= 0
                        ? GpuAdapters.All.Where(a => a.Index == options.GpuAdapter).ToList()
                        : GpuAdapters.HardwareByPreference();
                    if (options.Device == Waifu2xDevice.Gpu && options.GpuAdapter >= 0 && candidates.Count == 0)
                        _warnings.Add($"GPU {options.GpuAdapter} not found - using the CPU.");

                    foreach (var a in candidates)
                    {
                        lock (BrokenAdapters)
                            if (BrokenAdapters.Contains(a.Index)) continue;
                        string key = file + "|gpu" + a.Index;
                        if (_sessions.TryGetValue(key, out var cached))
                        {
                            LastDevice = a.Display;
                            return (cached, a.Index);
                        }
                        try
                        {
                            var gpu = CreateGpuSession(file, a.Index);
                            string vkey = a.Index + "|" + file;
                            bool ok;
                            lock (Verified)
                            {
                                if (!Verified.TryGetValue(vkey, out ok))
                                    Verified[vkey] = ok = GpuMatchesCpu(gpu, GetCpuSession(file), ValidTileSize(ModelOf(file), ScaleOf(file), 64));
                            }
                            if (!ok)
                            {
                                gpu.Dispose();
                                lock (BrokenAdapters) BrokenAdapters.Add(a.Index);
                                _warnings.Add($"{a.Display} gives wrong results with this model - not used.");
                                continue;
                            }
                            _sessions[key] = gpu;
                            LastDevice = a.Display;
                            return (gpu, a.Index);
                        }
                        catch (Exception ex)
                        {
                            lock (BrokenAdapters) BrokenAdapters.Add(a.Index);
                            _warnings.Add($"{a.Display} not usable ({ShortError(ex)}).");
                        }
                    }
                    if (candidates.Count > 0 || options.Device == Waifu2xDevice.Gpu)
                        _warnings.Add("Using the CPU.");
                }

                LastDevice = "CPU";
                return (GetCpuSession(file), -1);
            }
        }

        private static Waifu2xModel ModelOf(string file) =>
            file.Contains(Path.DirectorySeparatorChar + "cunet" + Path.DirectorySeparatorChar) ? Waifu2xModel.CunetArt : Waifu2xModel.SwinUnetArt;

        private static int ScaleOf(string file) => file.EndsWith("scale4x.onnx", StringComparison.OrdinalIgnoreCase) ? 4 : 2;

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var s in _sessions.Values) s.Dispose();
                _sessions.Clear();
            }
        }

        private static Waifu2xUpscaler _shared;

        /// <summary>One shared instance keeps the loaded models (and GPU memory) between images.</summary>
        public static Waifu2xUpscaler Shared => _shared ??= new Waifu2xUpscaler();
    }
}

