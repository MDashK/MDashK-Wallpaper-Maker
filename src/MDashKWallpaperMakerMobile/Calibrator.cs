using System;
using System.Collections.Generic;
using System.Linq;

namespace MDashKWallpaperMakerMobile
{
    internal sealed class CalibrationReport
    {
        public bool Adopted { get; init; }
        public string Message { get; init; }
        public FramingCalibration Result { get; init; }
        public int SeedCount { get; init; }
        public int ManualCount { get; init; }
        public int ApprovedCount { get; init; }
        public double CurrentScore { get; init; }
        public double FitScore { get; init; }
        public double CrossValidationScore { get; init; }
    }

    /// <summary>
    /// Refits the <see cref="FramingCalibration"/> parameters so the automatic framing reproduces the user's choices
    /// (the reference wallpapers + every adjusted / approved image in the history).
    /// Score = weighted mean overlap (IoU) between the automatic crop and the crop the user used.
    /// </summary>
    internal static class Calibrator
    {
        /// <summary>From this many user examples on, the headroom and zoom tendency are corrected.</summary>
        public const int MinSamplesForBias = 10;

        /// <summary>From this many user examples on, all parameters are refitted.</summary>
        public const int MinSamplesForFull = 30;

        private const double SeedWeight = 1.0, ManualWeight = 2.0, ApprovedWeight = 1.0;
        private const double MinImprovement = 0.003;
        private const double MaxCvLoss = 0.005;
        private const int Folds = 5;

        private sealed class Sample
        {
            public ImageAnalysis Analysis;
            public double[] Target; // crop window in original pixels: x0, y0, x1, y1
            public TargetSize Canvas; // wallpaper size the user's choice was made for
            public double Weight;
        }

        private sealed class Param
        {
            public string Name;
            public double Min, Max, Step;
            public Func<FramingCalibration, double> Get;
            public Action<FramingCalibration, double> Set;
        }

        private static readonly Param Headroom = new() { Name = "headroom", Min = -0.06, Max = 0.15, Step = 0.005, Get = c => c.HeadroomFraction, Set = (c, v) => c.HeadroomFraction = v };
        private static readonly Param Width = new() { Name = "subject width", Min = 0.8, Max = 3.0, Step = 0.05, Get = c => c.SubjectWidthFactor, Set = (c, v) => c.SubjectWidthFactor = v };
        private static readonly Param Typical = new() { Name = "typical zoom", Min = 1.0, Max = 1.8, Step = 0.02, Get = c => c.TypicalZoom, Set = (c, v) => c.TypicalZoom = v };
        private static readonly Param Weight = new() { Name = "typical weight", Min = 0.0, Max = 0.9, Step = 0.05, Get = c => c.TypicalZoomWeight, Set = (c, v) => c.TypicalZoomWeight = v };
        private static readonly Param Vertical = new() { Name = "vertical position", Min = 0.0, Max = 1.0, Step = 0.05, Get = c => c.VerticalPosition, Set = (c, v) => c.VerticalPosition = v };

        public static int UserSampleCount() => LearningStore.LoadHistory().Count;

