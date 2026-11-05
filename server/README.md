# Clip Engine online service

The part of Clip Engine that lives on the internet. It's what makes these work:

- **Share links.** `/c/<code>` plays a clip in any browser, with its moments marked on the timeline, and shows a preview when it's pasted into Discord. A small, see-through Clip Engine mark sits in the corner of the player and leads to the download.
- **Profiles.** `/u/<username>` shows someone's public clips, pinned ones first, with their badge unless they've hidden it.
- **The hub.** `/hub` lists everyone's public clips, newest or most watched this week, this month or ever, by game, with search. Anyone can watch without an account. Players sign in with a code from the app's Cloud page (**Open the hub** does it in one click) or a username and password they set, then choose who can see each clip, change their name and hide their badge. It's built for phones as much as desktops. Anyone can report a clip.
- **Squads.** Friends join with an invite (`/s/<invite>`). Squadmates playing the same match are linked, each one's app hears about the others' moments, and everyone's views go into a library the squad shares.
- **Friends.** Who's online, what they're playing and for how long, and a way into their squad.
- **Plans and donations.** It checks subscription and donation keys, so Pro, Ultra and the Supporter thank-yous work on every PC you sign in on.
- **Invites.** `/r/<code>` is a friend's invite, with the download. When enough friends install Clip Engine on PCs and networks of their own, the one who invited them gets a month of Pro.

## What's kept, and how it's protected

- Your clips stay on your PC until you upload or share one. Uploaded clips are stored privately and only reachable through their link (or your profile, if you make them public). Making a clip private stops its link at once.
- A cloud account needs no email. Sign-in tokens are stored hashed, sign-in codes stop working after a few wrong tries, and every address is limited in how fast it can sign up, sign in or make changes.
- It runs on Cloudflare, with its protection against attacks in front of it.

It's open for the community to read; see [LICENSE](../LICENSE) for what you may do with it.
