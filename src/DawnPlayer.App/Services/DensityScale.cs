using DawnPlayer.Core.Persistence;
using DawnPlayer.App.Styles;

namespace DawnPlayer.App.Services;

/// <summary>
/// U3 list-density presets. One place maps the persisted mode name to concrete row metrics so
/// settings UI, persistence, and page code never disagree. Values feed the track-row templates
/// (library list + playlist rows); the cover grid keeps its own user-sized zoom.
/// </summary>
public static class DensityScale
{
    /// <summary>Metrics for one list row.</summary>
    public readonly record struct RowMetrics(double MinHeight, double Spacing, double CoverListSize);

    public static RowMetrics For(string mode) => DensityModes.Normalize(mode) switch
    {
        DensityModes.Compact => new RowMetrics(MinHeight: 24, Spacing: DesignTokenValues.Space.S, CoverListSize: 24),
        DensityModes.Comfortable => new RowMetrics(MinHeight: 36, Spacing: DesignTokenValues.Space.L, CoverListSize: 40),
        // Cozy == today's look (28px rows) so the preset ships as a no-op default.
        _ => new RowMetrics(MinHeight: 28, Spacing: DesignTokenValues.Space.M, CoverListSize: 32),
    };

    /// <summary>Resource keys the pages read via ThemeResource; AppServices seeds these into
    /// the application-level resource scope (see ApplyDensity there) so every realized row
    /// template picks the preset up.</summary>
    public static class ResourceKeys
    {
        public const string TrackRowMinHeight = "TrackRowMinHeight";
        public const string TrackRowSpacing = "TrackRowSpacing";
        public const string ListCoverSize = "ListCoverSize";
    }
}
