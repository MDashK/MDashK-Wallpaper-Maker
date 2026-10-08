using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>One learning example: what the AI detected, what it proposed and what the user finally used.</summary>
    internal sealed class LearningRecord
    {
        public const string KindManual = "manual";      // user adjusted the framing
        public const string KindApproved = "approved";  // user looked at the automatic framing in the preview and kept it
        public const string KindSeed = "seed";          // built-in reference wallpapers (original calibration)

        [JsonPropertyName("v")] public int Version { get; set; } = 1;
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("time")] public DateTime TimeUtc { get; set; }
        [JsonPropertyName("file")] public string File { get; set; }
        [JsonPropertyName("w")] public int Width { get; set; }
        [JsonPropertyName("h")] public int Height { get; set; }
        [JsonPropertyName("det")] public List<DetectionDto> Detections { get; set; } = new();
        [JsonPropertyName("auto")] public PlanDto Auto { get; set; }
        [JsonPropertyName("final")] public PlanDto Final { get; set; }

        /// <summary>Identifies the same image across runs (re-processing replaces the older record).</summary>
        [JsonIgnore] public string Key => $"{File}|{Width}x{Height}";

        public ImageAnalysis ToAnalysis() => new()
        {
            Width = Width,
            Height = Height,
            Detections = Detections.Select(d => new Detection { Label = d.Label, Score = d.Score, Box = new RectangleF(d.X, d.Y, d.W, d.H) }).ToList(),
        };

        public static LearningRecord Create(string kind, string fileName, ImageAnalysis a, FramingPlan auto, FramingPlan final) => new()
        {
            Kind = kind,
            TimeUtc = DateTime.UtcNow,
            File = fileName,
            Width = a.Width,
            Height = a.Height,
            Detections = a.Detections.Select(d => new DetectionDto
            {
                Label = d.Label,
                Score = (float)Math.Round(d.Score, 3),
                X = (float)Math.Round(d.Box.X, 1),
                Y = (float)Math.Round(d.Box.Y, 1),
                W = (float)Math.Round(d.Box.Width, 1),
                H = (float)Math.Round(d.Box.Height, 1),
            }).ToList(),
            Auto = PlanDto.From(auto),
            Final = PlanDto.From(final),
        };
    }

    internal sealed class DetectionDto
    {
        [JsonPropertyName("l")] public string Label { get; set; }
        [JsonPropertyName("s")] public float Score { get; set; }
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("w")] public float W { get; set; }
        [JsonPropertyName("h")] public float H { get; set; }
    }

    internal sealed class PlanDto
    {
        [JsonPropertyName("scale")] public double Scale { get; set; }
        [JsonPropertyName("cw")] public int CanvasW { get; set; } = 1080;
        [JsonPropertyName("ch")] public int CanvasH { get; set; } = 2400;
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }

        public static PlanDto From(FramingPlan p) => p == null ? null : new PlanDto
        {
            Scale = Math.Round(p.Scale, 6),
            CanvasW = p.CanvasW,
            CanvasH = p.CanvasH,
            X = Math.Round(p.X, 2),
            Y = Math.Round(p.Y, 2),
        };

        public FramingPlan ToPlan() => new() { Scale = Scale, X = X, Y = Y, CanvasW = CanvasW > 0 ? CanvasW : 1080, CanvasH = CanvasH > 0 ? CanvasH : 2400 };
    }

    /// <summary>
    /// Learning history: config\history.jsonl (one JSON record per line, user examples) plus the built-in
    /// reference examples the original calibration was made from.
    /// </summary>
    internal static class LearningStore
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static void Append(LearningRecord record)
        {
            string line = JsonSerializer.Serialize(record, Json);
            lock (Gate) File.AppendAllText(AppPaths.HistoryFile, line + Environment.NewLine, Encoding.UTF8);
        }

        /// <summary>User examples; when an image was recorded several times only the latest record counts.</summary>
        public static List<LearningRecord> LoadHistory()
        {
            var list = new List<LearningRecord>();
            lock (Gate)
            {
                if (!File.Exists(AppPaths.HistoryFile)) return list;
                foreach (var line in File.ReadLines(AppPaths.HistoryFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize<LearningRecord>(line, Json);
                        if (r?.Final != null && r.Width > 0 && r.Height > 0) list.Add(r);
                    }
                    catch
                    {
                        // skip a damaged line
                    }
                }
            }
            return list.GroupBy(r => r.Key).Select(g => g.OrderBy(r => r.TimeUtc).Last()).ToList();
        }

        public static List<LearningRecord> LoadSeed()
        {
            var list = new List<LearningRecord>();
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MDashKWallpaperMakerMobile.seed_history.jsonl");
            if (s == null) return list;
            using var reader = new StreamReader(s, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var r = JsonSerializer.Deserialize<LearningRecord>(line, Json);
                if (r != null) list.Add(r);
            }
            return list;
        }
    }
}
