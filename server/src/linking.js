// Who is playing together. Squadmates are linked while they're in the same game; when both apps
// have seen the current match start, the starts have to be close too, so two friends playing
// the same game in different lobbies aren't cut together.

export const MATCH_START_TOLERANCE_MS = 45_000;

export function sameGame(a, b) {
  return typeof a === "string" && typeof b === "string" && a.length > 0 && a.toLowerCase() === b.toLowerCase();
}

// presence: { game, match } where match is when the current match started (server ms) or null.
export function playingTogether(a, b) {
  if (!a || !b || !sameGame(a.game, b.game)) return false;
  if (a.unlinked?.includes(b.member) || b.unlinked?.includes(a.member)) return false;
  if (Number.isFinite(a.match) && Number.isFinite(b.match)) {
    return Math.abs(a.match - b.match) <= MATCH_START_TOLERANCE_MS;
  }
  return true;
}

// The members linked with `from`, from everyone's presence. Each member counts once, even
// with the app open on two PCs.
export function linkedMembers(from, presences) {
  const linked = new Set();
  for (const p of presences) {
    if (p.member === from.member) continue;
    if (playingTogether(from, p)) linked.add(p.member);
  }
  return [...linked];
}

// Groups of members playing together right now, for showing "In a squad of 3" and the Squad page.
export function groups(presences) {
  const seen = new Set();
  const out = [];
  for (const p of presences) {
    if (seen.has(p.member) || !p.game) continue;
    const group = [p.member, ...linkedMembers(p, presences)].filter((m, i, all) => all.indexOf(m) === i);
    if (group.length < 2) continue;
    group.forEach((m) => seen.add(m));
    out.push({ game: p.game, members: group });
  }
  return out;
}
