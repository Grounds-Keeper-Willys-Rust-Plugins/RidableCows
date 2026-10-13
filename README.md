# RidableCows — user guide

Lets players ride Rust's farm cows and bulls bareback, the way they ride horses: a **driver** steers with
the normal movement keys, and a second player can get on behind as a **passenger**. The cow walks, jogs,
runs or sprints with its own animations, tires like a horse, hurts what it runs into, and charges on
command. Version 1.2.0.

1. [Installing, and where the files are](#1-installing-and-where-the-files-are)
2. [What players experience](#2-what-players-experience)
3. [How it works](#3-how-it-works)
4. [Settings](#4-settings)
5. [Permissions](#5-permissions)
6. [Commands](#6-commands)
7. [Testing](#7-testing)
8. [Limits and known issues](#8-limits-and-known-issues)

---

## 1. Installing, and where the files are

**Requirements:** a Rust server running Oxide (uMod), on a build with the farming update's cows
(`assets/rust.ai/agents/cow/cow.prefab`, bulls are `assets/rust.ai/agents/bull/bull.prefab`). Developed
and tested on Rust build 2634 with Oxide 2.0.7801. Carbon is untested.

1. Copy `RidableCows.cs` into `oxide/plugins/`.
2. The console shows `Loaded plugin RidableCows v1.2.0`.

Settings are in `oxide/config/RidableCows.json` (section 4). The plugin keeps no data file: rides
exist only while someone is on a cow.

The plugin does not spawn cows. Wild cows come from the game's own population (`cow.population`,
per km²); admins can add more with `/cow spawn` and `/cow spawn bull`.

---

## 2. What players experience

### Getting on

- Look at an adult cow within 3 m and press **E**. The hint shows `[E] Ride`.
- On a cow that **trusts you** (one you could lead) the hint adds `hold E for Lead / Ride`. A tap
  of E still rides; **holding E** for a third of a second opens the game's pie wheel with **Lead cow**,
  **Ride cow** and **Cancel** slices (Ride is pre-selected). Pick a slice as on any pie wheel. If you
  are already leading the cow, the first slice reads **Stop leading**. Closing the wheel without a
  choice cancels.
- A second player pressing E on a ridden cow gets on behind as the passenger (`[E] Ride behind`).
  A third is told the cow is full.
- A cow that is lying down or grazing gets up first: the player is told to try again in a moment.
- Calves can't be ridden. A cow someone else is leading can't be ridden.

### Riding

| Key | Does |
|---|---|
| **W** | Go (jog) |
| **W + Shift** | Faster (sprint) |
| **W + Ctrl** | Slower (walk) |
| **A / D** | Turn; on their own, turn on the spot |
| **S** | Stop |
| **Shift** near something in front | **Charge** it (section below) |
| **E** | Get off |

Only the driver steers. Cow speeds: walk 1.2 m/s, jog 3.2, run 4.6, sprint 6; a horse is faster. The
cow keeps to the ground it can walk on (the AI navmesh): steered into water, a cliff or a wall it
detours a little to the side the rider is turning to, or stops and the hint shows `Blocked`.

### Health and stamina

While riding, two entries appear in the game's own status list (where Comfort, Bleeding and the like
show): the cow's **health** as a percentage and its **stamina**. (A setting switches this to two small
bars drawn by the plugin instead, or off.) Sprinting empties the stamina bar in about 15 seconds; running
takes twice as long; anything slower refills it (about 40 seconds from empty at a jog, faster standing
still). When it runs out the cow is **tired**: the hint says so, the entry reads **Tired** and darkens, and the cow will go no
faster than its jog until stamina is a quarter back. A charge costs a quarter of the bar.

### Ramming and charging

- **Running into things hurts them.** At a run or faster, a player, animal or wooden structure the cow
  runs into takes blunt damage (20 to a player, 30 to a wooden wall at full sprint, scaled by speed) and
  players are shoved. Bulls hit half as hard again. Each thing is hit at most once a second.
- **Charging.** Press **Shift** with something to hit within 4 m in front (a player, an animal, a
  twig or wood building piece, a wooden door, barricade, fence or ladder) and the cow lowers its head
  and lunges with the game's own charge animation. From the moment the head comes down until the lunge
  ends, everything it reaches within 2.5 m is hit once: 35 damage to a player (shoved hard), 60 to a
  wooden structure, bulls half as much again. Then there is a short cooldown, and the cow is yours
  again. With nothing in front, Shift just sprints as before. Stone and metal shrug it off.

When the driver gets off, the passenger stays on and the cow stands still until someone takes the
front. Riders are always put down on the ground beside the cow, never killed for a lack of space
(vanilla's dismount does that), including when the cow dies under them.

---

## 3. How it works

- **Seats:** when someone gets on, the plugin spawns the horse saddle seats
  (`assets/prefabs/vehicle/seats/horsesaddle.prefab` for the driver, `horsesaddlerear.prefab` for a
  passenger: the horse-riding pose) and fixes them to the cow at set spots on its back. They are not
  saved, and are removed when the last rider gets off. They are fixed to the cow itself, not to a
  bone: the game client ignores bone parenting on cows (a part on a bone shows up edge-on at the
  cow's feet, and the cow jitters).
- **Seats follow the ground.** The server keeps the cow's root upright on the navmesh; the client tilts
  the body to fit the slope. Ten times a second the plugin measures the ground under the front and
  back hooves, and tilts and lifts the seats to match (about a 0.6 m pivot, smoothed, at most 25° and
  35 cm), so the rider stays on the back going up and down hills. The seats are spawned with position
  syncing on, so the engine sends the moves and the client interpolates them.
- **Steering:** while ridden, the cow's AI (its state machine) is parked, and the plugin steers the
  cow's own navmesh agent toward a point ahead of the driver's heading at the chosen gait. The client
  animates the cow from its movement, so a ridden cow looks like a cow walking or running.
- **Stamina** is the plugin's own counter per ride (the game's horse stamina bar is horse-only client
  code, driven by horse-specific network messages a cow cannot send). The display uses the game's
  **custom vitals** channel (`CommunityEntity.SendCustomVitals`): the client draws the entries itself
  in its status list, and the plugin only sends new values when they change. The CUI panel is kept as
  an alternative.
- **Charging** plays the cow's own `headbutt` root-motion animation (`cow_attack_charge`, 1.6 s, the blow
  authored at 0.7 s) through the game's `RootMotionPlayer`, which pauses the agent and moves the cow
  along the animation. The blow is a radius sweep rather than a cone: the lunge carries the cow a few
  metres, and a cone at one instant misses what it just ran over. A different animation asset can be
  named in the config.
- **The Lead / Ride wheel** is the game's real pie menu, filled by the server through
  `CommunityEntity.SendPie` (the same channel as the client's `communityentity.pietest`). Each slice
  runs a console command on the client, which reaches the plugin's `ridablecows.wheel` command; closing
  the pie runs its cancel. The cow's own long-press wheel entries cannot be added to (they are
  client-side attributes), which is why the plugin sends a wheel of its own on the hold.
- **Getting off:** the AI is switched back on and the cow carries on with its life.
- E on a cow is also vanilla's *Lead* (on a cow that trusts you). If that lead lands on the cow you
  just got on, the plugin drops it; if you cancel the wheel, a lead the press started is dropped too.
- **No saddles.** Rust has no saddle model a plugin can put on a cow: the horse's saddle is part of
  the horse model, and the saddle props in monuments are scenery that can't be spawned.

---

## 4. Settings

`oxide/config/RidableCows.json`. Reload the plugin after editing (`oxide.reload RidableCows`).

| Setting | Default | Meaning |
|---|---|---|
| Riders per cow | `2` | `1` = driver only, `2` = driver and passenger. |
| Key that mounts the cow you are looking at | `USE` | Any Rust `BUTTON` name, e.g. `RELOAD` if E clashes with Lead/Milk. |
| Hold the mount key this long for the Lead / Ride wheel | `0.35` | Seconds. |
| Mount reach (metres) | `3` | How close the player must be. |
| Require the ridablecows.ride permission | `false` | Off: everyone can ride. |
| Cow must trust the rider | `false` | On: uses the livestock familiarity system (same trust as leading). |
| Gait when W is held | `Jog` | `Walk`, `Jog`, `Run`, `Sprint` or `FullSprint`. |
| Gait with W + Duck | `Walk` | |
| Gait with W + Sprint | `Sprint` | |
| Turn rate with A/D | `100` | Degrees per second. |
| Front / rear seat prefab | horse saddle seats | Any mountable prefab. |
| Driver / passenger seat position | `(0, 0.76, 0)` / `(0, 0.78, -0.4)` | Cow-local metres (x right, y up, z forward). Tune live with `/cow seat`. |
| Seats follow the slope of the ground | `true` | Off: seats stay fixed and level on the root. |
| Seat tilt scale | `1` | `0` keeps the seats level but still lifted; `1` tilts with the ground. `/cow tilt`. |
| Height the cow's body pivots about | `0.6` | Metres above the root. |
| Show on-screen hints | `true` | |
| Pie menu icons | `fun`, `device_add`, `close`, `close` | Sprite paths for the lead, ride, stop-leading and cancel slices. Only the game's own icon paths load; these come from its pie test. |
| Health and stamina display | `Vitals` | `Vitals` (game status list), `Cui` (plugin bars) or `Off`. |
| Vitals icons | `embrella`, `fun` | Sprite paths for the health and stamina entries. |
| Cui bars anchor min / max | `0.78 0.135` / `0.98 0.175` | Screen fractions of the bar panel's corners (Cui style only). |
| Stamina: seconds of sprint before it runs out | `15` | Running costs half as much. |
| Stamina: seconds to recover fully | `40` | At a walk or jog; twice as fast standing still. |
| Stamina: a tired cow can sprint again above this fraction | `0.25` | |
| Ramming: damage to a player / wooden structure at full sprint | `20` / `30` | Scaled by speed from the slowest ramming gait up. |
| Ramming: slowest gait that hurts | `Run` | `Run` or `Sprint`. |
| Ramming and charging: bulls hit this much harder | `1.5` | Multiplier. |
| Wooden structures: prefab name fragments that count | `wood, barricade, ladder, fence, shutter, ...` | Building blocks go by grade (twigs and wood) instead. |
| Charge: who charges with Shift near a target | `All` | `All`, `Bulls` or `None`. |
| Charge: trigger range / cone | `4` m / `40`° | Either side of the nose. |
| Charge: damage to a player / wooden structure | `35` / `60` | |
| Charge: hit reach around the cow | `2.5` m | Swept from the blow to the end of the lunge. |
| Charge: seconds into the animation when the blow lands | `0` | `0` uses the cow's own timing (0.7 s). |
| Charge: cooldown after the animation ends | `2.5` | Seconds. |
| Charge: stamina it costs | `0.25` | Fraction of the bar. |
| Charge: animation asset | blank | Blank = the cow's own headbutt; else a `RootMotionData` asset path, e.g. `assets/rust.ai/agents/cow/animations/cow_stand_attack_kick_rootmotiondata.asset`. |

---

## 5. Permissions

| Permission | Grants |
|---|---|
| `ridablecows.ride` | Riding, only when "Require the ridablecows.ride permission" is on |
| `ridablecows.admin` | `/cow spawn`, `/cow seat`, `/cow tilt` |

Server admins have both.

---

## 6. Commands

| Command | Who | Does |
|---|---|---|
| `/cow` | all | Help |
| `/cow ride` | all | Get on the cow you look at (same as E) |
| `/cow lead` | all | Lead the cow you look at, or let it go (same as the wheel) |
| `/cow info` | all | Name, sex, age, health, trust, riders and stamina of the cow you look at |
| `/cow spawn [bull]` | admin | Spawn an adult cow (or bull) where you look |
| `/cow seat front\|rear <x> <y> <z>` | admin | Move the driver or passenger seat (saved to config); applies on the next mount |
| `/cow tilt <scale>` | admin | Seat tilt scale (0 level, 1 with the ground); saved to config |
| `cow.ride` (F1 console) | all | Same as E; bindable: `bind <key> cow.ride` |
| `ridablecows.seat front\|rear <x> <y> <z>` / `tilt <scale>` | server console | Console twin of the two above, for tuning over RCON |
| `ridablecows.status` | server console | Live rides: position, heading, gait, speed, stamina, charges, hits, seat tilt, riders, AI on/off |
| `ridablecows.probe` | server console | What the game offers: the seat prefab's HUD flags and the cow's authored animations with their damage and timing |
| `ridablecows.selftest [x y z]` | server console | Automated test, see below |

---

## 7. Testing

`ridablecows.selftest` (server console or RCON) runs an end-to-end test with no client: it spawns a
cow and bot players near the first wild cow (or at `x y z`), then checks 31 things and prints
`[selftest] PASS/FAIL ...` lines to the server log, ending with `done: N passed, M failed`. It covers
the pie menu channel being present, the driver getting on through the real path (including dropping their own lead, and retrying a cow
that is still getting up), the seat syncing its position, the AI being parked, W+Shift reaching
Sprint and draining stamina, steering, stopping, Shift near a bot starting a charge that hurts it and
hands the cow back, a passenger getting on behind (and being refused when the cow carries one), a
third rider being refused, the cow carrying two, the driver and then the passenger getting off unhurt,
the AI coming back on, and the cow dying under both riders (both put down unhurt). Each drive first
faces the most open ground, so the result doesn't depend on the spot. Everything it spawns is removed
afterwards. Last run on build 2634: 32 passed, 0 failed.

`ridablecows.selftest hold` stops with two bots riding, so you can reload the plugin and check that the
riders are put down and the seats removed; `ridablecows.selftest cleanup` removes the test cow and bots
afterwards.

`ridablecows.probe` on a live server printed, for build 2634: horse saddle seats have
`shouldShowHudHealth=False` (so the game's own mount health bar can't be borrowed), and the cow FSM
holds `headbutt = cow_attack_charge` (1.6 s, damage 35, delay 0.7, radius 2.5), `kick`/`kickBehind =
cow_stand_attack_kick` (1.5 s, damage 15, delay 0.5) and `warn = cow_stomp`.

---

## 8. Limits and known issues

- **Seat height and tilt are set from the ground and the cow's skeleton, not by eye.** If riders sit
  too high, too low or too far forward, adjust with `/cow seat front|rear <x> <y> <z>`; if they lean
  wrong on slopes, `/cow tilt 0.5` (or `0` to keep them level).
- **E clashes with vanilla.** E on a cow is also Lead (trusted cows) and Milk (ready cows). The game
  may start a lead on the same press that opens the wheel; choosing Ride drops it, Cancel drops it
  too. An untrusted cow that gets "grabbed" may run off once you get off. Change the mount key if this
  is a problem.
- **The cow's own long-press wheel** (if the client shows one) opens on the same hold as the plugin's
  pie; the client's `radialMenuHoldTime` and the plugin's hold setting decide which wins. Raise or lower
  "Hold the mount key this long" if they fight.
- **Icons** are limited to sprite paths the client can load; the defaults are the only ones the server
  can prove exist. Swap them in the config once you find better ones.
- **S stops, it doesn't reverse.** Cows have no walk-backwards animation.
- **Ramming needs the cow to reach the target.** Players and animals don't block the navmesh, so a
  ridden cow runs straight through them. Buildings the navmesh routes around are not run into at all
  (the hint shows `Blocked`); charge them instead.
- **No seat swapping**; the passenger gets off and on again to drive.
- Only on the navmesh: no swimming, and no riding into buildings the AI can't walk.
