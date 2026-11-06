namespace DawnPlayer.App.Controls;

/// <summary>
/// Translates a ListView drag-drop into a core queue Move (plan §12 A1, invariant I11). WinUI
/// removes the dragged item and inserts it at the drop index, so <c>Move(from, to)</c> with its
/// RemoveAt+Insert reproduces exactly the order the user saw. Pure math — headless tested.
/// </summary>
public static class QueueReorderCalculator
{
    /// <summary>The move target for a drop, or null when it is a no-op (drop on itself) or the
    /// inputs are out of bounds. A stale drop index is clamped into range rather than rejected.</summary>
    public static int? TargetIndex(int from, int dropIndex, int count)
    {
        if (count <= 0) return null;
        if (from < 0 || from >= count) return null;
        if (dropIndex < 0) dropIndex = 0;
        if (dropIndex >= count) dropIndex = count - 1;
        return dropIndex == from ? null : dropIndex;
    }
}
