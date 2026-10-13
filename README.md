# RidableCows — user guide

Lets players ride Rust's farm cows bareback, the way they ride horses: a **driver** steers with the
normal movement keys, and a second player can get on behind as a **passenger**. The cow walks, jogs,
runs or sprints with its own animations. Version 1.1.0.

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
(`assets/rust.ai/agents/cow/cow.prefab`). Developed and tested on Rust build 2634 with Oxide 2.0.7801.
Carbon is untested.

1. Copy `RidableCows.cs` into `oxide/plugins/`.
2. The console shows `Loaded plugin RidableCows v1.1.0`.

Settings are in `oxide/config/RidableCows.json` (section 4). The plugin keeps no data file: rides
exist only while someone is on a cow.

The plugin does not spawn cows. Wild cows come from the game's own population (`cow.population`,
per km²); admins can add more with `/cow spawn`.

---

## 2. What players experience

### Getting on

- Look at an adult cow within 3 m and press **E**. The hint shows `[E] Ride`.
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
| **E** | Get off |

Only the driver steers. Cow speeds: walk 1.2 m/s, jog 3.2, run 4.6, sprint 6; a horse is faster. The
cow keeps to the ground it can walk on (the AI navmesh): steered into water, a cliff or a wall it
detours a little to the side the rider is turning to, or stops and the hint shows `Blocked`.

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
- **Steering:** while ridden, the cow's AI (its state machine) is parked, and the plugin steers the
  cow's own navmesh agent toward a point ahead of the driver's heading at the chosen gait. The client
  animates the cow from its movement, so a ridden cow looks like a cow walking or running.
- **Getting off:** the AI is switched back on and the cow carries on with its life.
- E on a cow is also vanilla's *Lead* (on a cow that trusts you). If that lead lands on the cow you
  just got on, the plugin drops it.
- **No saddles.** Rust has no saddle model a plugin can put on a cow: the horse's saddle is part of
  the horse model, and the saddle props in monuments are scenery that can't be spawned. Version 1.0
  tracked horse saddles on cows and showed a shrunken rug as a stand-in; 1.1 dropped both.

---

## 4. Settings

`oxide/config/RidableCows.json`. Reload the plugin after editing (`oxide.reload RidableCows`).

| Setting | Default | Meaning |
|---|---|---|
| Riders per cow | `2` | `1` = driver only, `2` = driver and passenger. |
| Key that mounts the cow you are looking at | `USE` | Any Rust `BUTTON` name, e.g. `RELOAD` if E clashes with Lead/Milk. |
| Mount reach (metres) | `3` | How close the player must be. |
| Require the ridablecows.ride permission | `false` | Off: everyone can ride. |
| Cow must trust the rider | `false` | On: uses the livestock familiarity system (same trust as leading). |
| Gait when W is held | `Jog` | `Walk`, `Jog`, `Run`, `Sprint` or `FullSprint`. |
| Gait with W + Duck | `Walk` | |
| Gait with W + Sprint | `Sprint` | |
| Turn rate with A/D | `100` | Degrees per second. |
| Front / rear seat prefab | horse saddle seats | Any mountable prefab. |
| Driver / passenger seat position | `(0, 0.84, 0)` / `(0, 0.86, -0.4)` | Cow-local metres (x right, y up, z forward). Tune live with `/cow seat`. |
| Show on-screen hints | `true` | |

---

## 5. Permissions

| Permission | Grants |
|---|---|
| `ridablecows.ride` | Riding, only when "Require the ridablecows.ride permission" is on |
| `ridablecows.admin` | `/cow spawn` and `/cow seat` |

Server admins have both.

---

## 6. Commands

| Command | Who | Does |
|---|---|---|
| `/cow` | all | Help |
| `/cow ride` | all | Get on the cow you look at (same as E) |
| `/cow info` | all | Name, sex, age and riders of the cow you look at |
| `/cow spawn` | admin | Spawn an adult cow where you look |
| `/cow seat front\|rear <x> <y> <z>` | admin | Move the driver or passenger seat (saved to config); applies on the next mount |
| `cow.ride` (F1 console) | all | Same as E; bindable: `bind <key> cow.ride` |
| `ridablecows.status` | server console | Live rides: position, heading, gait, speed, riders, AI on/off |
| `ridablecows.selftest [x y z]` | server console | Automated test, see below |

---

## 7. Testing

`ridablecows.selftest` (server console or RCON) runs an end-to-end test with no client: it spawns a
cow and bot players near the first wild cow (or at `x y z`), then checks 25 things and prints
`[selftest] PASS/FAIL ...` lines to the server log, ending with `done: N passed, M failed`. It covers
the driver getting on through the real path (including dropping their own lead), the AI being parked,
W+Shift reaching Sprint, steering, stopping, a passenger getting on behind (and being refused when the
cow carries one), a third rider being refused, the cow carrying two, the driver and then the passenger
getting off unhurt, the AI coming back on, and the cow dying under both riders (both put down
unhurt). Each drive first faces the most open ground, so the result doesn't depend on the spot.
Everything it spawns is removed afterwards.

`ridablecows.selftest hold` stops with two bots riding, so you can reload the plugin and check that the
riders are put down and the seats removed; `ridablecows.selftest cleanup` removes the test cow and bots
afterwards.

---

## 8. Limits and known issues

- **Seat height is set from the cow's skeleton, not by eye.** If riders sit too high, too low or too
  far forward, adjust with `/cow seat front|rear <x> <y> <z>`.
- **E clashes with vanilla.** E on a cow is also Lead (trusted cows) and Milk (ready cows). An untrusted
  cow that gets "grabbed" may run off once you get off. Change the mount key if this is a problem.
- **S stops, it doesn't reverse.** Cows have no walk-backwards animation.
- **No stamina**, unlike horses; a cow can sprint as long as the rider likes.
- **No seat swapping**; the passenger gets off and on again to drive.
- Only on the navmesh: no swimming, and no riding into buildings the AI can't walk.
