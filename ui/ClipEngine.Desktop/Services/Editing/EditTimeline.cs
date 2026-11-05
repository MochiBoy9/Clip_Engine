namespace ClipEngine.Desktop.Services.Editing;

// A video the edit is cut from, and how long it is.
internal sealed record EditSource(string Path, double Duration);

// One part of the edit: seconds From to To of a source, played in turn with the others.
internal sealed record EditSegment(string Source, double From, double To)
{
    public double Length => To - From;
}

internal enum SegmentEdge { Start, End }

// The editor's timeline: the parts of one or more clips that make the edit, in order, with
// undo and redo. Cut parts are left out and the rest close up, so the edit's time runs from 0
// to Duration with no gaps. Times on the timeline are "edit seconds"; times in a source are
// "source seconds".
//
// Every change goes through here, so new tools (more clips, titles, speed) only add operations.
internal sealed class EditTimeline
{
    // The shortest part there can be, so every part stays visible and grabbable on the timeline.
    public const double MinSegment = 0.25;
    private const int MaxUndo = 200;

    private readonly Dictionary<string, EditSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<IReadOnlyList<EditSegment>> _undo = new();
    private readonly Stack<IReadOnlyList<EditSegment>> _redo = new();
    private IReadOnlyList<EditSegment> _segments;
    private IReadOnlyList<EditSegment>? _gestureStart;

    public EditTimeline(IEnumerable<EditSource> sources, IEnumerable<EditSegment> segments)
    {
        foreach (var source in sources) _sources[source.Path] = source;
        _segments = segments.Select(Clamp).Where(s => s is not null).Select(s => s!).ToList();
        if (_segments.Count == 0)
        {
            _segments = _sources.Values.Where(s => s.Duration >= MinSegment)
                .Select(s => new EditSegment(s.Path, 0, s.Duration)).Take(1).ToList();
        }
    }

    // The whole of one clip, the way the editor starts.
    public static EditTimeline Whole(string path, double duration) =>
        new([new EditSource(path, duration)], [new EditSegment(path, 0, duration)]);

    // Raised after every change, undo and redo included.
    public event Action? Changed;

    public IReadOnlyList<EditSegment> Segments => _segments;
    public IReadOnlyCollection<EditSource> Sources => _sources.Values;
    public double Duration => _segments.Sum(s => s.Length);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Whether anything has been cut, trimmed or moved: the edit isn't just the clip as it is.
    public bool IsEdited =>
        _segments.Count != 1 || _sources.Count != 1
        || _segments[0].From > 0.001 || _segments[0].To < _sources[_segments[0].Source].Duration - 0.001;

    public double SourceDuration(string path) => _sources.TryGetValue(path, out var s) ? s.Duration : 0;

    // Where part `index` starts on the timeline.
    public double StartOf(int index)
    {
        double at = 0;
        for (var i = 0; i < index && i < _segments.Count; i++) at += _segments[i].Length;
        return at;
    }

    public double EndOf(int index) => StartOf(index) + _segments[index].Length;

    // The part playing at a time on the timeline, and the matching time in its source. A time on
    // a cut between two parts belongs to the later one; the very end belongs to the last.
    public (int Index, double SourceSeconds) Locate(double editSeconds)
    {
        if (_segments.Count == 0) return (-1, 0);
        editSeconds = Math.Clamp(editSeconds, 0, Duration);
        double at = 0;
        for (var i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if (editSeconds < at + s.Length || i == _segments.Count - 1)
                return (i, Math.Clamp(s.From + (editSeconds - at), s.From, s.To));
            at += s.Length;
        }
        return (_segments.Count - 1, _segments[^1].To);
    }

