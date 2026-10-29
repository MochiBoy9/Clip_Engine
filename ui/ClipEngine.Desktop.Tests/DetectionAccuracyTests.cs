using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class DetectionAccuracyTests
{
    private static ClipEvent E(string type, double time) => new() { Type = type, Time = time };

    [Fact]
    public void MatchesEachMarkToTheClosestFindOfItsKind()
    {
        var marks = new[] { E("kill", 3), E("kill", 4.5), E("death", 10), E("revive", 20) };
        var found = new[] { E("kill", 4.2), E("kill", 3.4), E("death", 14), E("kill", 30), E("revive", 21.5) };
        var result = DetectionAccuracy.Compare(marks, found);

        Assert.Equal(3, result.Caught.Count);
        Assert.Equal([10.0], result.Missed.Select(e => e.Time));
        Assert.Equal([14.0, 30.0], result.Extra.Select(e => e.Time));
        Assert.Equal("Caught 3 of 4 (75%), 2 false alarms.", result.Summary);
        Assert.Equal([("kill", 2, 2, 1), ("death", 1, 0, 1), ("revive", 1, 1, 0)], result.ByType);
    }

    [Fact]
    public void AFindCountsOnceAndOnlyForItsOwnKind()
    {
        var result = DetectionAccuracy.Compare([E("kill", 5), E("kill", 5.5)], [E("kill", 5.2), E("assist", 5.4)]);
        Assert.Single(result.Caught);
        Assert.Single(result.Missed);
        Assert.Equal("assist", Assert.Single(result.Extra).Type);
    }

    [Fact]
    public void SaysSoWhenNothingIsMarked()
    {
        Assert.Equal("Nothing marked and nothing found.", DetectionAccuracy.Compare([], []).Summary);
        Assert.Equal("Nothing marked, 1 false alarm.", DetectionAccuracy.Compare([], [E("kill", 1)]).Summary);
    }

    [Fact]
    public void ReadsTheScanCommandsLines()
    {
        static EngineEvent? Parse(string message) =>
            EngineLogParser.ParseEvent(EngineLogParser.ParseLine($"2026-10-29 04:00:00.000 INFO  {message}")!);

        Assert.Equal(new ScanFound("kill", 12.25, "ELIMINATED Mochi"), Parse("Scan found kill at 12.25: ELIMINATED Mochi"));
        Assert.Equal(new ScanProgress(45), Parse("Scan progress 45%"));
        Assert.Equal(new ScanDone(3), Parse("Scan done: 3 moments"));
    }

    [Fact]
    public void KeepsMarksInTheClipsJson()
    {
        var metadata = ClipMetadataFile.Parse("""{"game":"The Finals","events":[],"labels":[{"type":"kill","time":4.5,"text":""}]}""");
        Assert.Equal(4.5, Assert.Single(metadata!.Labels!).Time);
        Assert.Null(ClipMetadataFile.Parse("""{"game":"The Finals","events":[]}""")!.Labels);
    }
}
