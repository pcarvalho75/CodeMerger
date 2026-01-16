using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Statistics;

namespace TransmutationLearning
{
    /// <summary>
    /// Represents a single cell's classification result from SingleR-style output
    /// </summary>
    public class CellClassification
    {
        public string Run { get; set; }
        public Dictionary<string, double> Scores { get; set; } = new Dictionary<string, double>();
        public string Labels { get; set; }
        public double DeltaNext { get; set; }
        public string PrunedLabels { get; set; }

        // Computed properties
        public double MaxScore => Scores.Count > 0 ? Scores.Values.Max() : 0;
        public bool IsPruned => PrunedLabels == "NA" || string.IsNullOrEmpty(PrunedLabels);

        // Ranking data for Distillation
        public List<(string CellType, double Score)> RankedScores { get; private set; } = new List<(string, double)>();
        public string SecondLabel { get; private set; }
        public double SecondScore { get; private set; }
        public int RankOfAssignedLabel { get; private set; }

        /// <summary>
        /// Computes full ranking from Scores dictionary. Call after Scores are populated.
        /// </summary>
        public void ComputeRankings()
        {
            if (Scores == null || Scores.Count == 0)
                return;

            // Sort by score descending
            RankedScores = Scores
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            // Second label info
            if (RankedScores.Count >= 2)
            {
                SecondLabel = RankedScores[1].CellType;
                SecondScore = RankedScores[1].Score;
            }
            else
            {
                SecondLabel = null;
                SecondScore = 0;
            }

            // Find where the assigned label ranks (1-based)
            RankOfAssignedLabel = 0;
            for (int i = 0; i < RankedScores.Count; i++)
            {
                if (RankedScores[i].CellType == Labels)
                {
                    RankOfAssignedLabel = i + 1;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Holds the complete dataset after loading and joining
    /// </summary>
    public class TransmutationDataset
    {
        // Raw data
        public List<CellClassification> Classifications { get; set; } = new List<CellClassification>();
        public Dictionary<string, Dictionary<string, double>> ProteinMatrix { get; set; } = new Dictionary<string, Dictionary<string, double>>();

        // Metadata
        public List<string> AllProteins { get; set; } = new List<string>();
        public List<string> AllRuns { get; set; } = new List<string>();
        public List<string> CellTypes { get; set; } = new List<string>();

        // Summary statistics
        public int TotalProteins => AllProteins.Count;
        public int TotalRuns => AllRuns.Count;
        public int TotalMatchedRuns => Classifications.Count(c => AllRuns.Contains(c.Run));
        public int UnmatchedClassifications => Classifications.Count(c => !AllRuns.Contains(c.Run));

        // Delta statistics
        public double MinDelta => Classifications.Count > 0 ? Classifications.Min(c => c.DeltaNext) : 0;
        public double MaxDelta => Classifications.Count > 0 ? Classifications.Max(c => c.DeltaNext) : 0;
        public double MedianDelta => Classifications.Count > 0 
            ? Statistics.Median(Classifications.Select(c => c.DeltaNext)) 
            : 0;

        /// <summary>
        /// Get cell type distribution
        /// </summary>
        public Dictionary<string, int> GetCellTypeCounts()
        {
            return Classifications
                .GroupBy(c => c.Labels)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Get delta values for histogram
        /// </summary>
        public List<double> GetDeltaValues()
        {
            return Classifications.Select(c => c.DeltaNext).ToList();
        }
    }

    /// <summary>
    /// Statistics for a single cell type
    /// </summary>
    public class CellTypeStatistics
    {
        public string CellType { get; set; }
        public int TotalCount { get; set; }
        public int RetainedCount { get; set; }
        public double MedianDelta { get; set; }
        public double MinDelta { get; set; }
        public double MaxDelta { get; set; }
        public double RetentionPercent => TotalCount > 0 ? (RetainedCount * 100.0 / TotalCount) : 0;
    }

    /// <summary>
    /// Result of confidence filtering with valid label set support
    /// </summary>
    public class FilteredDataset
    {
        public double DeltaThreshold { get; set; }
        public HashSet<string> ValidLabelSet { get; set; } = new HashSet<string>();

        // Three buckets
        public List<CellClassification> RetainedCells { get; set; } = new List<CellClassification>();
        public List<CellClassification> FilteredOutCells { get; set; } = new List<CellClassification>();  // Valid label, low delta
        public List<CellClassification> InvalidLabelCells { get; set; } = new List<CellClassification>(); // Invalid label (excluded regardless of delta)

        public List<CellTypeStatistics> CellTypeStats { get; set; } = new List<CellTypeStatistics>();

        // Counts
        public int TotalRetained => RetainedCells.Count;
        public int TotalFiltered => FilteredOutCells.Count;
        public int TotalInvalid => InvalidLabelCells.Count;
        public int TotalValidLabeled => TotalRetained + TotalFiltered;

        // Percentages
        public double RetentionPercent => TotalValidLabeled > 0
            ? (TotalRetained * 100.0 / TotalValidLabeled) : 0;
        public double InvalidPercent => (TotalValidLabeled + TotalInvalid) > 0
            ? (TotalInvalid * 100.0 / (TotalValidLabeled + TotalInvalid)) : 0;

        // Invalid label diagnostics
        public Dictionary<string, int> InvalidLabelDistribution => InvalidLabelCells
            .GroupBy(c => c.Labels)
            .ToDictionary(g => g.Key, g => g.Count());

        public Dictionary<string, double> InvalidLabelMedianDelta => InvalidLabelCells
            .GroupBy(c => c.Labels)
            .ToDictionary(g => g.Key, g => StatisticsHelper.Median(g.Select(c => c.DeltaNext)));
    }

    /// <summary>
    /// Statistical helper methods - uses MathNet.Numerics for core statistics
    /// </summary>
    public static class StatisticsHelper
    {
        /// <summary>
        /// Otsu's method for automatic threshold selection
        /// Finds threshold that minimizes intra-class variance
        /// </summary>
        public static double ComputeOtsuThreshold(List<double> values, int numBins = 100)
        {
            if (values == null || values.Count < 2)
                return 0;

            double min = values.Min();
            double max = values.Max();

            if (max - min < 1e-10)
                return min;

            // Create histogram
            double binWidth = (max - min) / numBins;
            int[] histogram = new int[numBins];

            foreach (var v in values)
            {
                int bin = Math.Min((int)((v - min) / binWidth), numBins - 1);
                histogram[bin]++;
            }

            int total = values.Count;
            double sum = 0;
            for (int i = 0; i < numBins; i++)
                sum += i * histogram[i];

            double sumB = 0;
            int wB = 0;
            double maxVariance = 0;
            int bestThreshold = 0;

            for (int t = 0; t < numBins; t++)
            {
                wB += histogram[t];
                if (wB == 0) continue;

                int wF = total - wB;
                if (wF == 0) break;

                sumB += t * histogram[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double variance = wB * wF * (mB - mF) * (mB - mF);

                if (variance > maxVariance)
                {
                    maxVariance = variance;
                    bestThreshold = t;
                }
            }

            return min + (bestThreshold + 0.5) * binWidth;
        }

        /// <summary>
        /// Compute histogram bins for visualization
        /// </summary>
        public static (double[] binEdges, int[] counts) ComputeHistogram(List<double> values, int numBins = 30)
        {
            if (values == null || values.Count == 0)
                return (new double[0], new int[0]);

            double min = values.Min();
            double max = values.Max();

            // Add small padding to include max value
            double range = max - min;
            if (range < 1e-10)
            {
                return (new double[] { min, min + 0.1 }, new int[] { values.Count });
            }

            double binWidth = range / numBins;
            double[] binEdges = new double[numBins + 1];
            int[] counts = new int[numBins];

            for (int i = 0; i <= numBins; i++)
                binEdges[i] = min + i * binWidth;

            foreach (var v in values)
            {
                int bin = Math.Min((int)((v - min) / binWidth), numBins - 1);
                counts[bin]++;
            }

            return (binEdges, counts);
        }

        /// <summary>
        /// Get median of a list using MathNet.Numerics
        /// </summary>
        public static double Median(IEnumerable<double> values)
        {
            var array = values.ToArray();
            if (array.Length == 0) return 0;
            return Statistics.Median(array);
        }

        /// <summary>
        /// Kruskal-Wallis H test - non-parametric test for differences between groups
        /// </summary>
        /// <param name="groups">List of groups, each containing values</param>
        /// <returns>H statistic and p-value</returns>
        public static (double H, double pValue) KruskalWallisTest(List<List<double>> groups)
        {
            if (groups == null || groups.Count < 2)
                return (0, 1);

            // Remove empty groups
            groups = groups.Where(g => g != null && g.Count > 0).ToList();
            if (groups.Count < 2)
                return (0, 1);

            // Combine all values with group index
            var allValues = new List<(double value, int groupIndex)>();
            for (int i = 0; i < groups.Count; i++)
            {
                foreach (var v in groups[i])
                {
                    allValues.Add((v, i));
                }
            }

            int N = allValues.Count;
            if (N < 3)
                return (0, 1);

            // Rank all values (handle ties with average rank)
            var ranked = allValues
                .Select((item, idx) => new { item.value, item.groupIndex, originalIndex = idx })
                .OrderBy(x => x.value)
                .ToList();

            double[] ranks = new double[N];
            int i2 = 0;
            while (i2 < N)
            {
                int j = i2;
                // Find all ties
                while (j < N - 1 && Math.Abs(ranked[j + 1].value - ranked[i2].value) < 1e-10)
                    j++;

                // Average rank for ties
                double avgRank = (i2 + j) / 2.0 + 1; // +1 because ranks are 1-based
                for (int k = i2; k <= j; k++)
                    ranks[ranked[k].originalIndex] = avgRank;

                i2 = j + 1;
            }

            // Calculate sum of ranks for each group
            double[] rankSums = new double[groups.Count];
            int[] groupSizes = new int[groups.Count];

            for (int i = 0; i < N; i++)
            {
                int groupIdx = allValues[i].groupIndex;
                rankSums[groupIdx] += ranks[i];
                groupSizes[groupIdx]++;
            }

            // Calculate H statistic
            double H = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groupSizes[i] > 0)
                {
                    H += (rankSums[i] * rankSums[i]) / groupSizes[i];
                }
            }
            H = (12.0 / (N * (N + 1))) * H - 3 * (N + 1);

            // Degrees of freedom
            int df = groups.Count - 1;

            // P-value from chi-squared distribution using MathNet.Numerics
            double pValue = 1.0 - ChiSquared.CDF(df, H);

            return (H, pValue);
        }
    }

    #region Feature Selection Models

    /// <summary>
    /// Per-cell-type metrics for a protein
    /// </summary>
    public class ProteinCellTypeMetrics
    {
        public string CellType { get; set; }
        public double DetectionRate { get; set; }      // 0-1, fraction of cells where detected
        public double MedianExpression { get; set; }   // Log2 intensity when detected
        public double MeanRank { get; set; }           // Average rank within this cell type
        public int CellCount { get; set; }             // Number of cells contributing
        public int DetectedCount { get; set; }         // Number of cells where detected
    }

    /// <summary>
    /// Complete statistics for a single protein
    /// </summary>
    public class ProteinStatistics
    {
        public string ProteinName { get; set; }

        // Per-cell-type metrics
        public Dictionary<string, ProteinCellTypeMetrics> CellTypeMetrics { get; set; }
            = new Dictionary<string, ProteinCellTypeMetrics>();

        // Global discrimination metrics
        public double KruskalWallisH { get; set; }
        public double KruskalWallisPValue { get; set; }

        // Specificity metrics (like delta.next but for proteins)
        public string BestCellType { get; set; }
        public double BestCellTypeExpression { get; set; }
        public double BestCellTypeDetection { get; set; }
        public string SecondBestCellType { get; set; }
        public double SecondBestCellTypeExpression { get; set; }
        public double SpecificityDelta { get; set; }   // Gap between best and second-best

        // Robustness flags
        public bool IsRobust { get; set; }             // Passes minimum cell count
        public int MinCellCount { get; set; }          // Smallest group contributing
        public double OverallDetectionRate { get; set; } // Detection across all cells

        // Selection state
        public bool IsSelected { get; set; }
        public bool PassesFilter { get; set; }

        // Display helpers
        public string PValueFormatted => KruskalWallisPValue < 0.001
            ? KruskalWallisPValue.ToString("0.0e0")
            : KruskalWallisPValue.ToString("F4");

        public string RobustFlag => IsRobust ? "✓" : "⚠️";

        public double BestDetectionPercent => BestCellTypeDetection * 100;
    }

    /// <summary>
    /// Criteria for selecting marker proteins
    /// </summary>
    public class FeatureSelectionCriteria
    {
        public double MaxPValue { get; set; } = 0.01;
        public double MinDetectionRate { get; set; } = 0.30;      // In best cell type
        public double MinSpecificityDelta { get; set; } = 0.50;
        public double MinCellFraction { get; set; } = 0.30;       // Min 30% of cells per type
        public double MaxMissingRate { get; set; } = 0.70;        // Exclude if >70% missing overall
        public bool RequireRobustness { get; set; } = true;
    }

    /// <summary>
    /// Result of feature selection process
    /// </summary>
    public class FeatureSelectionResult
    {
        public List<ProteinStatistics> AllProteinStats { get; set; } = new List<ProteinStatistics>();
        public List<ProteinStatistics> SelectedMarkers { get; set; } = new List<ProteinStatistics>();
        public FeatureSelectionCriteria Criteria { get; set; }

        // Grouped view by best cell type
        public Dictionary<string, List<ProteinStatistics>> MarkersByCellType { get; set; }
            = new Dictionary<string, List<ProteinStatistics>>();

        // Summary stats
        public int TotalProteinsAnalyzed => AllProteinStats.Count;
        public int TotalMarkersSelected => SelectedMarkers.Count;
        public double MedianSpecificityDelta => SelectedMarkers.Count > 0
            ? StatisticsHelper.Median(SelectedMarkers.Select(m => m.SpecificityDelta))
            : 0;

        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }

    #endregion

    #region Proteomics Reference Export Models

    /// <summary>
    /// Metadata for the proteomics reference file
    /// </summary>
    public class ProteomicsReferenceMetadata
    {
        public string Version { get; set; } = "1.0";
        public string GeneratedBy { get; set; } = "TransmutationLearning";
        public DateTime GeneratedDate { get; set; } = DateTime.Now;
        public string SourceDataset { get; set; }
        public string ClassificationSource { get; set; }
        public double DeltaThreshold { get; set; }
        public int TotalCellsRetained { get; set; }
        public int FeatureCount { get; set; }
        public List<string> CellTypes { get; set; } = new List<string>();
        public string SelectionCriteria { get; set; }
    }

    /// <summary>
    /// Complete proteomics reference for export to .pref file
    /// </summary>
    public class ProteomicsReference
    {
        public ProteomicsReferenceMetadata Metadata { get; set; } = new ProteomicsReferenceMetadata();

        // Expression matrix: Protein -> CellType -> MedianLog2Expression
        public Dictionary<string, Dictionary<string, double>> ExpressionMatrix { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();

        // Detection matrix: Protein -> CellType -> DetectionRate (0-1)
        public Dictionary<string, Dictionary<string, double>> DetectionMatrix { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();

        // Marker statistics for export
        public List<ProteinStatistics> MarkerStats { get; set; } = new List<ProteinStatistics>();

        // Helper to get cell types in consistent order
        public List<string> GetOrderedCellTypes() => Metadata.CellTypes.OrderBy(c => c).ToList();

        // Helper to get proteins in order (grouped by best cell type, then by specificity)
        public List<string> GetOrderedProteins()
        {
            return MarkerStats
                .OrderBy(m => m.BestCellType)
                .ThenByDescending(m => m.SpecificityDelta)
                .Select(m => m.ProteinName)
                .ToList();
        }
    }

    #endregion
}
