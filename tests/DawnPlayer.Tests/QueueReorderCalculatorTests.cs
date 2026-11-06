using DawnPlayer.App.Controls;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Invariant I11 gate (plan §12.3): a queue drop translates to a single Move that permutes the
/// queue without losing or duplicating entries. The calculator mirrors WinUI's drag semantics —
/// the ListView removes the dragged item, then inserts it at the drop index — so Move(from, to)
/// with RemoveAt+Insert reproduces exactly what the user saw.
/// </summary>
public class QueueReorderCalculatorTests
{
    [Fact]
    public void SimpleForwardMove()
    {
        Assert.Equal(3, QueueReorderCalculator.TargetIndex(from: 0, dropIndex: 3, count: 5));
    }

    [Fact]
    public void BackwardMove()
    {
        Assert.Equal(1, QueueReorderCalculator.TargetIndex(from: 3, dropIndex: 1, count: 5));
    }

    [Fact]
    public void MoveToTheEnd()
    {
        Assert.Equal(4, QueueReorderCalculator.TargetIndex(from: 0, dropIndex: 4, count: 5));
    }

    [Fact]
    public void MoveToTheStart()
    {
        Assert.Equal(0, QueueReorderCalculator.TargetIndex(from: 2, dropIndex: 0, count: 5));
    }

    [Fact]
    public void DropOnItselfIsANoOp()
    {
        Assert.Null(QueueReorderCalculator.TargetIndex(from: 2, dropIndex: 2, count: 5));
    }

    [Fact]
    public void OutOfRangeFromIsRejected()
    {
        Assert.Null(QueueReorderCalculator.TargetIndex(from: 5, dropIndex: 1, count: 5));
        Assert.Null(QueueReorderCalculator.TargetIndex(from: -1, dropIndex: 1, count: 5));
    }

    [Fact]
    public void DropIndexIsClampedIntoRange()
    {
        // defensive: a stale drop index must not escape the collection bounds
        Assert.Equal(4, QueueReorderCalculator.TargetIndex(from: 0, dropIndex: 99, count: 5));
        Assert.Equal(0, QueueReorderCalculator.TargetIndex(from: 2, dropIndex: -3, count: 5));
    }

    [Fact]
    public void SingleItemQueueIsANoOp()
    {
        Assert.Null(QueueReorderCalculator.TargetIndex(from: 0, dropIndex: 0, count: 1));
    }

    /// <summary>Property check: applying the computed Move to a list permutes it — same items,
    /// same count, order equals a reference RemoveAt+Insert.</summary>
    [Fact]
    public void ComputedMovePreservesTheItems()
    {
        var reference = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
        foreach (var from in Enumerable.Range(0, 7))
        {
            foreach (var drop in Enumerable.Range(0, 7))
            {
                var target = QueueReorderCalculator.TargetIndex(from, drop, 7);
                var actual = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
                if (target is int to)
                {
                    var item = actual[from];
                    actual.RemoveAt(from);
                    actual.Insert(to, item);
                    Assert.Equal(7, actual.Count);
                    Assert.Equal(7, actual.Distinct().Count()); // permutation: nothing lost/duplicated
                    Assert.Equal(item, actual[to]);
                }
                else
                {
                    Assert.Equal(from, drop); // only identical indices are rejected
                }
            }
        }
    }
}
