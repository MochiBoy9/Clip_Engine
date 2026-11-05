using System.Globalization;

namespace ClipEngine.Desktop.Services;

// The phrases that save a clip when they're said, as voice_phrases has them: separated by commas, each perhaps ending in
// its own clip length ("clip that, save the last minute 60"). Read the same way as ParseVoicePhrases in
// src/core/AudioSettings.cpp.
internal static class VoicePhrases
{
    public sealed record Phrase(string Words, int Seconds);

    public static IReadOnlyList<Phrase> Parse(string text, List<string>? bad = null)
    {
        var phrases = new List<Phrase>();
        foreach (var entry in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (entry.Length == 0) continue;
            var words = entry.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var seconds = 0;
            if (words.Count > 1 && Length(words[^1]) is { } length)
            {
                seconds = length;
                words.RemoveAt(words.Count - 1);
            }
            var ok = words.Count > 0 && words.All(w => w.All(c => char.IsLetterOrDigit(c) || c == '\'') && w.Any(char.IsLetter));
            if (!ok)
            {
                bad?.Add(entry);
                continue;
            }
            var phrase = string.Join(' ', words).ToLowerInvariant();
            if (phrases.All(p => p.Words != phrase)) phrases.Add(new(phrase, seconds));
        }
        return phrases;
    }

    // What's wrong with the phrases, or null when they can be saved.
    public static string? Problem(string text)
    {
        var bad = new List<string>();
        var phrases = Parse(text, bad);
        if (bad.Count > 0) return $"\"{bad[0]}\" should be words, perhaps with a length in seconds after them, like clip that 30.";
        return phrases.Count == 0 ? "Add a phrase to listen for, like clip that." : null;
    }

    // A word of advice about phrases that work but could work better, or null.
    public static string? Advice(string text) =>
        Parse(text).FirstOrDefault(p => !p.Words.Contains(' ')) is { } single
            ? $"\"{single.Words}\" on its own is easy to say by accident. Two or three words, like clip that, work best."
            : null;

    // "60", "60s" or "2m" as seconds.
    private static int? Length(string word)
    {
        var unit = 1;
        if (word.EndsWith('s') || word.EndsWith('S')) word = word[..^1];
        else if (word.EndsWith('m') || word.EndsWith('M'))
        {
            word = word[..^1];
            unit = 60;
        }
        return int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n * unit : null;
    }
}