        public static CalibrationReport Run(Action<string> log = null)
        {
            log ??= _ => { };
            var seed = LearningStore.LoadSeed();
            var history = LearningStore.LoadHistory();
            int manual = history.Count(r => r.Kind == LearningRecord.KindManual);
            int approved = history.Count(r => r.Kind == LearningRecord.KindApproved);
            int user = manual + approved;
            var current = FramingCalibration.Current;

            if (user < MinSamplesForBias)
            {
                return new CalibrationReport
                {
                    Message = $"Not enough examples yet: {user} of {MinSamplesForBias} needed (adjust or approve images in the preview and process them).",
                    SeedCount = seed.Count, ManualCount = manual, ApprovedCount = approved, Result = current,
                };
            }

            var samples = seed.Select(r => ToSample(r, SeedWeight))
                .Concat(history.Select(r => ToSample(r, r.Kind == LearningRecord.KindManual ? ManualWeight : ApprovedWeight)))
                .Where(s => s != null).ToList();

            bool full = user >= MinSamplesForFull;
            var active = full ? new[] { Headroom, Width, Typical, Weight, Vertical } : new[] { Headroom, Width, Vertical };
            string mode = full ? "full recalibration" : "tendency correction (zoom + position)";
            log($"Learning: {mode} with {seed.Count} reference + {manual} adjusted + {approved} approved images...");

            double currentScore = Score(current, samples);
            var fitted = Fit(current, samples, active);
            double fitScore = Score(fitted, samples);

            // Cross-validation: how well does "refitting" predict images it has not seen?
            double cvSum = 0, cvW = 0;
            for (int f = 0; f < Folds; f++)
            {
                var train = samples.Where((s, i) => i % Folds != f).ToList();
                var test = samples.Where((s, i) => i % Folds == f).ToList();
                if (test.Count == 0) continue;
                var c = Fit(current, train, active);
                double w = test.Sum(s => s.Weight);
                cvSum += Score(c, test) * w;
                cvW += w;
            }
            double cvScore = cvW > 0 ? cvSum / cvW : 0;

            bool adopt = fitScore > currentScore + MinImprovement && cvScore >= currentScore - MaxCvLoss;
            string numbers = $"match with your choices: current {currentScore:P1} -> new {fitScore:P1} (cross-validated {cvScore:P1})";

            if (!adopt)
            {
                return new CalibrationReport
                {
                    Message = $"Calibration kept - no reliable improvement ({numbers}).",
                    SeedCount = seed.Count, ManualCount = manual, ApprovedCount = approved, Result = current,
                    CurrentScore = currentScore, FitScore = fitScore, CrossValidationScore = cvScore,
                };
            }

            fitted.Source = $"learned ({mode}) from {seed.Count} reference + {manual} adjusted + {approved} approved images";
            fitted.CreatedUtc = DateTime.UtcNow;
            fitted.UserSamples = user;
            fitted.Score = fitScore;
            FramingCalibration.Save(fitted);
            return new CalibrationReport
            {
                Adopted = true,
                Message = $"New calibration adopted ({numbers}). {fitted.Describe()}",
                SeedCount = seed.Count, ManualCount = manual, ApprovedCount = approved, Result = fitted,
                CurrentScore = currentScore, FitScore = fitScore, CrossValidationScore = cvScore,
            };
        }

        /// <summary>Match score of a calibration against the reference set + history (for display).</summary>
        public static double CurrentScore()
        {
            var samples = LearningStore.LoadSeed().Select(r => ToSample(r, SeedWeight))
                .Concat(LearningStore.LoadHistory().Select(r => ToSample(r, r.Kind == LearningRecord.KindManual ? ManualWeight : ApprovedWeight)))
                .Where(s => s != null).ToList();
            return samples.Count == 0 ? 0 : Score(FramingCalibration.Current, samples);
        }

        private static Sample ToSample(LearningRecord r, double weight)
        {
            if (r.Final == null || r.Final.Scale <= 0) return null;
            var final = r.Final.ToPlan();
            return new Sample { Analysis = r.ToAnalysis(), Target = Window(r.Width, r.Height, final), Weight = weight, Canvas = final.Target };
        }

        /// <summary>Coordinate descent over a grid, started from the current and from the default values.</summary>
        private static FramingCalibration Fit(FramingCalibration start, List<Sample> samples, Param[] active)
        {
            FramingCalibration best = null;
            double bestScore = double.MinValue;
            foreach (var s0 in new[] { start, FramingCalibration.Default })
            {
                var c = s0.Clone();
                double score = Score(c, samples);
                for (int pass = 0; pass < 6; pass++)
                {
                    bool improved = false;
                    foreach (var p in active)
                    {
                        double keep = p.Get(c);
                        for (double v = p.Min; v <= p.Max + 1e-9; v += p.Step)
                        {
                            p.Set(c, v);
                            double sc = Score(c, samples);
                            if (sc > score + 1e-6) { score = sc; keep = v; improved = true; }
                        }
                        p.Set(c, Math.Round(keep, 4));
                    }
                    if (!improved) break;
                }
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        private static double Score(FramingCalibration c, List<Sample> samples)
        {
            double sum = 0, wsum = 0;
            foreach (var s in samples)
            {
                var plan = SmartFramer.AutoPlan(s.Analysis, c, s.Canvas);
                sum += Iou(Window(s.Analysis.Width, s.Analysis.Height, plan), s.Target) * s.Weight;
                wsum += s.Weight;
            }
            return wsum > 0 ? sum / wsum : 0;
        }

        /// <summary>Visible part of the original (in original pixels) for a plan.</summary>
        private static double[] Window(int ow, int oh, FramingPlan p)
        {
            // Phone layout: the image always covers the width; vertically it is cropped or surrounded by mirrors.
            double s = p.Scale;
            return new[]
            {
                -p.X / s,
                Math.Max(0, -p.Y) / s,
                (p.CanvasW - p.X) / s,
                Math.Min(oh * s, p.CanvasH - p.Y) / s,
            };
        }

        private static double Iou(double[] a, double[] b)
        {
            double ix = Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]));
            double iy = Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));
            double inter = ix * iy;
            double union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter;
            return union > 0 ? inter / union : 0;
        }
    }
}
