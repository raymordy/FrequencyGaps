using System;
using System.Collections.Generic;
using System.Linq;

// ─── Domain Models ────────────────────────────────────────────────────────────

public class Transmitter
{
    public string Name { get; set; }
    public float MinFrequency { get; set; }
    public float MaxFrequency { get; set; }
}

public class Receiver
{
    public string Name { get; set; }
    public float MinFrequency { get; set; }
    public float MaxFrequency { get; set; }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public class FrequencyGap
{
    /// <summary>The start of the uncovered range.</summary>
    public float GapMin { get; set; }

    /// <summary>The end of the uncovered range.</summary>
    public float GapMax { get; set; }

    /// <summary>Human-readable description of why/where the gap exists.</summary>
    public string Description { get; set; }

    public override string ToString() =>
        $"[{GapMin:F3} – {GapMax:F3}] {Description}";
}

public class TransmitterCoverageResult
{
    public string TransmitterName { get; set; }
    public float MinFrequency { get; set; }
    public float MaxFrequency { get; set; }

    /// <summary>
    /// True when at least one receiver covers some part of the transmitter range.
    /// </summary>
    public bool HasPartialCoverage { get; set; }

    /// <summary>
    /// True when the transmitter range is entirely covered by the union of receivers.
    /// </summary>
    public bool IsFullyCovered { get; set; }

