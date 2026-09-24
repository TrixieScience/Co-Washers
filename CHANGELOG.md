# Changelog

## 0.5.2

- **Joining over Steam is sturdier.** A host used to turn a friend away if their connection arrived before Steam
  had told the host they were in the lobby. Now the host waits for its lobby list to catch up, and the joining game
  says hello again every couple of seconds until the host answers.
- **The host can't start while someone is still joining.** Friends who are in the Steam lobby but not connected yet
  show up as "Joining...". Before, they didn't show up at all, and the host could start without them.
- **When joining fails, the menu says why.** For example "Couldn't reach the host through Steam", or which version
  is different. Every step of connecting is also written to `BepInEx/LogOutput.log`, so a failed join can be
  tracked down.
- Everyone in a session needs 0.5.2.

## 0.5.1

- **Friends' legs keep up with them.** Players move at up to 5 m/s and start and stop almost instantly, and the
  kobold's steps were made for a slow walk, so at full speed its legs stretched into splits and lunges. Now the stride
  and step rate follow the speed, a running kobold has a moment with both feet off the ground, and a foot in the air
  keeps aiming for where it should land as they speed up, stop or turn.
- **It runs the way it's going.** Moving quickly, the kobold faces where it's heading (or backs up, walking
  backwards), like the game's own feet, while its head keeps looking where that player looks. Strafing is a run
  instead of a sidestep. Knees follow the feet, so a turned foot doesn't twist a knee.
- **Tools are held in the hand.** The tool used to float where the other player's first-person view had it, with the
  hand grabbing at its side. Now the kobold holds it at the waist by its handle end, pointing where they aim, with its
  fingers wrapped around it. The sponge still reaches out to whatever they're scrubbing.
- **The left arm hangs naturally.** It used to be held up all the time. Now it only reaches when that player's hand
  actually moves.
- Everyone in a session needs 0.5.1: the mod won't connect copies that don't match.

## 0.5.0

- The mod is now called **Co-Washers** (name by Cinderace).
- **Yip!** Press **Y** during a wash and your kobold yips. It's the player's own "Yip!" from the story.
  Friends hear it from your kobold's mouth, in 3D. Each colour has its own pitch, so you can tell who yipped without
  looking. They also see it: the chin goes up, the mouth opens, the ears perk, the tail wags, and "Yip!" pops up over
  your name. You hear yourself too.
- **Footsteps.** A friend's kobold makes the floor's own footstep sound (and splash, on wet floors) each time a foot
  comes down. The game finds the sound the same way it does for yours. Shuffling in place is quieter than walking.
- **Tools in hand, checked.** A friend holding the sprayer or the sponge holds it in their right hand. When their
  sponge reaches out to scrub a surface, the kobold's arm and body lean after it.
- **One colour per player, the same on every screen.** The host is always red. The host gives each guest who joins
  the first free colour (green, blue, purple, orange, teal) and tells everyone. Kobolds, name tags and the lobby's
  player rings all use that colour, so no two friends match and everyone sees the same colours. (Before, lobby rings
  were coloured by list position, so each player saw different colours.)
- **Name tags in the menu's sticker style:** Chewy lettering in the player's colour, with an ink outline and a soft
  shadow. They face you and stay readable across the room.
- **Join Game works.** Friends can join from the host's entry in the Steam friends list, not only by invite
  (the lobby is now friends-only, and the host's Steam rich presence says where to connect).
- A guest the host rejects now sees the host's reason, not just "Disconnected: Handshake failed".
- The host sends its save to guests only when it changes, not every second.
- Changes the network protocol (version 6): everyone needs 0.5.0.
- Local-test developer options: `-coop-dev-tool <name>` (this copy holds that tool, e.g. `Sprayer` or `Sponge`,
  and uses it while `-coop-dev-walk` stands still) and `-coop-dev-yip` (yips once per walk cycle).
  `-coop-avatar-shot` also photographs the first tool use and the first yip.

## 0.4.0

- **Your friends are kobolds now, not floating hands.** Every other player is a whole kobold (KoboldKare's, see
  Credits), animated from what that player actually does:
  - the head looks where their camera looks, spread down the neck and spine
  - the left hand reaches for their real hand, and the right hand holds their real tool with its fingers closed
    round it, or hangs relaxed and swings with the walk
  - the feet plant and step as they walk, onto the real floor, without sliding or crossing; the hips bob and sway,
    and the body leans into turns and speed
  - the tail trails behind turns and swings with the walk, the ears flop when the head turns, it breathes and
    blinks, and it squints while working
  - it's sized to that player's real eye height, and each player gets their own colour: the host is the classic
    red kobold, guests are green, blue, purple, orange or teal
- **Smoother remote players.** Poses carry the sender's clock, and friends are drawn a tenth of a second in the
  past between the two poses around that moment, so they move smoothly however unevenly packets arrive. This
  changes the network protocol (version 5): everyone needs 0.4.0.
- **Checked for smoothness, frame by frame** (30 s traces of a friend walking and looking around, over the real
  network path):
  - planted feet stay locked (under 4 mm of total drift in 30 s)
  - no hitches: the kobold and its colours are prepared in the main menu, so a friend appearing mid-game costs
    about 3 ms instead of a ~60 ms stall
  - positions are interpolated along a smooth curve (Catmull-Rom), not straight lines between packets
  - body turns and hip motion run on critically damped springs, so nothing starts or stops with a jolt
  - the hips settle just low enough to keep both feet reachable, and a foot never reaches down off a ledge
- If the kobold can't be built, the friend falls back to the old hand and feet.
- Local-test developer options: `-coop-dev-walk` (this copy's *sent* pose walks a circle and looks around) and
  `-coop-avatar-shot <dir>` (photograph a friend's kobold from a separate camera; `-coop-shot-quit` quits after), and
  `-coop-avatar-trace <file.csv>` (30 s of the friend's kobold, every frame: feet, hips, head, snapshot buffer).

## 0.3.0

- **A new co-op menu.** A lobby laid out like R.E.P.O.'s, drawn like the game's own stickers:
  players with their status and Steam avatar, save stickers for the host, the Multiplayer page.
- **Guests play the host's save.** Guests used to load their own current save instead (the host's
  copy of the save reached them empty), so on a different save than the host's they played another
  level and Ryan went his own way. Two copies on one PC share a save slot, which hid it.
- **Ryan reacts to everyone.** A guest's sponge and spray now make him flinch, shake off water,
  lift a paw and so on, on every screen (only the host's did before).
- **Ryan's short lines** while he's washed now show for everyone (each copy picked its own).
- **Ready up is clearer.** The button says what it does (**Ready up** / **Not ready**), your own
  status on your strip can be clicked too, and the host shows as HOSTING (the host doesn't ready
  up; it picks the save).
- **Level changes move everyone.** Guests fade out and in with the host and start the next level
  at its start point (they stayed where they were).
- **Spraying the bucket** fills it for everyone when a guest does it (only the guest's copy
  filled before).
- **You see each other's water.** Other players' sprayers shoot real water, with its mist,
  splashes and sound (they were dry before). It's only for looks: what their water paints and
  hits comes from them, so nothing happens twice.
- Spray hits on the spots under Ryan count from up to 20 m away, as far as the spray reaches
  (8 m before).
