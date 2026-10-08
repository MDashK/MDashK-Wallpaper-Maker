using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>A detected object, in pixel coordinates of the image that was analysed.</summary>
    internal sealed class Detection
    {
        public string Label { get; init; }
        public RectangleF Box { get; init; }
        public float Score { get; init; }

        public override string ToString() =>
            $"{Label} {Score:F2} [{Box.Left:F0},{Box.Top:F0} {Box.Width:F0}x{Box.Height:F0}]";
    }

    /// <summary>
    /// YOLOv8 detector for the deepghs anime models (https://huggingface.co/deepghs).
    /// Mirrors imgutils: the image is stretched to the model input size, RGB / 255, CHW; output [4+classes, boxes] + NMS.
    /// </summary>
    internal sealed class YoloDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string[] _labels;
        private readonly int _inW, _inH;
        private readonly string _inputName;

        public float DefaultThreshold { get; }
        public string Name { get; }

        public YoloDetector(string modelDir, string name)
        {
            Name = name;
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(Path.Combine(modelDir, "model.onnx"), opts);
            _inputName = _session.InputMetadata.Keys.First();

            _labels = File.Exists(Path.Combine(modelDir, "labels.json"))
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(modelDir, "labels.json")))
                : new[] { name };

            DefaultThreshold = 0.35f;
            string thrFile = Path.Combine(modelDir, "threshold.json");
            if (File.Exists(thrFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(thrFile));
                if (doc.RootElement.TryGetProperty("threshold", out var t)) DefaultThreshold = (float)t.GetDouble();
            }

            _inW = _inH = 640;
            if (_session.ModelMetadata.CustomMetadataMap.TryGetValue("imgsz", out var imgsz))
            {
                var dims = JsonSerializer.Deserialize<int[]>(imgsz);
                if (dims?.Length == 2) { _inH = dims[0]; _inW = dims[1]; }
            }
        }

        public List<Detection> Detect(RgbImage img, float? threshold = null, float iouThreshold = 0.7f)
        {
            float thr = threshold ?? DefaultThreshold;
            var input = img.Resize(_inW, _inH, -0.5);
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inH, _inW });
            int plane = _inW * _inH;
            var buf = tensor.Buffer.Span;
            byte[] d = input.Data;
            for (int i = 0; i < plane; i++)
            {
                buf[i] = d[i * 3 + 2] / 255f;             // R
                buf[plane + i] = d[i * 3 + 1] / 255f;     // G
                buf[2 * plane + i] = d[i * 3] / 255f;     // B
            }

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var output = results.First().AsTensor<float>();
            int channels = output.Dimensions[1];
            int count = output.Dimensions[2];
            int classes = channels - 4;

            double sx = img.Width / (double)_inW, sy = img.Height / (double)_inH;
            var candidates = new List<Detection>();
            for (int i = 0; i < count; i++)
            {
                int best = 0;
                float bestScore = output[0, 4, i];
                for (int c = 1; c < classes; c++)
                {
                    float s = output[0, 4 + c, i];
                    if (s > bestScore) { bestScore = s; best = c; }
                }
                if (bestScore <= thr) continue;
                float cx = output[0, 0, i], cy = output[0, 1, i], w = output[0, 2, i], h = output[0, 3, i];
                var box = RectangleF.FromLTRB(
                    (float)((cx - w / 2) * sx), (float)((cy - h / 2) * sy),
                    (float)((cx + w / 2) * sx), (float)((cy + h / 2) * sy));
                box.Intersect(new RectangleF(0, 0, img.Width, img.Height));
                candidates.Add(new Detection { Label = best < _labels.Length ? _labels[best] : Name, Box = box, Score = bestScore });
            }
            return Nms(candidates, iouThreshold);
        }

        internal static List<Detection> Nms(List<Detection> dets, float iouThreshold)
        {
            var result = new List<Detection>();
            foreach (var d in dets.OrderByDescending(x => x.Score))
            {
                if (result.All(r => r.Label != d.Label || Iou(r.Box, d.Box) <= iouThreshold)) result.Add(d);
            }
            return result;
        }

        internal static float Iou(RectangleF a, RectangleF b)
        {
            var i = RectangleF.Intersect(a, b);
            if (i.IsEmpty) return 0;
            float inter = i.Width * i.Height;
            return inter / (a.Width * a.Height + b.Width * b.Height - inter);
        }

        public void Dispose() => _session.Dispose();
    }

    /// <summary>
    /// Text detector (PaddleOCR PP-OCRv4 DB detector, as used by imgutils.ocr). Returns axis-aligned text boxes.
    /// </summary>
    internal sealed class TextDetector : IDisposable
    {
        private const int Align = 64;
        private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
        private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

        private readonly InferenceSession _session;
        private readonly string _inputName;

        public TextDetector(string modelPath)
        {
            _session = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
            _inputName = _session.InputMetadata.Keys.First();
        }

        /// <param name="img">Image to analyse; should already be reduced to a moderate size (e.g. longest side ~1280).</param>
        public List<Detection> Detect(RgbImage img, float heatThreshold = 0.3f, float boxThreshold = 0.7f, float unclipRatio = 2.0f)
        {
            int w = img.Width, h = img.Height;
            int pw = (w + Align - 1) / Align * Align, ph = (h + Align - 1) / Align * Align;
            var tensor = new DenseTensor<float>(new[] { 1, 3, ph, pw });
            byte[] d = img.Data;
            for (int c = 0; c < 3; c++)
            {
                float pad = (0 - Mean[c]) / Std[c];
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        float v;
                        if (x < w && y < h) v = (d[(y * w + x) * 3 + (2 - c)] / 255f - Mean[c]) / Std[c];
                        else v = pad;
                        tensor[0, c, y, x] = v;
                    }
            }

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var heatTensor = results.First().AsTensor<float>();
            int hw = heatTensor.Dimensions[3];
            float[] heatData = heatTensor.ToArray();
            float Heat(int x, int y) => heatData[y * hw + x];

            // Connected components on the binarised heat map (8-connectivity).
            var labels = new int[w * h];
            var boxes = new List<Detection>();
            var stack = new Stack<int>();
            int next = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (labels[idx] != 0 || Heat(x, y) < heatThreshold) continue;
                    next++;
                    int minX = x, maxX = x, minY = y, maxY = y, n = 0;
                    double sum = 0;
                    labels[idx] = next;
                    stack.Push(idx);
                    while (stack.Count > 0)
                    {
                        int p = stack.Pop();
                        int px = p % w, py = p / w;
                        n++;
                        sum += Heat(px, py);
                        if (px < minX) minX = px; if (px > maxX) maxX = px;
                        if (py < minY) minY = py; if (py > maxY) maxY = py;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int qx = px + dx, qy = py + dy;
                                if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                                int q = qy * w + qx;
                                if (labels[q] != 0 || Heat(qx, qy) < heatThreshold) continue;
                                labels[q] = next;
                                stack.Push(q);
                            }
                    }
                    int bw = maxX - minX + 1, bh = maxY - minY + 1;
                    if (Math.Min(bw, bh) < 3) continue;
                    float score = (float)(sum / n);
                    if (score < boxThreshold) continue;
                    // "unclip": DB shrinks text regions, expand them back by area * ratio / perimeter.
                    float dist = bw * bh * unclipRatio / (2f * (bw + bh));
                    var r = RectangleF.FromLTRB(minX - dist, minY - dist, maxX + 1 + dist, maxY + 1 + dist);
                    r.Intersect(new RectangleF(0, 0, w, h));
                    if (Math.Min(r.Width, r.Height) < 5) continue;
                    boxes.Add(new Detection { Label = "text", Box = r, Score = score });
                }
            return boxes;
        }

        public void Dispose() => _session.Dispose();
    }
}