    /// <summary>All uncovered sub-ranges within the transmitter band.</summary>
    public List<FrequencyGap> DetectionGaps { get; set; } = new();
}

// ─── Service ──────────────────────────────────────────────────────────────────

public static class FrequencyGapDetector
{
    /// <summary>
    /// Analyses how well a collection of <paramref name="receivers"/> covers
    /// the frequency band of <paramref name="transmitter"/> and returns a
    /// <see cref="TransmitterCoverageResult"/> describing every gap.
    /// </summary>
    public static TransmitterCoverageResult FindGaps(
        Transmitter transmitter,
        IEnumerable<Receiver> receivers)
    {
        if (transmitter == null) throw new ArgumentNullException(nameof(transmitter));
        if (transmitter.MinFrequency >= transmitter.MaxFrequency)
            throw new ArgumentException(
                "Transmitter MinFrequency must be less than MaxFrequency.",
                nameof(transmitter));

        var result = new TransmitterCoverageResult
        {
            TransmitterName = transmitter.Name,
            MinFrequency    = transmitter.MinFrequency,
            MaxFrequency    = transmitter.MaxFrequency
        };

        // ── 1. Guard: no receivers at all ────────────────────────────────────
        var receiverList = (receivers ?? Enumerable.Empty<Receiver>()).ToList();
        if (receiverList.Count == 0)
        {
            result.DetectionGaps.Add(new FrequencyGap
            {
                GapMin      = transmitter.MinFrequency,
                GapMax      = transmitter.MaxFrequency,
                Description = "No receivers defined – entire transmitter band is uncovered."
            });
            result.IsFullyCovered     = false;
            result.HasPartialCoverage = false;
            return result;
        }

        // ── 2. Validate receivers (skip degenerate entries) ──────────────────
        var validReceivers = receiverList
            .Where(r => r != null && r.MinFrequency < r.MaxFrequency)
            .ToList();

        // ── 3. Keep only receivers that actually overlap the transmitter band ─
        //      Overlap condition: r.Min < tx.Max  AND  r.Max > tx.Min
        var overlapping = validReceivers
            .Where(r => r.MinFrequency < transmitter.MaxFrequency &&
                        r.MaxFrequency > transmitter.MinFrequency)
            .OrderBy(r => r.MinFrequency)
            .ThenBy(r => r.MaxFrequency)
            .ToList();

        result.HasPartialCoverage = overlapping.Count > 0;

        // ── 4. Merge overlapping / adjacent receiver intervals ────────────────
        //      Clamp each interval to the transmitter band first so the merge
        //      only operates within the range we care about.
        var merged = MergeIntervals(
            overlapping.Select(r => (
                Min: Math.Max(r.MinFrequency, transmitter.MinFrequency),
                Max: Math.Min(r.MaxFrequency, transmitter.MaxFrequency)
            )));

        // ── 5. Walk the merged coverage and find gaps ─────────────────────────
        float cursor = transmitter.MinFrequency;

        foreach (var (covMin, covMax) in merged)
        {
            if (cursor < covMin)
            {
                // Gap before this covered segment
                result.DetectionGaps.Add(BuildGap(cursor, covMin, transmitter, overlapping));
            }

            cursor = covMax; // advance past covered segment
        }

        // Gap after the last covered segment (or the whole band if no overlap)
        if (cursor < transmitter.MaxFrequency)
        {
            result.DetectionGaps.Add(BuildGap(cursor, transmitter.MaxFrequency, transmitter, overlapping));
        }

        result.IsFullyCovered = result.DetectionGaps.Count == 0;
        return result;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Merges a sorted sequence of (Min, Max) intervals into non-overlapping
    /// contiguous segments.
    /// </summary>
    private static List<(float Min, float Max)> MergeIntervals(
        IEnumerable<(float Min, float Max)> intervals)
    {
        var sorted = intervals.OrderBy(i => i.Min).ThenBy(i => i.Max).ToList();
        var merged = new List<(float Min, float Max)>();

        foreach (var interval in sorted)
        {
            if (merged.Count == 0 || interval.Min > merged[^1].Max)
            {
                merged.Add(interval);
            }
            else
            {
                // Extend the last segment
                var last = merged[^1];
                merged[^1] = (last.Min, Math.Max(last.Max, interval.Max));
            }
        }

        return merged;
    }

    /// <summary>
    /// Builds a <see cref="FrequencyGap"/> with an appropriate description
    /// based on where the gap falls relative to receiver coverage.
    /// </summary>
    private static FrequencyGap BuildGap(
        float gapMin, float gapMax,
        Transmitter tx,
        List<Receiver> overlapping)
    {
        string description;
        bool gapAtStart = Math.Abs(gapMin - tx.MinFrequency) < float.Epsilon;
        bool gapAtEnd   = Math.Abs(gapMax - tx.MaxFrequency) < float.Epsilon;

        if (overlapping.Count == 0)
        {
            description = "No receivers overlap the transmitter band.";
        }
        else if (gapAtStart && gapAtEnd)
        {
            description = "Entire transmitter band sits outside all receiver ranges.";
        }
        else if (gapAtStart)
        {
            // Receivers start somewhere above the transmitter minimum
            var firstReceiver = overlapping.OrderBy(r => r.MinFrequency).First();
            description = $"Transmitter band begins below the lowest receiver " +
                          $"({firstReceiver.Name} starts at {firstReceiver.MinFrequency:F3}).";
        }
        else if (gapAtEnd)
        {
            // Receivers end somewhere below the transmitter maximum
            var lastReceiver = overlapping.OrderByDescending(r => r.MaxFrequency).First();
            description = $"Transmitter band extends above the highest receiver " +
                          $"({lastReceiver.Name} ends at {lastReceiver.MaxFrequency:F3}).";
        }
        else
        {
            // Gap sits between two receiver ranges
            var below = overlapping
                .Where(r => r.MaxFrequency <= gapMin + float.Epsilon)
                .OrderByDescending(r => r.MaxFrequency)
                .FirstOrDefault();

            var above = overlapping
                .Where(r => r.MinFrequency >= gapMax - float.Epsilon)
                .OrderBy(r => r.MinFrequency)
                .FirstOrDefault();

            string belowName = below?.Name ?? "N/A";
            string aboveName = above?.Name ?? "N/A";
            description = $"Gap between receiver '{belowName}' (ends {below?.MaxFrequency:F3}) " +
                          $"and receiver '{aboveName}' (starts {above?.MinFrequency:F3}).";
        }

        return new FrequencyGap
        {
            GapMin      = gapMin,
            GapMax      = gapMax,
            Description = description
        };
    }
}

// ─── Demo ─────────────────────────────────────────────────────────────────────

class Program
{
    static void Main()
    {
        var transmitter = new Transmitter
        {
            Name         = "TX-Alpha",
            MinFrequency = 100f,
            MaxFrequency = 500f
        };

        var transmitters = new List<Transmitter>
        {
            new(){Name = "Transmitter 1 (UNDER) - ", MinFrequency = 10f, MaxFrequency = 45f}, //Under
            new(){Name = "Transmitter 2 (OVER) - ", MinFrequency = 750f, MaxFrequency = 800f},
            new(){Name = "Transmitter 3 (PART UNDER) - ", MinFrequency = 20f, MaxFrequency = 60f},
            new(){Name = "Transmitter 4 (PART OVER) - ", MinFrequency = 690f, MaxFrequency = 750f},
            new(){Name = "Transmitter 5 (IN BETWEEN) - ", MinFrequency = 100f, MaxFrequency = 200f},
            new(){Name = "Transmitter 6 (Covered) - ", MinFrequency = 550f, MaxFrequency = 580f}
        };


        var receivers = new List<Receiver>
        {
            new() { Name = "RX-1", MinFrequency = 50f,  MaxFrequency = 99f },  // overlaps lower edge
            new() { Name = "RX-2", MinFrequency = 501f, MaxFrequency = 600f },  // sits in the middle
            new() { Name = "RX-3", MinFrequency = 680f, MaxFrequency = 700f },  // overlaps RX-2
            // 400–500 deliberately uncovered (upper-edge gap)
        };

        var result = FrequencyGapDetector.FindGaps(transmitter, receivers);

        Console.WriteLine($"Transmitter : {result.TransmitterName}");
        Console.WriteLine($"Band        : {result.MinFrequency} – {result.MaxFrequency} MHz");
        Console.WriteLine($"Fully Covered   : {result.IsFullyCovered}");
        Console.WriteLine($"Partial Coverage: {result.HasPartialCoverage}");
        Console.WriteLine($"Gaps ({result.DetectionGaps.Count}):");

        foreach (var gap in result.DetectionGaps)
            Console.WriteLine($"  {gap}");

        Console.WriteLine("----------");
        foreach(var transmitter1 in transmitters)
        {
            result = FrequencyGapDetector.FindGaps(transmitter1, receivers);

            Console.WriteLine($"Transmitter : {result.TransmitterName}");
            Console.WriteLine($"Band        : {result.MinFrequency} – {result.MaxFrequency} MHz");
            Console.WriteLine($"Fully Covered   : {result.IsFullyCovered}");
            Console.WriteLine($"Partial Coverage: {result.HasPartialCoverage}");
            Console.WriteLine($"Gaps ({result.DetectionGaps.Count}):");

            foreach (var gap in result.DetectionGaps)
                Console.WriteLine($"  {gap}");
        }
    }
}