    // Every place on the timeline that shows this moment of a source: none if it was cut, more
    // than one if a part was duplicated.
    public IReadOnlyList<double> EditTimesOf(string source, double sourceSeconds)
    {
        var times = new List<double>();
        double at = 0;
        foreach (var s in _segments)
        {
            if (string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase)
                && sourceSeconds >= s.From && sourceSeconds <= s.To)
                times.Add(at + sourceSeconds - s.From);
            at += s.Length;
        }
        return times;
    }

    // The cuts between parts, as times on the timeline.
    public IReadOnlyList<double> Cuts()
    {
        var cuts = new List<double>();
        double at = 0;
        for (var i = 0; i < _segments.Count - 1; i++)
        {
            at += _segments[i].Length;
            cuts.Add(at);
        }
        return cuts;
    }

    // Cuts the part under the playhead in two. False when the playhead is too near either end of
    // its part for both halves to be long enough. Returns the index of the second half in `after`.
    public bool Split(double editSeconds, out int after)
    {
        after = -1;
        var (index, at) = Locate(editSeconds);
        if (index < 0) return false;
        var s = _segments[index];
        if (at - s.From < MinSegment || s.To - at < MinSegment) return false;
        var next = _segments.ToList();
        next[index] = s with { To = at };
        next.Insert(index + 1, s with { From = at });
        Apply(next);
        after = index + 1;
        return true;
    }

    // Takes a part out of the edit and closes the gap. The last part can't go.
    public bool Remove(int index)
    {
        if (index < 0 || index >= _segments.Count || _segments.Count == 1) return false;
        var next = _segments.ToList();
        next.RemoveAt(index);
        Apply(next);
        return true;
    }

    // Keeps only the part between two times on the timeline, cutting the rest.
    public bool KeepOnly(double fromEdit, double toEdit)
    {
        if (toEdit - fromEdit < MinSegment) return false;
        var next = new List<EditSegment>();
        double at = 0;
        foreach (var s in _segments)
        {
            var from = Math.Max(fromEdit, at);
            var to = Math.Min(toEdit, at + s.Length);
            if (to - from >= MinSegment) next.Add(s with { From = s.From + (from - at), To = s.From + (to - at) });
            at += s.Length;
        }
        if (next.Count == 0) return false;
        Apply(next);
        return true;
    }

    // Moves one end of a part to a time in its source, as far as the source and the shortest
    // part allow. The rest of the edit shifts along to close up. Returns whether it moved.
    public bool Resize(int index, SegmentEdge edge, double sourceSeconds)
    {
        if (index < 0 || index >= _segments.Count) return false;
        var s = _segments[index];
        var length = SourceDuration(s.Source);
        var resized = edge == SegmentEdge.Start
            ? s with { From = Math.Clamp(sourceSeconds, 0, s.To - MinSegment) }
            : s with { To = Math.Clamp(sourceSeconds, s.From + MinSegment, length) };
        if (resized == s) return false;
        var next = _segments.ToList();
        next[index] = resized;
        Apply(next);
        return true;
    }

    // Moves a part to another place in the order.
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _segments.Count || to < 0 || to >= _segments.Count || from == to) return false;
        var next = _segments.ToList();
        var s = next[from];
        next.RemoveAt(from);
        next.Insert(to, s);
        Apply(next);
        return true;
    }

    // Plays a part twice, the copy straight after it.
    public bool Duplicate(int index)
    {
        if (index < 0 || index >= _segments.Count) return false;
        var next = _segments.ToList();
        next.Insert(index + 1, _segments[index]);
        Apply(next);
        return true;
    }

    // Back to the whole of each source, one after another.
    public bool Reset()
    {
        var whole = _sources.Values.Select(s => new EditSegment(s.Path, 0, s.Duration)).ToList();
        if (whole.SequenceEqual(_segments)) return false;
        Apply(whole);
        return true;
    }

    // A drag of a handle is many small changes that undo as one: call BeginGesture when it
    // starts and EndGesture when it's let go.
    public void BeginGesture() => _gestureStart ??= _segments;

    public void EndGesture()
    {
        if (_gestureStart is null) return;
        var start = _gestureStart;
        _gestureStart = null;
        if (start.SequenceEqual(_segments)) return;
        PushUndo(start);
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_gestureStart is not null) EndGesture();
        if (_undo.Count == 0) return false;
        _redo.Push(_segments);
        _segments = _undo.Pop();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        PushUndo(_segments);
        _segments = _redo.Pop();
        Changed?.Invoke();
        return true;
    }

    private void Apply(List<EditSegment> next)
    {
        if (_gestureStart is null)
        {
            PushUndo(_segments);
            _redo.Clear();
        }
        _segments = next;
        Changed?.Invoke();
    }

    private void PushUndo(IReadOnlyList<EditSegment> state)
    {
        _undo.Push(state);
        if (_undo.Count <= MaxUndo) return;
        // Drop the oldest.
        var kept = _undo.Take(MaxUndo).Reverse().ToList();
        _undo.Clear();
        foreach (var s in kept) _undo.Push(s);
    }

    // A part that fits inside its source, or null if it doesn't name one or is too short.
    private EditSegment? Clamp(EditSegment s)
    {
        if (!_sources.TryGetValue(s.Source, out var source)) return null;
        var from = Math.Clamp(s.From, 0, source.Duration);
        var to = Math.Clamp(s.To, 0, source.Duration);
        return to - from < MinSegment ? null : new EditSegment(source.Path, from, to);
    }
}
