using ClipEngine.Desktop.Services.Editing;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class EditTimelineTests
{
    private const string Clip = @"C:\Videos\Clip_Engine\The Finals\The Finals_2026-11-04.mp4";

    private static EditTimeline Thirty() => EditTimeline.Whole(Clip, 30);

    [Fact]
    public void StartsAsTheWholeClip()
    {
        var t = Thirty();
        Assert.Equal([new EditSegment(Clip, 0, 30)], t.Segments);
        Assert.Equal(30, t.Duration);
        Assert.False(t.IsEdited);
        Assert.False(t.CanUndo);
    }

    [Fact]
    public void SplitCutsThePartUnderThePlayhead()
    {
        var t = Thirty();
        Assert.True(t.Split(12, out var after));
        Assert.Equal(1, after);
        Assert.Equal([new EditSegment(Clip, 0, 12), new EditSegment(Clip, 12, 30)], t.Segments);
        Assert.Equal(30, t.Duration);
        Assert.Equal([12.0], t.Cuts());
        Assert.True(t.IsEdited);
    }

    [Fact]
    public void SplitTooCloseToAnEndDoesNothing()
    {
        var t = Thirty();
        Assert.False(t.Split(0.1, out _));
        Assert.False(t.Split(29.9, out _));
        t.Split(10, out _);
        Assert.False(t.Split(10.1, out _));
        Assert.Equal(2, t.Segments.Count);
    }

    [Fact]
    public void RemovingAPartClosesTheGap()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Split(20, out _);
        Assert.True(t.Remove(1));
        Assert.Equal([new EditSegment(Clip, 0, 10), new EditSegment(Clip, 20, 30)], t.Segments);
        Assert.Equal(20, t.Duration);
        // Five seconds into what's left of the edit after the cut is 25 s into the clip.
        Assert.Equal((1, 25.0), t.Locate(15));
    }

    [Fact]
    public void TheLastPartCantBeRemoved()
    {
        var t = Thirty();
        Assert.False(t.Remove(0));
        Assert.Single(t.Segments);
    }

    [Fact]
    public void LocateGivesACutToThePartAfterIt()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Move(1, 0);  // 10–30 first, then 0–10
        Assert.Equal((0, 10.0), t.Locate(0));
        Assert.Equal((1, 0.0), t.Locate(20));
        Assert.Equal((1, 10.0), t.Locate(30));
        Assert.Equal((1, 10.0), t.Locate(99));
    }

    [Fact]
    public void ResizeStaysInsideTheClipAndKeepsAPartLongEnough()
    {
        var t = Thirty();
        t.Split(10, out _);
        Assert.True(t.Resize(0, SegmentEdge.Start, 4));
        Assert.Equal(new EditSegment(Clip, 4, 10), t.Segments[0]);
        Assert.True(t.Resize(0, SegmentEdge.Start, 50));
        Assert.Equal(10 - EditTimeline.MinSegment, t.Segments[0].From, 6);
        Assert.True(t.Resize(1, SegmentEdge.End, 20));
        Assert.True(t.Resize(1, SegmentEdge.End, 99));
        Assert.Equal(30, t.Segments[1].To);
        Assert.False(t.Resize(1, SegmentEdge.End, 31));  // already at the end
        Assert.True(t.Resize(1, SegmentEdge.Start, -5));
        Assert.Equal(0, t.Segments[1].From);
    }

    [Fact]
    public void MarkersFollowTheirMomentThroughTheEdit()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Split(20, out _);
        t.Remove(1);
        Assert.Equal([5.0], t.EditTimesOf(Clip, 5));
        Assert.Empty(t.EditTimesOf(Clip, 15));  // cut
        Assert.Equal([15.0], t.EditTimesOf(Clip, 25));
        t.Duplicate(1);
        Assert.Equal([15.0, 25.0], t.EditTimesOf(Clip, 25));
        Assert.Empty(t.EditTimesOf(@"C:\other.mp4", 5));
    }

    [Fact]
    public void KeepOnlyCutsEverythingOutsideTheRange()
    {
        var t = Thirty();
        t.Split(10, out _);
        Assert.True(t.KeepOnly(5, 18));
        Assert.Equal([new EditSegment(Clip, 5, 10), new EditSegment(Clip, 10, 18)], t.Segments);
        Assert.False(t.KeepOnly(3, 3.1));
    }

    [Fact]
    public void UndoAndRedoWalkThroughChanges()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Remove(0);
        Assert.True(t.Undo());
        Assert.Equal(2, t.Segments.Count);
        Assert.True(t.Undo());
        Assert.Single(t.Segments);
        Assert.False(t.Undo());
        Assert.True(t.Redo());
        Assert.True(t.Redo());
        Assert.Equal([new EditSegment(Clip, 10, 30)], t.Segments);
        Assert.False(t.Redo());
    }

    [Fact]
    public void ANewChangeClearsRedo()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Undo();
        t.Split(20, out _);
        Assert.False(t.CanRedo);
        Assert.Equal(20, t.Segments[0].To);
    }

    [Fact]
    public void ADragUndoesAsOneStep()
    {
        var t = Thirty();
        t.Split(10, out _);
        var changes = 0;
        t.Changed += () => changes++;
        t.BeginGesture();
        for (var at = 10.5; at <= 14; at += 0.5) t.Resize(0, SegmentEdge.End, at);
        t.EndGesture();
        Assert.Equal(14, t.Segments[0].To);
        Assert.True(changes > 1);
        Assert.True(t.Undo());
        Assert.Equal(10, t.Segments[0].To);
        Assert.True(t.Undo());
        Assert.Single(t.Segments);
    }

    [Fact]
    public void ADragThatEndsWhereItStartedLeavesNoUndo()
    {
        var t = Thirty();
        t.BeginGesture();
        t.Resize(0, SegmentEdge.End, 20);
        t.Resize(0, SegmentEdge.End, 30);
        t.EndGesture();
        Assert.False(t.CanUndo);
    }

    [Fact]
    public void ResetPutsTheWholeClipBackAndCanBeUndone()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Remove(0);
        Assert.True(t.Reset());
        Assert.False(t.IsEdited);
        Assert.False(t.Reset());
        t.Undo();
        Assert.Equal([new EditSegment(Clip, 10, 30)], t.Segments);
    }

    [Fact]
    public void SegmentsOutsideTheirSourceAreClampedOrDropped()
    {
        var t = new EditTimeline([new EditSource(Clip, 30)],
            [new EditSegment(Clip, -3, 5), new EditSegment(Clip, 28, 40), new EditSegment(@"C:\gone.mp4", 0, 5),
             new EditSegment(Clip, 12, 12.1)]);
        Assert.Equal([new EditSegment(Clip, 0, 5), new EditSegment(Clip, 28, 30)], t.Segments);
    }

    [Fact]
    public void AProjectRoundTripsAndOnlyFitsTheSameClip()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Remove(0);
        var folder = Path.Combine(Path.GetTempPath(), "clip-engine-tests", Guid.NewGuid().ToString("N"));
        var file = EditProject.PathFor(folder, Clip);
        try
        {
            EditProject.From(t, DateTimeOffset.Now).Save(file);
            var loaded = EditProject.Load(file);
            Assert.NotNull(loaded);
            Assert.Equal(t.Segments, loaded!.ToTimeline(Clip, 30)!.Segments);
            Assert.Equal(t.Segments, loaded.ToTimeline(Clip.ToUpperInvariant(), 30.2)!.Segments);
            // Another clip of a different length now has this name: start fresh.
            Assert.Null(loaded.ToTimeline(Clip, 45));
            Assert.Null(loaded.ToTimeline(@"C:\other.mp4", 30));

            loaded.Version = EditProject.CurrentVersion + 1;
            Assert.Null(loaded.ToTimeline(Clip, 30));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ABrokenProjectFileIsIgnored()
    {
        var file = Path.Combine(Path.GetTempPath(), $"clip-engine-edit-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        try
        {
            Assert.Null(EditProject.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ProjectFilesAreNamedPerClip()
    {
        Assert.Equal(EditProject.PathFor("data", Clip), EditProject.PathFor("data", Clip.ToLowerInvariant()));
        Assert.NotEqual(EditProject.PathFor("data", Clip), EditProject.PathFor("data", @"C:\other.mp4"));
    }

    [Fact]
    public void ExportPutsEveryPartInOrder()
    {
        var t = Thirty();
        t.Split(10, out _);
        t.Move(1, 0);
        var args = EditExport.Arguments(t, @"C:\out.mp4", ExportQuality.P720);
        Assert.Equal(
            ["--montage", @"C:\out.mp4", "--part", $"{Clip}|10.000|30.000", "--part", $"{Clip}|0.000|10.000",
             "--height", "720", "--mbps", "8", "--kind", "edit"],
            args);
        var original = EditExport.Arguments(t, @"C:\out.mp4", ExportQuality.Original);
        Assert.Equal(["--height", "2160", "--mbps", "auto", "--kind", "edit"], original.Skip(6));
    }
}
