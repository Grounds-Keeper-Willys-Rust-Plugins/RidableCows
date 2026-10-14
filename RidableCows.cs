// Reference: UnityEngine.AnimationModule
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Oxide.Core;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using ProtoBuf;
using Rust.Ai.Gen2;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RidableCows", "Lucien", "1.3.2")]
    [Description("Ride cows bareback like horses: driver and passenger, stamina, ramming, bull charges, and the game's horse HUD via a hidden carrier horse")]
    public class RidableCows : RustPlugin
    {
        private const string CowPrefab = "assets/rust.ai/agents/cow/cow.prefab";
        private const string BullPrefab = "assets/rust.ai/agents/bull/bull.prefab";
        private const string HorsePrefab = "assets/content/vehicles/horse/ridablehorse.prefab";
        private const string HarmonyId = "ridablecows.carrier";
        private const string PermRide = "ridablecows.ride";
        private const string PermAdmin = "ridablecows.admin";
        private const string HintPanel = "RidableCows.Hint";
        private const string BarsPanel = "RidableCows.Bars";
        private const string WarmPanel = "RidableCows.IconWarm";
        private const float TickInterval = 0.1f;
        private const float WheelTimeout = 30f;    // a wheel nobody picks from is replaced by a one-slice close after this

        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo FsmRunningField = typeof(FSMComponent).GetField("isRunning", Private);
        private static readonly FieldInfo PlayStateField = typeof(RootMotionPlayer).GetField("currentPlayState", Private);
        private static readonly MethodInfo StopServerMethod = typeof(RootMotionPlayer).GetMethod("StopServer", Private);
        private static readonly FieldInfo CurrentSpeedField = typeof(RustNavMeshAgent).GetField("currentSpeed", Private);

        // ------------------------------------------------------------------ config

        private Configuration _config;

        private class SeatSpot
        {
            public float X, Y, Z;
            public SeatSpot() { }
            public SeatSpot(float x, float y, float z) { X = x; Y = y; Z = z; }
            [JsonIgnore] public Vector3 Vector => new Vector3(X, Y, Z);
            public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
        }

        private class Configuration
        {
            [JsonProperty("Ride method: 1 = saddle seats on the cow with the plugin's own health/stamina display, 2 = hidden carrier horse with the game's horse bars")]
            public int RideMethod = 2;

            [JsonProperty("Riders per cow (1 = driver only, 2 = driver and passenger)")]
            public int RidersPerCow = 2;

            [JsonProperty("Carrier horse: vertical offset from the cow (metres); its body must never overlap the cow. 3 is proven; 20 m under the ground left the client unmounted")]
            public float CarrierLift = 3f;

            [JsonProperty("Carrier horse: where the rider sits on the cow (cow-local metres, before the lift)")]
            public SeatSpot CarrierSpot = new SeatSpot(0f, -0.2f, -0.6f);

            [JsonProperty("Carrier horse: seconds after mounting before it is hidden")]
            public float CarrierHideDelay = 0.75f;

            [JsonProperty("Stop: deceleration when the driver lets go (m/s^2)")]
            public float StopDeceleration = 3f;

            [JsonProperty("Reverse: speed walking backwards with S (m/s)")]
            public float ReverseSpeed = 0.8f;

            [JsonProperty("Stop: play one of the cow's own idle clips once it has stopped")]
            public bool IdleClipOnStop = true;

            [JsonProperty("Key that mounts the cow you are looking at (USE, RELOAD, FIRE_THIRD...)")]
            public string MountKey = "USE";

            [JsonProperty("Hold the mount key this long for the Lead / Ride wheel (seconds)")]
            public float WheelHoldSeconds = 0.35f;

            [JsonProperty("Mount reach (metres)")]
            public float MountDistance = 3f;

            [JsonProperty("Head zone: looking at the cow this far forward of its centre (cow-local metres), E leads instead of rides when it can be led")]
            public float HeadZone = 0.8f;

            [JsonProperty("Require the ridablecows.ride permission")]
            public bool RequirePermission = false;

            [JsonProperty("Cow must trust the rider (livestock familiarity)")]
            public bool RequireTrust = false;

            [JsonProperty("Gait when W is held (Walk, Jog, Run, Sprint, FullSprint)")]
            public string CruiseGait = "Jog";

            [JsonProperty("Gait with W + Duck (slow)")]
            public string SlowGait = "Walk";

            [JsonProperty("Gait with W + Sprint (fast)")]
            public string FastGait = "Sprint";

            [JsonProperty("Turn rate with A/D (degrees per second)")]
            public float TurnRate = 100f;

            [JsonProperty("Front seat prefab")]
            public string FrontSeatPrefab = "assets/prefabs/vehicle/seats/horsesaddle.prefab";

            [JsonProperty("Rear seat prefab")]
            public string RearSeatPrefab = "assets/prefabs/vehicle/seats/horsesaddlerear.prefab";

            // Seats are fixed to the cow itself: the game client ignores bone parenting on cows (a part on a
            // bone shows up edge-on at the cow's feet, and the cow jitters)
            [JsonProperty("Driver seat position (cow-local metres: x right, y up, z forward)")]
            public SeatSpot FrontSeat = new SeatSpot(0f, 0.76f, 0f);

            [JsonProperty("Passenger seat position")]
            public SeatSpot RearSeat = new SeatSpot(0f, 0.78f, -0.40f);

            [JsonProperty("Seats follow the slope of the ground under the cow")]
            public bool SeatFollowsGround = true;

            [JsonProperty("Seat tilt scale (1 = tilt with the ground, 0 = stay level)")]
            public float SeatPitchScale = 1f;

            [JsonProperty("Height the cow's body pivots about when it tilts (metres)")]
            public float SeatPivotHeight = 0.6f;

            [JsonProperty("Show on-screen hints")]
            public bool ShowHints = true;

            [JsonProperty("Log pie wheel choices to the console (diagnostics)")]
            public bool LogWheel = true;

            [JsonProperty("Pie menu icons by slice (sprite paths the client can load): ride, lead, stop, milk, cancel")]
            public Dictionary<string, string> PieIcons = new Dictionary<string, string>
            {
                ["ride"] = "assets/icons/vehicle.png",
                ["lead"] = "assets/icons/peace.png",
                ["stop"] = "assets/icons/stop.png",
                ["milk"] = "assets/icons/cart.png",
                ["cancel"] = "assets/icons/close.png"
            };

            [JsonProperty("Health and stamina display while riding: Vitals (the game's status list), Cui (plugin bars) or Off")]
            public string BarsStyle = "Vitals";

            [JsonProperty("Vitals icons: health, stamina (sprite paths the client can load)")]
            public List<string> VitalIcons = new List<string> { "assets/icons/embrella.png", "assets/icons/fun.png" };

            [JsonProperty("Cui bars anchor min (screen fraction, bottom-left corner)")]
            public string BarsAnchorMin = "0.78 0.135";

            [JsonProperty("Cui bars anchor max (top-right corner)")]
            public string BarsAnchorMax = "0.98 0.175";

            [JsonProperty("Stamina: seconds of sprint before it runs out")]
            public float SprintSeconds = 15f;

            [JsonProperty("Stamina: seconds to recover fully at a walk or jog")]
            public float RecoverSeconds = 40f;

            [JsonProperty("Stamina: a tired cow can sprint again above this fraction")]
            public float TiredRecover = 0.25f;

            [JsonProperty("Ramming: damage to a player run into at full sprint")]
            public float RamPlayerDamage = 20f;

            [JsonProperty("Ramming: damage to a wooden structure run into at full sprint")]
            public float RamStructureDamage = 30f;

            [JsonProperty("Ramming: slowest gait that hurts (Run or Sprint)")]
            public string RamMinGait = "Run";

            [JsonProperty("Ramming and charging: bulls hit this much harder (multiplier)")]
            public float BullMultiplier = 1.5f;

            [JsonProperty("Wooden structures: prefab name fragments that count (building blocks go by grade)")]
            public List<string> WoodenNames = new List<string> { "wood", "barricade", "ladder", "fence", "shutter", "window.bars.wood" };

            [JsonProperty("Charge: who charges with Shift near a target (All, Bulls, None)")]
            public string ChargeWho = "All";

            [JsonProperty("Charge: trigger range (metres)")]
            public float ChargeRange = 4f;

            [JsonProperty("Charge: trigger cone either side of the nose (degrees)")]
            public float ChargeAngle = 40f;

            [JsonProperty("Charge: damage to a player")]
            public float ChargePlayerDamage = 35f;

            [JsonProperty("Charge: damage to a wooden structure")]
            public float ChargeStructureDamage = 60f;

            [JsonProperty("Charge: hit reach around the cow (metres)")]
            public float ChargeHitRange = 2.5f;

            [JsonProperty("Charge: seconds into the animation when the blow lands (0 = the cow's own timing)")]
            public float ChargeHitDelay = 0f;

            [JsonProperty("Charge: cooldown after the animation ends (seconds)")]
            public float ChargeCooldown = 2.5f;

            [JsonProperty("Charge: stamina it costs (fraction of the bar)")]
            public float ChargeStaminaCost = 0.25f;

            [JsonProperty("Charge: animation asset (blank = the cow's own headbutt)")]
            public string ChargeAnimation = "";

            [JsonProperty("Kick (double-tap Ctrl): damage to a player")]
            public float KickPlayerDamage = 15f;

            [JsonProperty("Kick: damage to a wooden structure")]
            public float KickStructureDamage = 20f;

            [JsonProperty("Kick: reach (metres) in front of the cow")]
            public float KickRange = 2.5f;

            [JsonProperty("Kick: cooldown (seconds)")]
            public float KickCooldown = 1.5f;

            [JsonProperty("Kick: stamina it costs (fraction of the bar)")]
            public float KickStaminaCost = 0.08f;

            [JsonProperty("Moo key (RELOAD = R, or any Rust BUTTON name; blank = off)")]
            public string MooKey = "RELOAD";
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception("empty config");
            }
            catch (Exception ex)
            {
                PrintError($"Config is invalid ({ex.Message}); using defaults. Fix or delete the file.");
                _config = new Configuration();
                return;
            }
            _config.RidersPerCow = Mathf.Clamp(_config.RidersPerCow, 1, 2);
            _config.RideMethod = Mathf.Clamp(_config.RideMethod, 1, 2);
            if (_config.CarrierSpot == null) _config.CarrierSpot = new SeatSpot(0f, -0.2f, -0.6f);
            if (_config.StopDeceleration < 0.5f) _config.StopDeceleration = 0.5f;
            if (_config.SprintSeconds < 1f) _config.SprintSeconds = 1f;
            if (_config.RecoverSeconds < 1f) _config.RecoverSeconds = 1f;
            if (_config.WoodenNames == null) _config.WoodenNames = new List<string>();
            if (_config.PieIcons == null) _config.PieIcons = new Dictionary<string, string>();
            foreach (var pair in new Dictionary<string, string> { ["ride"] = "assets/icons/vehicle.png", ["lead"] = "assets/icons/peace.png", ["stop"] = "assets/icons/stop.png", ["milk"] = "assets/icons/cart.png", ["cancel"] = "assets/icons/close.png" })
                if (!_config.PieIcons.ContainsKey(pair.Key)) _config.PieIcons[pair.Key] = pair.Value;
            while (_config.VitalIcons.Count < 2) _config.VitalIcons.Add("");
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        // ------------------------------------------------------------------ rides (runtime only)

        [Flags]
        private enum Keys { None = 0, Forward = 1, Back = 2, Left = 4, Right = 8, Faster = 16, Slower = 32 }

        private class Ride
        {
            public Cow Cow;
            public RustNavMeshAgent Agent;
            public FSMComponent Fsm;
            public RootMotionPlayer Anim;
            public BaseMountable Front, Rear;   // Rear only once a passenger gets on
            public float Heading;               // where the driver is steering, world yaw in degrees
            public Keys Input;
            public float InputTime;
            public bool InputPinned;            // self-test drives the cow without a client
            public bool Moving;
            public bool Ending;
            public float BlockedUntil;          // nothing walkable ahead: don't re-plan every tick
            public float MountedAt;
            public string Gait = "Stop";
            // stamina, 0..1 like a horse's bar
            public float Stamina = 1f;
            public bool Tired;
            // seats following the ground
            public float SeatPitch, SeatLift;
            // ramming and charging
            public readonly Dictionary<ulong, float> RamCooldown = new Dictionary<ulong, float>();
            // method 2: the hidden carrier horse whose saddle the driver sits on
            public RidableHorse Carrier;
            public bool CarrierHidden;
            public Vector3 RearSpot;            // the passenger's spot: the config's, pushed back behind a carrier saddle
            public Vector3 LastCowPos;
            // stopping: brake to a halt, then an idle clip (which pauses the agent until the driver moves again)
            public bool Stopping, Reversing;
            public float RampSpeed;
            public RustNavMeshAgent.Speeds LastGait = RustNavMeshAgent.Speeds.Jog;
            public RootMotionPlayer.PlayServerState Idle;
            // rider tricks: a kick (double-tap Ctrl) and a moo (R)
            public RootMotionPlayer.PlayServerState Kick, Moo;
            public float KickHitAt, KickReadyAt, LastDuckTap;
            public bool KickHit, KickRequested, MooRequested;
            public RootMotionPlayer.PlayServerState Charge;
            public float ChargeHitAt, ChargeReadyAt;
            public readonly HashSet<ulong> ChargeVictims = new HashSet<ulong>();   // hit once per charge
            public int Charges, Hits;
        }

        // A mount-key press on a cow that could be led as well as ridden: a tap rides, a hold opens the wheel
        private class UsePress
        {
            public Cow Cow;
            public float Since;
            public bool WasLeading;             // leading it before the press (the game's own E may start a lead too)
            public bool WheelOpen;
            public float WheelOpenedAt;
            public bool CancelPending;          // the pie's close ran; the chosen slice's command follows a moment later
            public bool HeadLead;               // pressed at the head of a leadable cow: a tap leads (the game's own E), only a hold rides
        }

        private readonly Dictionary<ulong, Ride> _rides = new Dictionary<ulong, Ride>();       // by cow net ID
        private readonly Dictionary<ulong, Ride> _seatRides = new Dictionary<ulong, Ride>();   // by seat net ID
        private readonly Dictionary<ulong, Ride> _carrierRides = new Dictionary<ulong, Ride>();   // by carrier horse net ID
        private static readonly HashSet<RidableHorse> Carriers = new HashSet<RidableHorse>();   // the Harmony prefix skips their fixed update
        private Harmony _harmony;
        private Timer _carrierTimer;
        private readonly Dictionary<string, uint> _iconIds = new Dictionary<string, uint>();   // slice -> file store CRC of our own PNG
        private readonly Dictionary<ulong, float> _mountCooldown = new Dictionary<ulong, float>();   // by player: no remount on the dismount press
        private readonly Dictionary<ulong, string> _hintShown = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _barsShown = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, UsePress> _presses = new Dictionary<ulong, UsePress>();
        private Timer _tickTimer, _hintTimer;
        private BUTTON _mountButton = BUTTON.USE;
        private BUTTON? _mooButton = BUTTON.RELOAD;
        private bool _unloading;
        private RootMotionData _chargeAnimOverride;
        private bool _chargeAnimTried;
        private int _hitMask;

        // ------------------------------------------------------------------ lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermRide, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            if (!Enum.TryParse(_config.MountKey, true, out _mountButton))
            {
                PrintWarning($"Unknown mount key '{_config.MountKey}', using USE");
                _mountButton = BUTTON.USE;
            }
            if (string.IsNullOrEmpty(_config.MooKey)) _mooButton = null;
            else if (Enum.TryParse(_config.MooKey, true, out BUTTON moo)) _mooButton = moo;
            else { PrintWarning($"Unknown moo key '{_config.MooKey}', using RELOAD"); _mooButton = BUTTON.RELOAD; }
            _hitMask = MaskOf("Player (Server)", "Construction", "Deployed", "Default", "AI", "Vehicle Detailed");
            // A carrier horse's own fixed update (physics drive, ground alignment, clipping checks) would fight
            // its placement on the cow every tick: skip it. Its HUD update is a separate invoke and keeps running.
            _harmony = new Harmony(HarmonyId);
            var target = AccessTools.Method(typeof(RidableHorse), "VehicleFixedUpdate");
            if (target != null) _harmony.Patch(target, prefix: new HarmonyMethod(typeof(RidableCows).GetMethod(nameof(VehicleFixedUpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            else PrintError("RidableHorse.VehicleFixedUpdate not found: ride method 2 (carrier horse) is unavailable");
            LoadIcons();
            _tickTimer = timer.Every(TickInterval, TickRides);
            _hintTimer = timer.Every(0.25f, UpdateHints);
        }

        private void Unload()
        {
            _unloading = true;
            _tickTimer?.Destroy();
            _hintTimer?.Destroy();
            foreach (var ride in new List<Ride>(_rides.Values)) EndRide(ride);
            _carrierTimer?.Destroy();
            _harmony?.UnpatchAll(HarmonyId);
            Carriers.Clear();
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, HintPanel);
                CuiHelper.DestroyUi(player, WarmPanel);
                if (_barsShown.ContainsKey(player.userID)) ShowBars(player, null);
            }
        }

        private static bool VehicleFixedUpdatePrefix(RidableHorse __instance) => !Carriers.Contains(__instance);

        private static bool UsesCarrier(Ride ride) => ride.Carrier != null;

        // Layer names with spaces resolve to -1 and would silently drop out of LayerMask.GetMask
        private static int MaskOf(params string[] names)
        {
            int mask = 0;
            foreach (string name in names)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask |= 1 << layer;
            }
            return mask;
        }

        // ------------------------------------------------------------------ mounting

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null) return;
            var seat = player.GetMounted();
            if (seat != null)
            {
                if (_presses.ContainsKey(player.userID)) CloseWheel(player, cancel: false);
                if (seat.net == null || !_seatRides.TryGetValue(seat.net.ID.Value, out Ride ride) || seat != ride.Front || ride.InputPinned) return;
                Keys keys = Keys.None;
                if (input.IsDown(BUTTON.FORWARD)) keys |= Keys.Forward;
                if (input.IsDown(BUTTON.BACKWARD)) keys |= Keys.Back;
                if (input.IsDown(BUTTON.LEFT)) keys |= Keys.Left;
                if (input.IsDown(BUTTON.RIGHT)) keys |= Keys.Right;
                if (input.IsDown(BUTTON.SPRINT)) keys |= Keys.Faster;
                if (input.IsDown(BUTTON.DUCK)) keys |= Keys.Slower;
                ride.Input = keys;
                ride.InputTime = Time.realtimeSinceStartup;
                if (_mooButton.HasValue && input.WasJustPressed(_mooButton.Value)) ride.MooRequested = true;
                if (input.WasJustPressed(BUTTON.DUCK))
                {
                    // Double-tap Ctrl kicks, the way a horse rears
                    if (ride.InputTime - ride.LastDuckTap < 0.3f) ride.KickRequested = true;
                    ride.LastDuckTap = ride.InputTime;
                }
                return;
            }

            float now = Time.realtimeSinceStartup;
            bool down = input.IsDown(_mountButton);
            if (_presses.TryGetValue(player.userID, out UsePress press))
            {
                var cow = press.Cow;
                bool gone = cow == null || cow.IsDestroyed || cow.IsDead() || Vector3.Distance(player.transform.position, cow.transform.position) > _config.MountDistance + 2.5f;
                if (press.WheelOpen)
                {
                    // The pie menu is the client's; its choice arrives as a console command, possibly seconds
                    // later. Keep the press until then, or until it expires, the cow goes away, or the key is
                    // pressed again for a fresh wheel.
                    if (gone || now - press.WheelOpenedAt > WheelTimeout)
                    {
                        // The client's pie stays up on its own; the server cannot close it, but it can replace it:
                        // a single Cancel slice tells the player the cow is out of reach and lets them close it
                        ReplaceWheel(player, gone ? $"{(cow == null || cow.IsDestroyed ? "The cow" : CowName(cow))} wandered off" : "Nothing chosen");
                        CloseWheel(player, cancel: true);
                        return;
                    }
                    if (input.WasJustPressed(_mountButton) && now - press.WheelOpenedAt > 0.5f) CloseWheel(player, cancel: true);
                    else return;
                }
                else
                {
                    if (gone) { _presses.Remove(player.userID); return; }
                    if (down)
                    {
                        if (now - press.Since >= _config.WheelHoldSeconds) OpenWheel(player, press);
                        return;
                    }
                    // Released before the wheel: a tap rides, unless it was at the head of a leadable cow,
                    // where the game's own E has already started (or stopped) the lead
                    _presses.Remove(player.userID);
                    if (!press.HeadLead) TryRide(player, cow);
                    return;
                }
            }

            if (!input.WasJustPressed(_mountButton)) return;
            if (_mountCooldown.TryGetValue(player.userID, out float until) && now < until) return;
            var looked = CowAtLook(player, _config.MountDistance, out bool atHead);
            if (looked == null) return;
            bool headLead = atHead && (CanLeadIt(player, looked) || IsLeadingIt(player, looked));
            if (!headLead && !CanChoose(player, looked)) { TryRide(player, looked); return; }   // nothing to choose: ride on the press
            if (!_warmed.Contains(player.userID)) WarmIcons(player);
            _presses[player.userID] = new UsePress
            {
                Cow = looked,
                Since = now,
                HeadLead = headLead,
                WasLeading = looked.IsLeading() && looked.LeadingPlayer.Get(true) == player
            };
        }

        // Anything besides riding this cow offers the player: a hold opens the wheel only when there is a choice
        private static bool CanLeadIt(BasePlayer player, Cow cow) => cow.CanLead(player) && !(cow.IsLeading() && cow.LeadingPlayer.Get(true) != player);
        private static bool IsLeadingIt(BasePlayer player, Cow cow) => cow.IsLeading() && cow.LeadingPlayer.Get(true) == player;
        private static bool CanMilkIt(BasePlayer player, Cow cow) => cow.CanBeMilked(player) && cow.TrustsToHandle(player);
        private static bool CanChoose(BasePlayer player, Cow cow) =>
            cow.IsAdult() && (CanLeadIt(player, cow) || IsLeadingIt(player, cow) || CanMilkIt(player, cow));

        private Cow CowAtLook(BasePlayer player, float reach) => CowAtLook(player, reach, out _);

        // head: the look lands on the front of the cow (its head and neck), where the game's own E is Lead
        private Cow CowAtLook(BasePlayer player, float reach, out bool head)
        {
            head = false;
            var hits = Physics.RaycastAll(player.eyes.HeadRay(), reach, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var entity = hit.GetEntity();
                if (entity == player) continue;
                if (entity is Cow cow)
                {
                    head = cow.transform.InverseTransformPoint(hit.point).z >= _config.HeadZone;
                    return cow;
                }
                if (entity is BaseMountable seat && seat.net != null && _seatRides.TryGetValue(seat.net.ID.Value, out Ride ride)) return ride.Cow;
                if (entity is RidableHorse horse && horse.net != null && _carrierRides.TryGetValue(horse.net.ID.Value, out Ride carried)) return carried.Cow;
                return null;   // something else is in the way
            }
            return null;
        }

        private bool CanUse(BasePlayer player) =>
            !_config.RequirePermission || player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermRide);

        private bool IsAdmin(BasePlayer player) => player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin);

        private static string CowName(Cow cow)
        {
            string name = cow.AnimalName;
            return string.IsNullOrEmpty(name) ? (cow.IsMale ? "The bull" : "The cow") : name;
        }

        // Why this cow cannot be ridden right now, or null. May ask a lying or grazing cow to get up.
        private string RideBlocker(BasePlayer player, Cow cow, bool force)
        {
            if (cow == null || cow.IsDestroyed || cow.IsDead()) return "That cow is gone.";
            if (!force)
            {
                if (!CanUse(player)) return "You don't have permission to ride cows.";
                if (!player.CanMountMountablesNow()) return "You can't ride right now.";
                if (!cow.IsAdult()) return $"{CowName(cow)} is too young to ride.";
                if (cow.IsLeading() && cow.LeadingPlayer.Get(true) != player) return $"Someone is leading {CowName(cow)}.";
                if (cow.IsMating() || cow.IsBeingPurchased) return $"{CowName(cow)} is busy.";
                if (_config.RequireTrust && !cow.TrustsToHandle(player)) return $"{CowName(cow)} doesn't trust you enough to be ridden.";
            }
            if (!_rides.ContainsKey(cow.net.ID.Value) && (cow.IsOffItsFeet() || cow.IsHeadDown()))
            {
                // The livestock FSM redirects any state change to "get up" or "raise head" first
                var fsm = cow.GetComponent<LivestockAnimalFSM>();
                if (fsm != null) fsm.SetState(fsm.roam);
                return $"{CowName(cow)} is getting up. Try again in a moment.";
            }
            return null;
        }

        // Mount the player on the cow: as driver if the front seat is free, else as passenger
        private bool TryRide(BasePlayer player, Cow cow, bool force = false)
        {
            if (player.isMounted) return false;
            string blocker = RideBlocker(player, cow, force);
            if (blocker != null) { Reply(player, blocker); return false; }

            // E on a trusting cow is also vanilla's Lead: the rider's own lead just drops
            if (cow.IsLeading() && cow.LeadingPlayer.Get(true) == player) cow.StopLeading();

            _rides.TryGetValue(cow.net.ID.Value, out Ride ride);
            if (ride == null) ride = StartRide(cow);
            if (ride == null) { Reply(player, $"{CowName(cow)} can't be ridden here."); return false; }

            BaseMountable seat = null;
            if (ride.Front != null && ride.Front.GetMounted() == null) seat = ride.Front;
            else if (_config.RidersPerCow >= 2)
            {
                if (ride.Rear == null)
                {
                    // Behind the driver: with a carrier the driver's saddle sits further back than the method-1 seat
                    ride.RearSpot = _config.RearSeat.Vector;
                    if (ride.Carrier != null && ride.Front != null)
                        ride.RearSpot.z = Mathf.Min(ride.RearSpot.z, cow.transform.InverseTransformPoint(ride.Front.transform.position).z - 0.45f);
                    ride.Rear = SpawnSeat(ride, _config.RearSeatPrefab, ride.RearSpot);
                }
                if (ride.Rear != null && ride.Rear.GetMounted() == null) seat = ride.Rear;
            }
            if (seat == null)
            {
                Reply(player, _config.RidersPerCow >= 2 ? $"{CowName(cow)} is full." : $"{CowName(cow)} only carries one rider.");
                if (!AnyRider(ride)) EndRide(ride);
                return false;
            }

            seat.MountPlayer(player);
            if (seat.GetMounted() != player)
            {
                Reply(player, "You couldn't get on.");
                if (!AnyRider(ride)) EndRide(ride);
                return false;
            }
            ride.MountedAt = Time.realtimeSinceStartup;
            if (ride.Carrier != null && !ride.CarrierHidden)
            {
                // Hidden before the client has set up the saddle pose, the rider sits at a wrong angle
                ride.CarrierHidden = true;
                var carrier = ride.Carrier;
                timer.Once(_config.CarrierHideDelay, () => { if (carrier != null && !carrier.IsDestroyed) SetFlag(carrier, BaseEntity.Flags.Disabled, true); });
            }
            if (seat == ride.Front)
            {
                ride.Heading = cow.transform.eulerAngles.y;
                if (_config.ShowHints) ShowHint(player, "W go · Shift faster · Ctrl slower · S stop · A/D turn · E get off" +
                                                       (ChargeAllowed(cow) ? " · Shift near a target: charge" : "") +
                                                       " · double-tap Ctrl: kick" + (_mooButton.HasValue ? $" · {KeyName(_mooButton.Value)}: moo" : ""));
            }
            return true;
        }

        // Take the cow off its AI and put the driver's seat on it
        private Ride StartRide(Cow cow)
        {
            var agent = cow.GetComponent<RustNavMeshAgent>();
            var fsm = cow.GetComponent<FSMComponent>();
            if (agent == null || fsm == null) return null;

            var ride = new Ride { Cow = cow, Agent = agent, Fsm = fsm, Anim = cow.GetComponent<RootMotionPlayer>(), Heading = cow.transform.eulerAngles.y };

            // Leave whatever the FSM was doing through its exit (stops grazing/idle montages and paths), then park it
            if (fsm is LivestockAnimalFSM livestockFsm) fsm.SetState(livestockFsm.roam);
            fsm.SetFsmActive(false);
            StopMontage(cow);
            agent.ResetPath();

            if (_config.RideMethod == 2) ride.Front = SpawnCarrier(ride);
            else ride.Front = SpawnSeat(ride, _config.FrontSeatPrefab, _config.FrontSeat.Vector);
            if (ride.Front == null) { ReleaseCow(ride); return null; }
            _rides[cow.net.ID.Value] = ride;
            if (ride.Carrier != null && _carrierTimer == null) _carrierTimer = timer.Every(0.05f, TickCarriers);
            return ride;
        }

        private static void SetFlag(BaseEntity entity, BaseEntity.Flags flag, bool on)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdateImmediate)) scope.Set(flag, on);
        }

        private Vector3 CarrierPoint(Ride ride) => ride.Cow.transform.TransformPoint(_config.CarrierSpot.Vector) + Vector3.up * _config.CarrierLift;

        // Method 2: a real horse, carried above the cow (never overlapping it: its body would shove the cow on
        // the client at every stop), kinematic, its own fixed update skipped, not parented (the client never
        // follows a parented horse) but placed on the cow 20 times a second. Its saddle seat is dropped back
        // onto the cow's back in world space (the seat hangs under a horse bone, so local axes are the bone's).
        // The rider mounted on it gets the game's horse stamina and health bars, fed from the cow's numbers.
        private BaseMountable SpawnCarrier(Ride ride)
        {
            var cow = ride.Cow;
            var horse = GameManager.server.CreateEntity(HorsePrefab, CarrierPoint(ride), cow.transform.rotation) as RidableHorse;
            if (horse == null) { PrintError($"{HorsePrefab} is not a RidableHorse; falling back to seats"); return SpawnSeat(ride, _config.FrontSeatPrefab, _config.FrontSeat.Vector); }
            horse.enableSaving = false;
            horse.Spawn();
            if (horse.rigidBody != null) horse.rigidBody.isKinematic = true;
            Carriers.Add(horse);
            var seat = horse.mountPoints != null && horse.mountPoints.Count > 0 ? horse.mountPoints[0].mountable : null;
            if (seat == null)
            {
                PrintError("Carrier horse has no saddle mount point; falling back to seats");
                Carriers.Remove(horse);
                horse.Kill();
                return SpawnSeat(ride, _config.FrontSeatPrefab, _config.FrontSeat.Vector);
            }
            seat.transform.position += Vector3.down * _config.CarrierLift;
            seat.SendNetworkUpdateImmediate();
            horse.InitializeHealth(cow.health, cow.MaxHealth());
            horse.currentMaxStamina = horse.maxStamina;
            horse.currentStamina = horse.maxStamina;
            ride.Carrier = horse;
            ride.LastCowPos = cow.transform.position;
            _carrierRides[horse.net.ID.Value] = ride;
            _seatRides[seat.net.ID.Value] = ride;
            return seat;
        }

        // 20 Hz: keep each carrier on its cow's back and send cow and horse positions from the same frame
        // (two separately interpolated entities otherwise drift apart under the rider)
        private void TickCarriers()
        {
            bool any = false;
            foreach (var ride in _rides.Values)
            {
                var horse = ride.Carrier;
                var cow = ride.Cow;
                if (horse == null || horse.IsDestroyed || cow == null || cow.IsDestroyed) continue;
                any = true;
                var t = horse.transform;
                if (horse.rigidBody != null && !horse.rigidBody.isKinematic) horse.rigidBody.isKinematic = true;
                Vector3 wp = CarrierPoint(ride);
                Quaternion wr = cow.transform.rotation;
                if ((t.position - wp).sqrMagnitude > 0.00001f || Quaternion.Angle(t.rotation, wr) > 0.2f)
                {
                    t.SetPositionAndRotation(wp, wr);
                    if (horse.rigidBody != null) { horse.rigidBody.position = wp; horse.rigidBody.rotation = wr; }
                }
                if (cow.transform.hasChanged) cow.NetworkPositionTick();
                if (t.hasChanged) horse.NetworkPositionTick();
                // The game's bars read the horse: feed them the cow's health and the ride's stamina
                horse.currentMaxStamina = horse.maxStamina;
                horse.currentStamina = ride.Stamina * horse.maxStamina;
            }
            if (!any && _carrierRides.Count == 0) { _carrierTimer?.Destroy(); _carrierTimer = null; }
        }

        private void MirrorCarrierHealth(Ride ride)
        {
            var horse = ride.Carrier;
            if (horse == null || horse.IsDestroyed) return;
            if (Mathf.Abs(horse.MaxHealth() - ride.Cow.MaxHealth()) < 0.01f && Mathf.Abs(horse.health - ride.Cow.health) < 0.5f) return;
            horse.InitializeHealth(ride.Cow.health, ride.Cow.MaxHealth());
            horse.SendNetworkUpdate();
        }

        // Nothing hurts the carrier itself (it hangs in the air above the cow): the cow takes the damage
        private object OnEntityTakeDamage(RidableHorse horse, HitInfo info)
        {
            if (horse == null || !Carriers.Contains(horse)) return null;
            if (horse.net != null && _carrierRides.TryGetValue(horse.net.ID.Value, out Ride ride) && ride.Cow != null && !ride.Cow.IsDestroyed && info != null
                && info.Initiator != ride.Cow && info.damageTypes != null && info.damageTypes.Total() > 0f)
                ride.Cow.Hurt(info.damageTypes.Total(), info.damageTypes.GetMajorityDamageType(), info.Initiator, true);
            return true;
        }

        private BaseMountable SpawnSeat(Ride ride, string prefab, Vector3 local)
        {
            var cow = ride.Cow;
            var seat = GameManager.server.CreateEntity(prefab, cow.transform.TransformPoint(local), cow.transform.rotation) as BaseMountable;
            if (seat == null) { PrintError($"Seat prefab {prefab} is not a mountable"); return null; }
            seat.enableSaving = false;
            // The seat moves on the cow's back as the ground tilts: let the engine send its position (must be set before Spawn)
            if (_config.SeatFollowsGround) seat.syncPosition = true;
            seat.SetParent(cow, worldPositionStays: true);
            seat.Spawn();
            _seatRides[seat.net.ID.Value] = ride;
            return seat;
        }

        // A montage still playing pauses the cow's agent: stop it (without freeing it; its state owns it)
        private void StopMontage(Cow cow)
        {
            var player = cow.GetComponent<RootMotionPlayer>();
            if (player == null || PlayStateField == null || StopServerMethod == null) return;
            var state = PlayStateField.GetValue(player);
            if (state != null) StopServerMethod.Invoke(player, new[] { state, (object)true });
        }

        private static bool AnyRider(Ride ride) =>
            (ride.Front != null && ride.Front.GetMounted() != null) || (ride.Rear != null && ride.Rear.GetMounted() != null);

        // Everyone off, seats gone, cow back on its AI
        private void EndRide(Ride ride)
        {
            if (ride.Ending) return;
            ride.Ending = true;
            if (ride.Charge != null && ride.Anim != null) ride.Anim.StopServerAndReturnToPool(ref ride.Charge);
            ride.Charge = null;
            if (ride.Kick != null && ride.Anim != null) ride.Anim.StopServerAndReturnToPool(ref ride.Kick);
            ride.Kick = null;
            if (ride.Moo != null && ride.Anim != null) ride.Anim.StopServerAndReturnToPool(ref ride.Moo);
            ride.Moo = null;
            ClearIdle(ride);
            foreach (var seat in new[] { ride.Front, ride.Rear })
            {
                if (seat == null || seat.IsDestroyed) continue;
                var rider = seat.GetMounted();
                if (rider != null) GetOff(ride, seat, rider);
            }
            foreach (var seat in new[] { ride.Front, ride.Rear })
            {
                if (seat == null) continue;
                if (seat.net != null) _seatRides.Remove(seat.net.ID.Value);
                if (!seat.IsDestroyed) seat.Kill();
            }
            ride.Front = ride.Rear = null;
            if (ride.Carrier != null)
            {
                var horse = ride.Carrier;
                ride.Carrier = null;
                Carriers.Remove(horse);
                if (horse.net != null) _carrierRides.Remove(horse.net.ID.Value);
                if (!horse.IsDestroyed) horse.Kill();
            }
            if (ride.Cow != null && ride.Cow.net != null) _rides.Remove(ride.Cow.net.ID.Value);
            ClearIdle(ride);
            ReleaseCow(ride);
        }

        private static void ReleaseCow(Ride ride)
        {
            EndReverse(ride);
            var cow = ride.Cow;
            if (cow == null || cow.IsDestroyed || cow.IsDead()) return;
            if (ride.Agent != null) ride.Agent.canSteer = true;
            ride.Agent?.ResetPath();
            ride.Fsm?.SetFsmActive(true);
        }

        // Put the rider on the ground beside the cow. Vanilla's dismount kills a rider it can't place.
        private void GetOff(Ride ride, BaseMountable seat, BasePlayer player)
        {
            Vector3 spot;
            if (seat.GetParentEntity() is RidableHorse || !seat.GetDismountPosition(player, out spot, silent: true))
            {
                var cow = ride.Cow.transform;
                spot = cow.position;
                foreach (var side in new[] { cow.right * 1.3f, -cow.right * 1.3f, -cow.forward * 1.8f, cow.forward * 1.8f })
                {
                    Vector3 candidate = cow.position + side;
                    if (Physics.Raycast(candidate + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 6f, LayerMask.GetMask("Terrain", "World", "Construction", "Default"))
                        && !Physics.CheckCapsule(ground.point + Vector3.up * 0.5f, ground.point + Vector3.up * 1.3f, 0.4f, LayerMask.GetMask("World", "Construction", "Default")))
                    {
                        spot = ground.point + Vector3.up * 0.1f;
                        break;
                    }
                }
            }
            _mountCooldown[player.userID] = Time.realtimeSinceStartup + 1f;
            seat.DismountPlayer(player, true);
            player.SetParent(null, true, true);
            player.MovePosition(spot);
            player.ClientRPC(RpcTarget.Player("ForcePositionTo", player), spot);
            player.SendNetworkUpdateImmediate();
            player.PauseVehicleNoClipDetection(5f);
            player.PauseFlyHackDetection(5f);
            player.PauseSpeedHackDetection(5f);
            ShowHint(player, null);
            ShowBars(player, null);
        }

        private Ride RideOfSeat(BaseMountable seat) =>
            seat != null && seat.net != null && _seatRides.TryGetValue(seat.net.ID.Value, out Ride ride) ? ride : null;

        private object OnPlayerWantsDismount(BasePlayer player, BaseMountable seat)
        {
            var ride = RideOfSeat(seat);
            if (ride == null) return null;
            GetOff(ride, seat, player);
            return true;
        }

        private void OnPlayerDismountFailed(BasePlayer player, BaseMountable seat)
        {
            var ride = RideOfSeat(seat);
            if (ride != null && seat.GetMounted() == player) GetOff(ride, seat, player);
        }

        // Every way off (dismount, death, disconnect) ends here: free the cow when nobody is left
        private void OnEntityDismounted(BaseMountable seat, BasePlayer player)
        {
            var ride = RideOfSeat(seat);
            if (ride == null || ride.Ending) return;
            _mountCooldown[player.userID] = Time.realtimeSinceStartup + 1f;
            ShowBars(player, null);
            NextTick(() =>
            {
                if (ride.Ending) return;
                if (!AnyRider(ride)) { EndRide(ride); return; }
                // The driver got off: the passenger stays on, the cow stops
                if (ride.Front == null || ride.Front.GetMounted() == null) { ride.Input = Keys.None; ride.InputPinned = false; }
            });
        }

        // ------------------------------------------------------------------ the Lead / Ride wheel

        // The game's own pie menu, filled by the server: each slice runs a console command on the client,
        // which reaches our console commands here. Closing the pie without a choice runs the close command.
        private void OpenWheel(BasePlayer player, UsePress press)
        {
            press.WheelOpen = true;
            press.WheelOpenedAt = Time.realtimeSinceStartup;
            ShowHint(player, null);
            var community = CommunityEntity.ServerInstance;
            if (community == null) { PrintWarning("No CommunityEntity on the server: cannot open a pie menu"); CloseWheel(player, cancel: false); return; }
            SendWheel(player, press.Cow, press.WasLeading);
        }

        private string Icon(string slice) => _config.PieIcons.TryGetValue(slice, out string path) ? path ?? "" : "";

        private void ReplaceWheel(BasePlayer player, string why)
        {
            var community = CommunityEntity.ServerInstance;
            if (community == null || player == null || !player.IsConnected) return;
            using (var pie = Facepunch.Pool.Get<CustomPie>())
            {
                pie.closeCommand = "";
                pie.menus = Facepunch.Pool.Get<List<CustomPieMenu>>();
                pie.menus.Add(Slice("Close", why, "ridablecows.wheel cancel", Icon("cancel"), 0, selected: true, icon: "cancel"));
                community.SendPie(player, pie);
            }
        }

        // Our own pie icons: PNG files in oxide/data/RidableCows/icons/<slice>.png (ride, lead, stop, milk, cancel),
        // put into the game's file store at every load (the store does not survive a restart) and sent to the
        // wheel by id; the client fetches them from the server. A slice without a file uses its sprite path.
        private string IconDir => Path.Combine(Interface.Oxide.DataDirectory, "RidableCows", "icons");

        private void LoadIcons()
        {
            _iconIds.Clear();
            var community = CommunityEntity.ServerInstance;
            if (community == null || community.net == null) { PrintWarning("No CommunityEntity: custom pie icons unavailable"); return; }
            try { Directory.CreateDirectory(IconDir); } catch { }
            foreach (string slice in new[] { "ride", "lead", "stop", "milk", "cancel" })
            {
                string file = Path.Combine(IconDir, slice + ".png");
                if (!File.Exists(file)) continue;
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    uint crc = FileStorage.server.Store(bytes, FileStorage.Type.png, community.net.ID);
                    if (crc != 0) _iconIds[slice] = crc;
                }
                catch (Exception ex) { PrintWarning($"Icon {file} not loaded: {ex.Message}"); }
            }
            Puts($"Pie icons from {IconDir}: {_iconIds.Count} custom ({string.Join(", ", _iconIds.Keys)}), the rest from sprite paths");
            foreach (var player in BasePlayer.activePlayerList) WarmIcons(player);
        }

        // The pie is drawn once, with whatever sprites the client already has: an image it has not fetched
        // yet shows as a missing-icon mark. CUI images, on the other hand, are filled in when the bytes
        // arrive. So each client is shown our icons once, invisibly, which fetches them into its cache
        // (and onto its disk); every wheel after that finds them at once.
        private readonly HashSet<ulong> _warmed = new HashSet<ulong>();

        private void WarmIcons(BasePlayer player)
        {
            if (player == null || !player.IsConnected || _iconIds.Count == 0) return;
            _warmed.Add(player.userID);
            CuiHelper.DestroyUi(player, WarmPanel);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.002 0.002" } }, "Hud", WarmPanel);
            foreach (var pair in _iconIds)
                ui.Add(new CuiElement
                {
                    Parent = WarmPanel,
                    Components =
                    {
                        new CuiRawImageComponent { Png = pair.Value.ToString(), Color = "1 1 1 0" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });
            CuiHelper.AddUi(player, ui);
            timer.Once(8f, () => { if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, WarmPanel); });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player != null && !_warmed.Contains(player.userID)) WarmIcons(player);
        }

        // The wheel offers what this cow offers this player right now: ride, lead or stop leading, milk
        private void SendWheel(BasePlayer player, Cow cow, bool wasLeading)
        {
            var community = CommunityEntity.ServerInstance;
            if (community == null) return;
            string animal = cow.IsMale ? "bull" : "cow";
            using (var pie = Facepunch.Pool.Get<CustomPie>())
            {
                // No close command: the client runs it before the chosen slice's command (seconds before, at
                // times), so acting on it would throw the choice away. The press simply expires instead.
                pie.closeCommand = "";
                pie.menus = Facepunch.Pool.Get<List<CustomPieMenu>>();
                int order = 0;
                pie.menus.Add(Slice($"Ride {animal}", $"Get on {CowName(cow)}'s back", "ridablecows.wheel ride", Icon("ride"), order++, selected: true, icon: "ride"));
                if (wasLeading || IsLeadingIt(player, cow))
                    pie.menus.Add(Slice("Stop leading", $"Let go of {CowName(cow)}", "ridablecows.wheel lead", Icon("stop"), order++, icon: "stop"));
                else if (CanLeadIt(player, cow))
                    pie.menus.Add(Slice($"Lead {animal}", $"Walk {CowName(cow)} on a lead", "ridablecows.wheel lead", Icon("lead"), order++, icon: "lead"));
                if (CanMilkIt(player, cow))
                    pie.menus.Add(Slice("Milk", $"Milk {CowName(cow)}", "ridablecows.wheel milk", Icon("milk"), order++, icon: "milk"));
                pie.menus.Add(Slice("Cancel", null, "ridablecows.wheel cancel", Icon("cancel"), order, icon: "cancel"));
                community.SendPie(player, pie);
            }
        }

        private CustomPieMenu Slice(string name, string description, string command, string sprite, int order, bool selected = false, string icon = null)
        {
            var slice = Facepunch.Pool.Get<CustomPieMenu>();
            slice.name = name;
            slice.description = description ?? "";
            slice.command = command;
            if (icon != null && _iconIds.TryGetValue(icon, out uint id)) { slice.imageId = id; slice.sprite = ""; }
            else slice.sprite = sprite ?? "";
            slice.order = order;
            slice.selected = selected;
            slice.nextCommand = "";
            slice.prevCommand = "";
            slice.disabledCommand = "";
            return slice;
        }

        // cancel: undo a lead the game's own E press may have started under the wheel
        private void CloseWheel(BasePlayer player, bool cancel)
        {
            if (!_presses.TryGetValue(player.userID, out UsePress press)) return;
            _presses.Remove(player.userID);
            var cow = press.Cow;
            if (cancel && !press.WasLeading && cow != null && !cow.IsDestroyed && cow.IsLeading() && cow.LeadingPlayer.Get(true) == player) cow.StopLeading();
            if (press.WheelOpen) _mountCooldown[player.userID] = Time.realtimeSinceStartup + 0.3f;
        }

        [ConsoleCommand("ridablecows.wheel")]
        private void CmdWheel(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string choice = arg.GetString(0, "cancel").ToLower();
            _presses.TryGetValue(player.userID, out UsePress press);
            if (_config.LogWheel) Puts($"[wheel] t={Time.realtimeSinceStartup:0.00} {player.displayName}: {choice} (press {(press == null ? "none" : $"cow {press.Cow?.net?.ID} open={press.WheelOpen} wasLeading={press.WasLeading}")})");
            if (press == null || !press.WheelOpen) return;
            var cow = press.Cow;
            bool wasLeading = press.WasLeading;
            CloseWheel(player, cancel: choice == "cancel");
            if (cow == null || cow.IsDestroyed || cow.IsDead()) { if (_config.LogWheel) Puts("[wheel] cow gone"); return; }
            switch (choice)
            {
                case "lead":
                    if (wasLeading)
                    {
                        if (cow.TryLead(player, false)) Reply(player, $"You let go of {CowName(cow)}.");
                    }
                    else if (cow.IsLeading() && cow.LeadingPlayer.Get(true) == player) Reply(player, $"You are leading {CowName(cow)}.");
                    else if (cow.TryLead(player, true)) Reply(player, $"You are leading {CowName(cow)}.");
                    else Reply(player, $"{CowName(cow)} won't be led by you.");
                    return;
                case "ride":
                {
                    bool on = TryRide(player, cow);
                    if (_config.LogWheel) Puts($"[wheel] ride -> {on}, mounted on {player.GetMounted()?.ShortPrefabName ?? "-"}");
                    return;
                }
                case "milk":
                    if (!cow.CanBeMilked(player)) Reply(player, $"{CowName(cow)} has no milk right now.");
                    else if (cow.TryMilk(player)) Reply(player, $"You milked {CowName(cow)}.");
                    else Reply(player, $"{CowName(cow)} won't let you.");
                    return;
            }
        }

        // ------------------------------------------------------------------ steering

        private void TickRides()
        {
            if (_rides.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            foreach (var ride in new List<Ride>(_rides.Values))
            {
                var cow = ride.Cow;
                if (cow == null || cow.IsDestroyed || cow.IsDead()) { EndRide(ride); continue; }
                if (!AnyRider(ride)) { EndRide(ride); continue; }

                // Something (an AI zone waking up, another plugin) may have restarted the FSM
                if (FsmRunningField != null && (bool)FsmRunningField.GetValue(ride.Fsm)) ride.Fsm.SetFsmActive(false);
                // The same E press can arrive as a Lead RPC after the mount: nobody leads a cow they sit on
                if (cow.IsLeading())
                {
                    var leader = cow.LeadingPlayer.Get(true);
                    if (leader != null && (leader == ride.Front?.GetMounted() || leader == ride.Rear?.GetMounted())) cow.StopLeading();
                }

                Keys keys = ride.Input;
                if (!ride.InputPinned && (ride.Front?.GetMounted() == null || now - ride.InputTime > 0.5f)) keys = Keys.None;
                TickStamina(ride, TickInterval);
                Steer(ride, keys, TickInterval);
                if (ride.Charge == null) TickRam(ride, now);
                if (_config.SeatFollowsGround) FollowGround(ride);
                if (ride.Carrier != null) MirrorCarrierHealth(ride);
            }
        }

        private void Steer(Ride ride, Keys keys, float dt)
        {
            if (ride.Moo != null && !ride.Moo.isPlaying) ride.Anim.StopServerAndReturnToPool(ref ride.Moo, interrupt: false);
            // A charge or a kick owns the cow until its animation ends
            if (ride.Charge != null)
            {
                TickCharge(ride);
                if (ride.Charge != null) return;
            }
            if (ride.Kick != null)
            {
                TickKick(ride);
                if (ride.Kick != null) return;
            }
            if (ride.KickRequested) { ride.KickRequested = false; if (TryStartKick(ride)) return; }
            if (ride.MooRequested) { ride.MooRequested = false; TryMoo(ride); }

            int turn = ((keys & Keys.Right) != 0 ? 1 : 0) - ((keys & Keys.Left) != 0 ? 1 : 0);
            bool forward = (keys & Keys.Forward) != 0 && (keys & Keys.Back) == 0;
            bool back = (keys & Keys.Back) != 0 && (keys & Keys.Forward) == 0;
            bool faster = (keys & Keys.Faster) != 0;
            if (back && _config.ReverseSpeed > 0f)
            {
                Reverse(ride, turn, dt);
                return;
            }
            // A/D swing the heading, but only so far ahead of the cow: past that it would chase a target
            // behind it and wobble. The cow detouring round a rock never drags the heading back.
            if (turn != 0)
            {
                float yaw = ride.Cow.transform.eulerAngles.y;
                float maxLead = forward ? 45f : 75f;
                float lead = Mathf.DeltaAngle(yaw, ride.Heading);
                float wanted = lead + turn * _config.TurnRate * dt;
                if (turn > 0 && wanted > maxLead) wanted = Mathf.Max(lead, maxLead);
                if (turn < 0 && wanted < -maxLead) wanted = Mathf.Min(lead, -maxLead);
                ride.Heading = Mathf.Repeat(yaw + wanted, 360f);
            }

            // Shift with something to hit in front: charge it instead of sprinting
            if (faster && TryStartCharge(ride)) return;

            if (!forward && turn == 0)
            {
                SoftStop(ride);
                return;
            }
            ClearIdle(ride);   // an idle clip pauses the agent; the driver wants to move
            EndReverse(ride);
            if (ride.Stopping) { ride.Stopping = false; ride.Agent.canSteer = true; }

            var fastest = ParseGait(_config.FastGait, RustNavMeshAgent.Speeds.Sprint);
            var cruise = ParseGait(_config.CruiseGait, RustNavMeshAgent.Speeds.Jog);
            if (ride.Tired && fastest > cruise) fastest = cruise;   // out of breath: no more than the cruising gait
            RustNavMeshAgent.Speeds gait;
            if (!forward) gait = RustNavMeshAgent.Speeds.Walk;   // turning on the spot
            else if ((keys & Keys.Slower) != 0) gait = ParseGait(_config.SlowGait, RustNavMeshAgent.Speeds.Walk);
            else if (faster) gait = fastest;
            else gait = cruise;
            if (gait > fastest) gait = fastest;

            float now = Time.realtimeSinceStartup;
            if (now < ride.BlockedUntil) return;
            float speed = ride.Agent.GetSpeedForGait(gait);
            Vector3 origin = ride.Cow.transform.position;
            float reach = forward ? Mathf.Max(3f, speed * 1.5f) : 1.5f;
            // Straight ahead first; if that is a wall, rock or drop, slide along it a little, toward the side
            // the rider is turning to first
            var deviations = turn < 0 ? DeviationsLeft : DeviationsRight;
            foreach (float scale in Reaches)
                foreach (float angle in deviations)
                {
                    Vector3 target = origin + Quaternion.Euler(0f, ride.Heading + angle, 0f) * Vector3.forward * (reach * scale);
                    if (!SetTarget(ride, target, gait)) continue;
                    ride.Moving = true;
                    ride.LastGait = gait;
                    ride.Gait = gait.ToString();
                    return;
                }
            HardStop(ride);   // nothing walkable that way: water, a cliff, a wall
            ride.Gait = "Blocked";
            ride.BlockedUntil = now + 0.3f;
        }

        private static readonly float[] Reaches = { 1f, 0.5f };
        private static readonly float[] DeviationsRight = { 0f, 25f, -25f, 50f, -50f };
        private static readonly float[] DeviationsLeft = { 0f, -25f, 25f, -50f, 50f };

        // The target is at the cow's height; on a slope that is above or under the ground. Snap it onto the
        // navmesh, and reject snaps that land somewhere else (the far side of a wall, across water).
        private static bool SetTarget(Ride ride, Vector3 target, RustNavMeshAgent.Speeds gait)
        {
            if (!ride.Agent.SamplePosition(target, out UnityEngine.AI.NavMeshHit hit, 3f, false))
            {
                target.y = TerrainMeta.HeightMap.GetHeight(target);
                if (!ride.Agent.SamplePosition(target, out hit, 2f, false)) return false;
            }
            if ((hit.position - target).WithY(0f).sqrMagnitude > 1.5f * 1.5f) return false;
            return ride.Agent.SetDestinationWithParams(hit.position, autoBraking: false, gait: gait);
        }

        // The driver let go: ease to a halt (a path cut dead makes the client's animator lurch), then settle
        // with one of the cow's own idle clips, as its state machine does on arrival
        private static void EndReverse(Ride ride)
        {
            if (!ride.Reversing) return;
            ride.Reversing = false;
            if (ride.Agent != null) ride.Agent.canSteer = true;
        }

        private void SoftStop(Ride ride)
        {
            EndReverse(ride);
            if (ride.Moving)
            {
                // The driver let go: ramp the speed down ourselves. Asking the agent to brake to a point a metre
                // or two ahead does not work (it counts a target that close as reached and halts dead), so the
                // path is dropped and the cow is moved along the mesh each tick at a decaying speed, steering
                // off so the agent does not swing it about. The client animates from the velocity, so it eases.
                ride.RampSpeed = Mathf.Max(CurrentSpeed(ride), 0.3f);
                ride.Agent.ResetPath();
                ride.Agent.canSteer = false;
                ride.Moving = false;
                ride.Stopping = true;
            }
            else if (ride.Stopping)
            {
                ride.RampSpeed = Mathf.Max(0f, ride.RampSpeed - _config.StopDeceleration * TickInterval);
                var t = ride.Cow.transform;
                if (ride.RampSpeed > 0f && !ride.Agent.IsPaused)
                {
                    Vector3 from = t.position;
                    Vector3 to = from + t.forward * (ride.RampSpeed * TickInterval);
                    ride.Agent.Move(ride.Agent.WorldToNavSpace(to) - ride.Agent.WorldToNavSpace(from));
                }
                else
                {
                    ride.Stopping = false;
                    ride.Agent.canSteer = true;
                    if (_config.IdleClipOnStop) PlayIdle(ride);
                }
            }
            ride.Gait = "Stop";
        }

        private static void HardStop(Ride ride)
        {
            EndReverse(ride);
            ride.Agent.ResetPath();
            ride.Agent.canSteer = true;
            ride.Moving = false;
            ride.Stopping = false;
            ride.Gait = "Stop";
        }

        private void PlayIdle(Ride ride)
        {
            var fsm = ride.Fsm as LivestockAnimalFSM;
            if (ride.Anim == null || fsm == null || fsm.randomIdle == null || fsm.randomIdle.animations == null || fsm.randomIdle.animations.Length == 0) return;
            var clip = fsm.randomIdle.animations[UnityEngine.Random.Range(0, fsm.randomIdle.animations.Length)];
            if (clip == null) return;
            ClearIdle(ride);
            ride.Idle = ride.Anim.PlayServerAndTakeFromPool(clip);
        }

        private static void ClearIdle(Ride ride)
        {
            if (ride.Idle == null) return;
            if (ride.Anim != null) ride.Anim.StopServerAndReturnToPool(ref ride.Idle, interrupt: ride.Idle.isPlaying);
            ride.Idle = null;
        }

        // S: back up. Cows have no reverse gait on the navmesh, so the agent is moved straight backwards along the
        // mesh without a path, and A/D turn it on the spot
        private void Reverse(Ride ride, int turn, float dt)
        {
            ClearIdle(ride);
            if (ride.Moving || ride.Stopping) { ride.Agent.ResetPath(); ride.Moving = false; ride.Stopping = false; }
            if (!ride.Reversing) { ride.Reversing = true; ride.Agent.canSteer = false; }   // steering would swing it round to face the move
            var t = ride.Cow.transform;
            if (turn != 0)
            {
                t.rotation = Quaternion.Euler(0f, t.eulerAngles.y + turn * _config.TurnRate * 0.6f * dt, 0f);
            }
            ride.Heading = t.eulerAngles.y;
            if (!ride.Agent.IsPaused)
            {
                Vector3 from = t.position;
                Vector3 to = from - t.forward * (_config.ReverseSpeed * dt);
                ride.Agent.Move(ride.Agent.WorldToNavSpace(to) - ride.Agent.WorldToNavSpace(from));
            }
            ride.Gait = "Reverse";
        }




        private static RustNavMeshAgent.Speeds ParseGait(string name, RustNavMeshAgent.Speeds fallback) =>
            Enum.TryParse(name, true, out RustNavMeshAgent.Speeds gait) ? gait : fallback;

        private static float CurrentSpeed(Ride ride) =>
            CurrentSpeedField != null ? (float)CurrentSpeedField.GetValue(ride.Agent) : 0f;

        // ------------------------------------------------------------------ stamina

        // Like a horse: sprinting empties the bar, anything slower refills it, and an empty bar means no
        // sprinting (or charging) until it is part way back
        private void TickStamina(Ride ride, float dt)
        {
            float change;
            switch (ride.Gait)
            {
                case "Sprint":
                case "FullSprint": change = -dt / _config.SprintSeconds; break;
                case "Run": change = -dt / (2f * _config.SprintSeconds); break;
                case "Charge":
                case "Kick": change = 0f; break;
                case "Stop":
                case "Blocked": change = 2f * dt / _config.RecoverSeconds; break;
                case "Reverse": change = dt / _config.RecoverSeconds; break;
                default: change = dt / _config.RecoverSeconds; break;
            }
            ride.Stamina = Mathf.Clamp01(ride.Stamina + change);
            if (ride.Stamina <= 0f) ride.Tired = true;
            else if (ride.Tired && ride.Stamina >= _config.TiredRecover) ride.Tired = false;
        }

        // ------------------------------------------------------------------ ramming and charging

        private bool ChargeAllowed(Cow cow)
        {
            switch ((_config.ChargeWho ?? "All").ToLower())
            {
                case "none": return false;
                case "bulls": return cow.IsMale;
                default: return true;
            }
        }

        // The charge animation: the config's asset if it loads, else the cow's own headbutt, else its kick
        private RootMotionData ChargeAnimation(Ride ride)
        {
            if (!string.IsNullOrEmpty(_config.ChargeAnimation))
            {
                if (!_chargeAnimTried)
                {
                    _chargeAnimTried = true;
                    try { _chargeAnimOverride = FileSystem.Load<RootMotionData>(_config.ChargeAnimation); }
                    catch (Exception ex) { PrintWarning($"Charge animation '{_config.ChargeAnimation}' did not load: {ex.Message}"); }
                    if (_chargeAnimOverride == null || _chargeAnimOverride.inPlaceAnimation == null)
                    {
                        PrintWarning($"Charge animation '{_config.ChargeAnimation}' not found; using the cow's own");
                        _chargeAnimOverride = null;
                    }
                }
                if (_chargeAnimOverride != null) return _chargeAnimOverride;
            }
            var fsm = ride.Fsm as LivestockAnimalFSM;
            if (fsm == null) return null;
            if (fsm.headbutt != null && fsm.headbutt.HasAnimation) return fsm.headbutt.Animation;
            if (fsm.kick != null && fsm.kick.HasAnimation) return fsm.kick.Animation;
            return null;
        }

        private float ChargeHitDelay(Ride ride)
        {
            if (_config.ChargeHitDelay > 0f) return _config.ChargeHitDelay;
            var fsm = ride.Fsm as LivestockAnimalFSM;
            if (fsm != null && fsm.headbutt != null && fsm.headbutt.HasAnimation && fsm.headbutt.damageDelay > 0f) return fsm.headbutt.damageDelay;
            return 0.5f;
        }

        private bool TryStartCharge(Ride ride)
        {
            var cow = ride.Cow;
            float now = Time.realtimeSinceStartup;
            if (ride.Anim == null || now < ride.ChargeReadyAt || ride.Tired || ride.Stamina < _config.ChargeStaminaCost) return false;
            if (!ChargeAllowed(cow)) return false;
            var anim = ChargeAnimation(ride);
            if (anim == null) return false;
            var target = NearestTarget(ride, _config.ChargeRange, _config.ChargeAngle);
            if (target == null) return false;

            ClearIdle(ride);
            HardStop(ride);
            Vector3 to = (target.ClosestPoint(cow.transform.position) - cow.transform.position).WithY(0f);
            if (to.sqrMagnitude > 0.01f) cow.transform.rotation = Quaternion.LookRotation(to.normalized);
            ride.Charge = ride.Anim.PlayServerAndTakeFromPool(anim);   // pauses the agent, moves the cow by root motion, ends on its own
            if (ride.Charge == null) return false;
            ride.ChargeHitAt = now + ChargeHitDelay(ride);
            ride.ChargeVictims.Clear();
            ride.Charges++;
            ride.Stamina = Mathf.Max(0f, ride.Stamina - _config.ChargeStaminaCost);
            float length = anim.inPlaceAnimation != null ? anim.inPlaceAnimation.length : 1f;
            ride.ChargeReadyAt = now + length + _config.ChargeCooldown;
            ride.Gait = "Charge";
            return true;
        }

        private void TickCharge(Ride ride)
        {
            float now = Time.realtimeSinceStartup;
            // The blow: from the moment the head comes down to the end of the lunge, anything the cow
            // reaches is hit once. The root motion carries it a few metres, so a cone at one instant
            // would miss what it just ran over (the game's own headbutt is a plain radius too).
            if (now >= ride.ChargeHitAt)
                foreach (var target in TargetsAhead(ride, _config.ChargeHitRange, 180f))
                {
                    if (!ride.ChargeVictims.Add(target.net?.ID.Value ?? 0)) continue;
                    Hit(ride, target, _config.ChargePlayerDamage, _config.ChargeStructureDamage, 1f, 12f);
                }
            if (ride.Charge.isPlaying && now < ride.ChargeHitAt + 6f) return;
            // Finished (or stuck): free the state, the cow is ours again
            ride.Anim.StopServerAndReturnToPool(ref ride.Charge, interrupt: ride.Charge.isPlaying);
            ride.Charge = null;
            ride.Heading = ride.Cow.transform.eulerAngles.y;
            ride.Gait = "Stop";
        }

        // Double-tap Ctrl: the cow's own kick (its stand-attack), hitting what is in front of it
        private bool TryStartKick(Ride ride)
        {
            float now = Time.realtimeSinceStartup;
            if (ride.Anim == null || now < ride.KickReadyAt || ride.Tired || ride.Stamina < _config.KickStaminaCost) return false;
            var fsm = ride.Fsm as LivestockAnimalFSM;
            if (fsm == null || fsm.kick == null || !fsm.kick.HasAnimation) return false;
            ClearIdle(ride);
            HardStop(ride);
            if (ride.Moo != null) ride.Anim.StopServerAndReturnToPool(ref ride.Moo);
            ride.Kick = ride.Anim.PlayServerAndTakeFromPool(fsm.kick.Animation);
            if (ride.Kick == null) return false;
            ride.KickHitAt = now + (fsm.kick.Delay > 0f ? fsm.kick.Delay : 0.5f);
            ride.KickHit = false;
            ride.Stamina = Mathf.Max(0f, ride.Stamina - _config.KickStaminaCost);
            float length = fsm.kick.Animation.inPlaceAnimation != null ? fsm.kick.Animation.inPlaceAnimation.length : 1f;
            ride.KickReadyAt = now + length + _config.KickCooldown;
            ride.Gait = "Kick";
            return true;
        }

        private void TickKick(Ride ride)
        {
            float now = Time.realtimeSinceStartup;
            if (!ride.KickHit && now >= ride.KickHitAt)
            {
                ride.KickHit = true;
                foreach (var target in TargetsAhead(ride, _config.KickRange, 60f))
                    Hit(ride, target, _config.KickPlayerDamage, _config.KickStructureDamage, 1f, 16f);
            }
            if (ride.Kick.isPlaying && now < ride.KickHitAt + 6f) return;
            ride.Anim.StopServerAndReturnToPool(ref ride.Kick, interrupt: ride.Kick.isPlaying);
            ride.Kick = null;
            ride.Heading = ride.Cow.transform.eulerAngles.y;
            ride.Gait = "Stop";
        }

        // R: one of the cow's own moo clips. Standing, it plays in full; moving, the cow keeps walking under it.
        private void TryMoo(Ride ride)
        {
            if (ride.Anim == null || ride.Charge != null || ride.Kick != null || (ride.Moo != null && ride.Moo.isPlaying)) return;
            var fsm = ride.Fsm as LivestockAnimalFSM;
            if (fsm == null || fsm.randomIdle == null || fsm.randomIdle.animations == null) return;
            var moos = new List<AnimationClip>();
            foreach (var clip in fsm.randomIdle.animations) if (clip != null && clip.name.ToLower().Contains("moo")) moos.Add(clip);
            if (moos.Count == 0) return;
            ClearIdle(ride);
            if (ride.Moo != null) ride.Anim.StopServerAndReturnToPool(ref ride.Moo);
            ride.Moo = ride.Anim.PlayServerAndTakeFromPool(moos[UnityEngine.Random.Range(0, moos.Count)], 1f, pauseAgent: !ride.Moving);
        }

        // Running into things at speed hurts them (players, animals, wooden structures)
        private void TickRam(Ride ride, float now)
        {
            if (!ride.Moving) return;
            float speed = CurrentSpeed(ride);
            float min = ride.Agent.GetSpeedForGait(ParseGait(_config.RamMinGait, RustNavMeshAgent.Speeds.Run));
            if (speed < min * 0.9f) return;
            float sprint = ride.Agent.GetSpeedForGait(RustNavMeshAgent.Speeds.Sprint);
            float scale = Mathf.Clamp(speed / Mathf.Max(0.1f, sprint), 0.5f, 1.2f);
            foreach (var target in TargetsAhead(ride, 1.6f, 50f))
            {
                ulong id = target.net?.ID.Value ?? 0;
                if (ride.RamCooldown.TryGetValue(id, out float until) && now < until) continue;
                ride.RamCooldown[id] = now + 1f;
                Hit(ride, target, _config.RamPlayerDamage, _config.RamStructureDamage, scale, 6f);
            }
            if (ride.RamCooldown.Count > 64) ride.RamCooldown.Clear();
        }

        private void Hit(Ride ride, BaseCombatEntity target, float playerDamage, float structureDamage, float scale, float push)
        {
            var cow = ride.Cow;
            float mult = (cow.IsMale ? _config.BullMultiplier : 1f) * scale;
            Vector3 away = (target.transform.position - cow.transform.position).WithY(0f).normalized;
            if (target is BasePlayer player)
            {
                player.Hurt(playerDamage * mult, Rust.DamageType.Blunt, cow, true);
                if (!player.IsNpc && !player.IsDead()) player.DoPush(away * push + Vector3.up * (push * 0.35f));
            }
            else if (target.IsNpc) target.Hurt(playerDamage * mult, Rust.DamageType.Blunt, cow, true);
            else target.Hurt(structureDamage * mult, Rust.DamageType.Blunt, cow, true);
            ride.Hits++;
        }

        // What a cow may run into: players and animals, twig and wood building blocks, wooden deployables
        private bool RamTarget(Ride ride, BaseCombatEntity entity)
        {
            if (entity == null || entity == ride.Cow || entity.IsDestroyed || entity.IsDead()) return false;
            if (entity is BaseMountable) return false;
            if (entity is BasePlayer player)
                return player != ride.Front?.GetMounted() && player != ride.Rear?.GetMounted();
            if (entity.IsNpc) return true;
            if (entity is BuildingBlock block)
                return block.grade == BuildingGrade.Enum.Twigs || block.grade == BuildingGrade.Enum.Wood;
            string name = entity.ShortPrefabName ?? "";
            foreach (string fragment in _config.WoodenNames)
                if (!string.IsNullOrEmpty(fragment) && name.Contains(fragment)) return true;
            return false;
        }

        private readonly List<BaseCombatEntity> _visBuffer = new List<BaseCombatEntity>();

        private List<BaseCombatEntity> TargetsAhead(Ride ride, float range, float angle)
        {
            var found = new List<BaseCombatEntity>();
            var t = ride.Cow.transform;
            Vector3 origin = t.position;
            _visBuffer.Clear();
            Vis.Entities(origin + Vector3.up * 0.6f, range + 0.8f, _visBuffer, _hitMask, QueryTriggerInteraction.Ignore);
            foreach (var entity in _visBuffer)
            {
                if (found.Contains(entity) || !RamTarget(ride, entity)) continue;
                Vector3 to = (entity.ClosestPoint(origin) - origin).WithY(0f);
                if (to.magnitude > range) continue;
                if (to.sqrMagnitude > 0.04f && Vector3.Angle(t.forward, to) > angle) continue;
                found.Add(entity);
            }
            return found;
        }

        private BaseCombatEntity NearestTarget(Ride ride, float range, float angle)
        {
            BaseCombatEntity best = null;
            float bestDist = float.MaxValue;
            Vector3 origin = ride.Cow.transform.position;
            foreach (var entity in TargetsAhead(ride, range, angle))
            {
                float d = (entity.ClosestPoint(origin) - origin).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = entity; }
            }
            return best;
        }

        // ------------------------------------------------------------------ seats on the cow's back

        private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World", "Construction");

        private static float GroundHeight(Vector3 at, float fallback)
        {
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 3.5f, GroundMask, QueryTriggerInteraction.Ignore)) return hit.point.y;
            float h = TerrainMeta.HeightMap.GetHeight(at);
            return Mathf.Abs(h - fallback) < 3f ? h : fallback;
        }

        // The server keeps the cow's root upright at the navmesh; the client tilts the body to the ground.
        // Tilt and lift the seats the same way so the rider stays on the back.
        private void FollowGround(Ride ride)
        {
            var t = ride.Cow.transform;
            Vector3 p = t.position, f = t.forward;
            float front = GroundHeight(p + f * 0.9f, p.y), rear = GroundHeight(p - f * 0.9f, p.y);
            float pitch = Mathf.Clamp(-Mathf.Atan2(front - rear, 1.8f) * Mathf.Rad2Deg * _config.SeatPitchScale, -25f, 25f);
            float lift = Mathf.Clamp((front + rear) * 0.5f - p.y, -0.35f, 0.35f);
            ride.SeatPitch = Mathf.Lerp(ride.SeatPitch, pitch, 0.35f);
            ride.SeatLift = Mathf.Lerp(ride.SeatLift, lift, 0.35f);
            if (ride.Carrier == null) PlaceSeat(ride, ride.Front, _config.FrontSeat.Vector);
            PlaceSeat(ride, ride.Rear, ride.RearSpot);
        }

        private void PlaceSeat(Ride ride, BaseMountable seat, Vector3 basePos)
        {
            if (seat == null || seat.IsDestroyed) return;
            Vector3 pivot = new Vector3(0f, _config.SeatPivotHeight, 0f);
            Quaternion rot = Quaternion.Euler(ride.SeatPitch, 0f, 0f);
            Vector3 pos = pivot + rot * (basePos - pivot) + Vector3.up * ride.SeatLift;
            var tr = seat.transform;
            if ((tr.localPosition - pos).sqrMagnitude < 0.0001f && Quaternion.Angle(tr.localRotation, rot) < 0.2f) return;
            tr.localPosition = pos;
            tr.localRotation = rot;   // the engine's position tick sends it (syncPosition)
        }

        // ------------------------------------------------------------------ the cow's fate

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity is Cow cow) CowGone(cow);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (_unloading || Rust.Application.isQuitting) return;
            if (entity is Cow cow) CowGone(cow);
            else if (entity is RidableHorse horse && horse.net != null && _carrierRides.TryGetValue(horse.net.ID.Value, out Ride carried) && !carried.Ending)
            {
                _carrierRides.Remove(horse.net.ID.Value);
                Carriers.Remove(horse);
                carried.Carrier = null;
                carried.Front = null;
                NextTick(() => { if (!carried.Ending) EndRide(carried); });
            }
            else if (entity is BaseMountable seat && seat.net != null && _seatRides.TryGetValue(seat.net.ID.Value, out Ride ride) && !ride.Ending)
            {
                // A seat killed from outside (admin, another plugin): end the ride cleanly
                _seatRides.Remove(seat.net.ID.Value);
                if (ride.Front == seat) ride.Front = null;
                if (ride.Rear == seat) ride.Rear = null;
                NextTick(() => { if (!ride.Ending) EndRide(ride); });
            }
        }

        private void CowGone(Cow cow)
        {
            if (cow.net != null && _rides.TryGetValue(cow.net.ID.Value, out Ride ride)) EndRide(ride);
        }

        // ------------------------------------------------------------------ hints and bars

        private void UpdateHints()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsSleeping()) continue;
                string text = null;
                var seat = player.GetMounted();
                var ride = RideOfSeat(seat);
                if (ride != null)
                {
                    ShowBars(player, ride);
                    // Keep the controls up for a few seconds after getting on, then show the gait
                    if (Time.realtimeSinceStartup - ride.MountedAt < 6f && seat == ride.Front) continue;
                    if (seat == ride.Front)
                    {
                        text = ride.Gait == "Stop" ? null : ride.Gait;
                        if (ride.Tired) text = text == null ? "Tired" : text + " · tired";
                    }
                }
                else
                {
                    if (_barsShown.ContainsKey(player.userID)) ShowBars(player, null);
                    if (seat == null && !_presses.ContainsKey(player.userID))
                    {
                        var cow = CowAtLook(player, _config.MountDistance, out bool atHead);
                        if (cow != null && cow.IsAdult() && CanUse(player))
                        {
                            string key = KeyName(_mountButton);
                            if (atHead && IsLeadingIt(player, cow)) text = $"[{key}] Stop leading";
                            else if (atHead && CanLeadIt(player, cow)) text = $"[{key}] Lead";
                            else if (!_rides.TryGetValue(cow.net.ID.Value, out Ride busy)) text = $"[{key}] Ride";
                            else if (busy.Front == null || busy.Front.GetMounted() == null) text = $"[{key}] Ride";
                            else if (_config.RidersPerCow >= 2 && (busy.Rear == null || busy.Rear.GetMounted() == null)) text = $"[{key}] Ride behind";
                            if (text != null && CanChoose(player, cow)) text += $" · hold {key} for more";
                        }
                    }
                }
                ShowHint(player, text);
            }
        }

        private static string KeyName(BUTTON button)
        {
            switch (button)
            {
                case BUTTON.USE: return "E";
                case BUTTON.RELOAD: return "R";
                case BUTTON.FIRE_THIRD: return "Middle mouse";
                default: return button.ToString();
            }
        }

        private void ShowHint(BasePlayer player, string text)
        {
            if (!_config.ShowHints && text != null) return;
            _hintShown.TryGetValue(player.userID, out string shown);
            if (shown == text) return;
            CuiHelper.DestroyUi(player, HintPanel);
            if (text == null) { _hintShown.Remove(player.userID); return; }
            _hintShown[player.userID] = text;
            var ui = new CuiElementContainer();
            ui.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.9" },
                RectTransform = { AnchorMin = "0.3 0.40", AnchorMax = "0.7 0.44" }
            }, "Hud", HintPanel);
            CuiHelper.AddUi(player, ui);
        }

        // Health and stamina while riding, sent only when a value changes: either two entries in the game's
        // own vitals list (where Comfort, Bleeding and the like appear), or a small CUI panel
        private void ShowBars(BasePlayer player, Ride ride)
        {
            string style = (_config.BarsStyle ?? "Vitals").ToLower();
            if (ride == null || style == "off" || ride.Cow == null || ride.Cow.IsDestroyed || ride.Carrier != null)
            {
                if (!_barsShown.Remove(player.userID)) return;
                CuiHelper.DestroyUi(player, BarsPanel);
                SendVitals(player, null);
                return;
            }
            int hp = Mathf.RoundToInt(ride.Cow.healthFraction * 100f);
            int st = Mathf.RoundToInt(ride.Stamina * 100f);
            int key = hp * 1000 + st + (ride.Tired ? 1000000 : 0);
            if (_barsShown.TryGetValue(player.userID, out int shown) && shown == key) return;
            _barsShown[player.userID] = key;
            if (style != "cui") { SendVitals(player, ride, hp, st); return; }
            CuiHelper.DestroyUi(player, BarsPanel);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = _config.BarsAnchorMin, AnchorMax = _config.BarsAnchorMax }
            }, "Hud", BarsPanel);
            AddBar(ui, "0 0.55", "1 1", hp / 100f, "0.62 0.17 0.13 0.95", ride.Cow.IsMale ? "Bull" : "Cow");
            AddBar(ui, "0 0", "1 0.45", st / 100f, ride.Tired ? "0.55 0.38 0.12 0.95" : "0.78 0.62 0.16 0.95", "Stamina");
            CuiHelper.AddUi(player, ui);
        }

        // The game's custom vitals: an empty list clears ours
        private void SendVitals(BasePlayer player, Ride ride, int hp = 0, int st = 0)
        {
            var community = CommunityEntity.ServerInstance;
            if (community == null || !player.IsConnected) return;
            using (var vitals = Facepunch.Pool.Get<CustomVitals>())
            {
                vitals.vitals = Facepunch.Pool.Get<List<CustomVitalInfo>>();
                if (ride != null)
                {
                    vitals.vitals.Add(Vital(ride.Cow.IsMale ? "Bull" : "Cow", $"{hp}%", _config.VitalIcons[0],
                        new Color(0.45f, 0.12f, 0.1f, 0.85f), hp <= 25 ? new Color(1f, 0.5f, 0.4f) : Color.white));
                    vitals.vitals.Add(Vital(ride.Tired ? "Tired" : "Stamina", $"{st}%", _config.VitalIcons[1],
                        ride.Tired ? new Color(0.4f, 0.28f, 0.08f, 0.85f) : new Color(0.5f, 0.4f, 0.1f, 0.85f), ride.Tired ? new Color(1f, 0.75f, 0.4f) : Color.white));
                }
                community.SendCustomVitals(player, vitals);
            }
        }

        private static CustomVitalInfo Vital(string left, string right, string icon, Color background, Color text)
        {
            var vital = Facepunch.Pool.Get<CustomVitalInfo>();
            vital.leftText = left;
            vital.rightText = right;
            vital.icon = icon ?? "";
            vital.backgroundColor = background;
            vital.leftTextColor = text;
            vital.rightTextColor = text;
            vital.iconColor = Color.white;
            vital.active = true;
            vital.timeLeft = 0;
            return vital;
        }

        private static void AddBar(CuiElementContainer ui, string min, string max, float fraction, string color, string label)
        {
            string track = ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, BarsPanel);
            ui.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = $"{Mathf.Clamp01(fraction):0.###} 1" }
            }, track);
            ui.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.85" },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "1 1" }
            }, track);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _warmed.Remove(player.userID);
            _hintShown.Remove(player.userID);
            _barsShown.Remove(player.userID);
            _mountCooldown.Remove(player.userID);
            _presses.Remove(player.userID);
        }

        private bool _selfTestRunning;   // bots have no chat: log what they would have been told

        private void Reply(BasePlayer player, string message)
        {
            if (_config.LogWheel) Puts($"[reply to {player.displayName}] {message}");
            if (player.IsConnected) player.ChatMessage($"<color=#d9b38c>Cow</color>: {message}");
            else if (_selfTestRunning) Puts($"[selftest]   reply to {player.displayName}: {message}");
        }

        // ------------------------------------------------------------------ commands

        [ChatCommand("cow")]
        private void CmdCow(BasePlayer player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLower() : "help";
            switch (sub)
            {
                case "ride":
                {
                    var cow = CowAtLook(player, _config.MountDistance);
                    if (cow == null) { Reply(player, "Look at a cow within reach."); return; }
                    TryRide(player, cow);
                    return;
                }
                case "lead":
                {
                    var cow = CowAtLook(player, _config.MountDistance);
                    if (cow == null) { Reply(player, "Look at a cow within reach."); return; }
                    bool leading = cow.IsLeading() && cow.LeadingPlayer.Get(true) == player;
                    if (cow.TryLead(player, !leading)) Reply(player, leading ? $"You let go of {CowName(cow)}." : $"You are leading {CowName(cow)}.");
                    else Reply(player, $"{CowName(cow)} won't be led by you.");
                    return;
                }
                case "info":
                {
                    var cow = CowAtLook(player, 10f);
                    if (cow == null) { Reply(player, "Look at a cow."); return; }
                    _rides.TryGetValue(cow.net.ID.Value, out Ride ride);
                    int riding = ride == null ? 0 : (ride.Front?.GetMounted() != null ? 1 : 0) + (ride.Rear?.GetMounted() != null ? 1 : 0);
                    Reply(player, $"{CowName(cow)}: {(cow.IsMale ? "bull" : "cow")}, {(cow.IsAdult() ? "adult" : "young")}, " +
                                  $"health {cow.health:0}/{cow.MaxHealth():0}, {(cow.TrustsToHandle(player) ? "trusts you" : "doesn't trust you")}, " +
                                  $"{riding} of {_config.RidersPerCow} rider{(_config.RidersPerCow == 1 ? "" : "s")} on" +
                                  (ride != null ? $", stamina {ride.Stamina * 100f:0}%{(ride.Tired ? " (tired)" : "")}, charges {ride.Charges}, hits {ride.Hits}." : "."));
                    return;
                }
                case "spawn":
                {
                    if (!IsAdmin(player)) { Reply(player, "Admins only."); return; }
                    if (!Physics.Raycast(player.eyes.HeadRay(), out RaycastHit hit, 30f, LayerMask.GetMask("Terrain", "World", "Construction", "Default")))
                    { Reply(player, "Look at the ground within 30 m."); return; }
                    bool bull = args.Length > 1 && args[1].ToLower() == "bull";
                    var cow = SpawnCow(hit.point, Quaternion.LookRotation((player.transform.position - hit.point).WithY(0f)), bull);
                    Reply(player, cow == null ? "Couldn't spawn a cow there." : $"Spawned {CowName(cow)}.");
                    return;
                }
                case "seat":
                {
                    if (!IsAdmin(player)) { Reply(player, "Admins only."); return; }
                    string which = args.Length > 1 ? args[1].ToLower() : "";
                    SeatSpot spot = which == "front" ? _config.FrontSeat : which == "rear" ? _config.RearSeat : null;
                    if (spot == null || args.Length < 5 || !float.TryParse(args[2], out float x) || !float.TryParse(args[3], out float y) || !float.TryParse(args[4], out float z))
                    {
                        Reply(player, $"/cow seat front|rear <x> <y> <z>  (now front {_config.FrontSeat}, rear {_config.RearSeat}). Takes effect on the next mount.");
                        return;
                    }
                    spot.X = x; spot.Y = y; spot.Z = z;
                    SaveConfig();
                    Reply(player, $"{which} seat now at {spot}. Get off and on again to see it.");
                    return;
                }
                case "tilt":
                {
                    if (!IsAdmin(player)) { Reply(player, "Admins only."); return; }
                    if (args.Length < 2 || !float.TryParse(args[1], out float scale))
                    {
                        Reply(player, $"/cow tilt <scale>  (now {_config.SeatPitchScale:0.00}; 0 = seats stay level, 1 = tilt with the ground). Follow ground: {_config.SeatFollowsGround}.");
                        return;
                    }
                    _config.SeatPitchScale = scale;
                    SaveConfig();
                    Reply(player, $"Seat tilt scale now {scale:0.00}.");
                    return;
                }
                default:
                    Reply(player, $"Look at an adult cow and press {KeyName(_mountButton)} to ride it bareback" +
                                  (_config.RidersPerCow >= 2 ? "; a second player gets on behind." : ".") +
                                  $" Hold {KeyName(_mountButton)} on a cow that trusts you to choose Lead or Ride." +
                                  "\n/cow ride · /cow lead · /cow info" +
                                  (IsAdmin(player) ? "\nAdmin: /cow spawn [bull] · /cow seat front|rear <x> <y> <z> · /cow tilt <scale> · /ridablecow config method 1|2" : ""));
                    return;
            }
        }

        [ChatCommand("ridablecow")]
        private void CmdRidableCow(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { Reply(player, "Admins only."); return; }
            if (args.Length >= 3 && args[0].ToLower() == "config" && args[1].ToLower() == "method" && int.TryParse(args[2], out int method) && (method == 1 || method == 2))
            {
                Reply(player, SetMethod(method));
                return;
            }
            if (args.Length >= 3 && args[0].ToLower() == "config" && args[1].ToLower() == "lift" && float.TryParse(args[2], out float lift))
            {
                _config.CarrierLift = lift;
                SaveConfig();
                Reply(player, $"Carrier horse offset now {lift:0.0} m ({(lift < 0 ? "under the ground" : "above the cow")}); applies to the next mount.");
                return;
            }
            if (args.Length >= 3 && args[0].ToLower() == "config" && args[1].ToLower() == "decel" && float.TryParse(args[2], out float decel) && decel > 0f)
            {
                _config.StopDeceleration = decel;
                SaveConfig();
                Reply(player, $"Stop deceleration now {decel:0.0} m/s² (a jog eases out over {3.16f * 3.16f / (2f * decel):0.0} m).");
                return;
            }
            if (args.Length >= 1 && args[0].ToLower() == "spawn")
            {
                if (!Physics.Raycast(player.eyes.HeadRay(), out RaycastHit hit, 30f, LayerMask.GetMask("Terrain", "World", "Construction", "Default")))
                { Reply(player, "Look at the ground within 30 m."); return; }
                bool bull = args.Length > 1 && args[1].ToLower() == "bull";
                var spawned = SpawnCow(hit.point, Quaternion.LookRotation((player.transform.position - hit.point).WithY(0f)), bull);
                Reply(player, spawned == null ? "Couldn't spawn a cow there." : $"Spawned {CowName(spawned)}.");
                return;
            }
            if (args.Length >= 2 && args[0].ToLower() == "reload" && args[1].ToLower() == "icons")
            {
                LoadIcons();
                _warmed.Clear();
                foreach (var pl in BasePlayer.activePlayerList) WarmIcons(pl);
                Reply(player, $"{_iconIds.Count} custom icon(s) loaded and sent to everyone's cache: {string.Join(", ", _iconIds.Keys)}. Give it a few seconds, then /ridablecow pie.");
                return;
            }
            Reply(player, $"Ride method is {_config.RideMethod} ({MethodName(_config.RideMethod)}).\n/ridablecow config method 1|2  (1 = seats on the cow with the plugin's bars, 2 = hidden carrier horse with the game's horse bars; applies to the next mount)\n/ridablecow config lift <m> · /ridablecow config decel <m/s²> · /ridablecow spawn [bull] · /ridablecow reload icons");
        }

        private static string MethodName(int method) => method == 2 ? "carrier horse, game's horse bars" : "saddle seats, plugin bars";

        private string SetMethod(int method)
        {
            _config.RideMethod = method;
            SaveConfig();
            return $"Ride method now {method} ({MethodName(method)}); applies to the next mount.";
        }

        // ridablecows.leads: every cow being led, by whom, and what each online player could do with the cow they look at
        [ConsoleCommand("ridablecows.leads")]
        private void CmdLeads(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var lines = new List<string>();
            foreach (var e in BaseNetworkable.serverEntities)
                if (e is Cow c && c.IsLeading())
                    lines.Add($"cow {c.net.ID} {CowName(c)} led by {c.LeadingPlayer.Get(true)?.displayName ?? "?"} flags {c.flags}");
            if (lines.Count == 0) lines.Add("no cow is being led");
            foreach (var pl in BasePlayer.activePlayerList)
            {
                var cow = CowAtLook(pl, 10f);
                if (cow == null) { lines.Add($"{pl.displayName}: not looking at a cow"); continue; }
                lines.Add($"{pl.displayName} looks at {cow.net.ID} {CowName(cow)}: trust {cow.TrustOf(pl):0}s trusts={cow.TrustsToHandle(pl)} canLead={cow.CanLead(pl)} leading={cow.IsLeading()} leadable={cow.IsLeadable} sore={cow.IsSoreAt(pl)} milk={cow.CanBeMilked(pl)} flags {cow.flags}");
            }
            arg.ReplyWith(string.Join("\n", lines));
        }

        [ConsoleCommand("ridablecows.lift")]
        private void CmdLift(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            if (float.TryParse(arg.GetString(0, ""), out float lift)) { _config.CarrierLift = lift; SaveConfig(); }
            arg.ReplyWith($"carrier offset {_config.CarrierLift:0.0} m (applies to the next mount)");
        }

        // ridablecows.endall: every rider off, every carrier and seat removed
        [ConsoleCommand("ridablecows.endall")]
        private void CmdEndAll(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            int n = _rides.Count;
            foreach (var ride in new List<Ride>(_rides.Values)) EndRide(ride);
            arg.ReplyWith($"ended {n} ride(s)");
        }

        [ConsoleCommand("ridablecows.method")]
        private void CmdMethod(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            if (int.TryParse(arg.GetString(0, ""), out int method) && (method == 1 || method == 2)) arg.ReplyWith(SetMethod(method));
            else arg.ReplyWith($"ridablecows.method 1|2 (now {_config.RideMethod}: {MethodName(_config.RideMethod)})");
        }

        // Bindable: bind <key> cow.ride
        [ConsoleCommand("cow.ride")]
        private void CmdConsoleRide(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            var cow = CowAtLook(player, _config.MountDistance);
            if (cow != null) TryRide(player, cow);
        }

        private Cow SpawnCow(Vector3 position, Quaternion rotation, bool bull = false)
        {
            var cow = GameManager.server.CreateEntity(bull ? BullPrefab : CowPrefab, position, rotation) as Cow;
            if (cow == null) return null;
            cow.Spawn();
            return cow;
        }

        // ------------------------------------------------------------------ server console: status, probe and self-test

        [ConsoleCommand("ridablecows.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var lines = new List<string> { $"{_rides.Count} ride(s), method {_config.RideMethod} ({MethodName(_config.RideMethod)})" };
            foreach (var ride in _rides.Values)
            {
                var cow = ride.Cow;
                lines.Add($"  cow {cow.net.ID} {CowName(cow)} at {cow.transform.position} yaw {cow.transform.eulerAngles.y:0} heading {ride.Heading:0} " +
                          $"gait {ride.Gait} speed {CurrentSpeed(ride):0.00} stamina {ride.Stamina:0.00}{(ride.Tired ? " tired" : "")} charges {ride.Charges} hits {ride.Hits} " +
                          $"carrier {(ride.Carrier == null ? "-" : $"{ride.Carrier.net.ID} {(ride.Carrier.HasFlag(BaseEntity.Flags.Disabled) ? "hidden" : "visible")} hp {ride.Carrier.health:0} stam {ride.Carrier.currentStamina:0.0}")} stopping {ride.Stopping} idle {(ride.Idle != null && ride.Idle.isPlaying)} " +
                          $"seat pitch {ride.SeatPitch:0.0} lift {ride.SeatLift:0.00} driver {ride.Front?.GetMounted()?.displayName ?? "-"} " +
                          $"passenger {ride.Rear?.GetMounted()?.displayName ?? "-"} fsm {(FsmRunningField != null && (bool)FsmRunningField.GetValue(ride.Fsm) ? "on" : "off")}");
            }
            arg.ReplyWith(string.Join("\n", lines));
        }

        // Console twin of /cow seat and /cow tilt: ridablecows.seat front|rear <x> <y> <z>, ridablecows.seat tilt <scale>
        [ConsoleCommand("ridablecows.seat")]
        private void CmdSeat(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            string which = arg.GetString(0, "").ToLower();
            if (which == "tilt" && float.TryParse(arg.GetString(1, ""), out float scale))
            {
                _config.SeatPitchScale = scale;
                SaveConfig();
                arg.ReplyWith($"Seat tilt scale now {scale:0.00}.");
                return;
            }
            SeatSpot spot = which == "front" ? _config.FrontSeat : which == "rear" ? _config.RearSeat : null;
            if (spot == null || !float.TryParse(arg.GetString(1, ""), out float x) || !float.TryParse(arg.GetString(2, ""), out float y) || !float.TryParse(arg.GetString(3, ""), out float z))
            {
                arg.ReplyWith($"ridablecows.seat front|rear <x> <y> <z> | tilt <scale>  (now front {_config.FrontSeat}, rear {_config.RearSeat}, tilt {_config.SeatPitchScale:0.00})");
                return;
            }
            spot.X = x; spot.Y = y; spot.Z = z;
            SaveConfig();
            arg.ReplyWith($"{which} seat now at {spot}; applies on the next mount.");
        }

        // What the game gives us to work with: the seat prefab's HUD flag, and the cow's authored animations
        [ConsoleCommand("ridablecows.probe")]
        private void CmdProbe(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var lines = new List<string>();
            foreach (string prefab in new[] { _config.FrontSeatPrefab, _config.RearSeatPrefab })
            {
                var seat = GameManager.server.FindPrefab(prefab)?.GetComponent<BaseMountable>();
                lines.Add(seat == null ? $"{prefab}: not a mountable"
                    : $"{prefab}: shouldShowHudHealth={seat.shouldShowHudHealth} isMobile={seat.isMobile} health={seat.startHealth} syncPosition={seat.syncPosition}");
            }
            if (!string.IsNullOrEmpty(_config.ChargeAnimation))
            {
                RootMotionData rm = null;
                try { rm = FileSystem.Load<RootMotionData>(_config.ChargeAnimation); } catch (Exception ex) { lines.Add($"load error: {ex.Message}"); }
                lines.Add($"charge animation '{_config.ChargeAnimation}': {(rm == null ? "not found" : $"{rm.name} clip {rm.inPlaceAnimation?.name} {rm.inPlaceAnimation?.length:0.00}s")}");
            }
            var player = arg.Player();
            Cow sample = player != null ? CowAtLook(player, 20f) : null;
            if (sample == null)
                foreach (var entity in BaseNetworkable.serverEntities)
                    if (entity is Cow c) { sample = c; break; }
            if (sample == null) { lines.Add("no cow on the map to inspect"); arg.ReplyWith(string.Join("\n", lines)); return; }
            lines.Add($"cow {sample.net.ID} {CowName(sample)} {(sample.IsMale ? "bull" : "cow")} prefab {sample.PrefabName} fsm {sample.GetComponent<FSMComponent>()?.GetType().Name} health {sample.health:0}/{sample.MaxHealth():0}");
            var fsm = sample.GetComponent<LivestockAnimalFSM>();
            if (fsm != null)
                foreach (var field in typeof(LivestockAnimalFSM).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object value = field.GetValue(fsm);
                    if (value is State_PlayAnimationRM rm)
                    {
                        string extra = value is State_AttackWithTracking track ? $" damage {track.damage} delay {track.damageDelay} radius {track.radius}"
                                     : value is State_Attack attack ? $" damage {attack.Damage} delay {attack.Delay}" : "";
                        lines.Add($"  {field.Name}: {(rm.Animation == null ? "-" : $"{rm.Animation.name} clip {rm.Animation.inPlaceAnimation?.name} {rm.Animation.inPlaceAnimation?.length:0.00}s")}{extra}");
                    }
                    else if (value is LivestockAnimalFSM.State_Stomp stomp)
                        lines.Add($"  {field.Name}: rm {stomp.RootMotionAnimation?.name ?? "-"} clip {stomp.Animation?.name ?? "-"}");
                }
            arg.ReplyWith(string.Join("\n", lines));
        }

        // Spawns a cow and two bot riders somewhere walkable, then mounts, steers and dismounts them,
        // checking each step. Results go to the server log, prefixed "[selftest]".
        //   ridablecows.selftest            near the first wild cow
        //   ridablecows.selftest x y z      near that point
        //   ridablecows.selftest hold       stop with two bots riding (to test a reload), then:
        //   ridablecows.selftest cleanup    remove the test cow and bots
        [ConsoleCommand("ridablecows.selftest")]
        private void CmdSelfTest(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            string mode = arg.GetString(0, "").ToLower();
            if (mode == "cleanup") { arg.ReplyWith(SelfTest.Cleanup(this)); return; }
            bool hold = mode == "hold";
            Vector3 at;
            if (arg.HasArgs(3) && float.TryParse(arg.GetString(0), out float x) && float.TryParse(arg.GetString(1), out float y) && float.TryParse(arg.GetString(2), out float z))
                at = new Vector3(x, y, z);
            else
            {
                Cow wild = null;
                foreach (var entity in BaseNetworkable.serverEntities)
                    if (entity is Cow c && !_rides.ContainsKey(c.net.ID.Value)) { wild = c; break; }
                if (wild == null) { arg.ReplyWith("No cow on the map to test near; pass x y z."); return; }
                at = wild.transform.position + wild.transform.right * 4f;
            }
            arg.ReplyWith($"Self-test started at {at}; watch the log for [selftest].");
            new SelfTest(this, at, hold).Run();
        }

        private class SelfTest
        {
            private readonly RidableCows _p;
            private const string TestCowName = "SelfTest";
            private readonly Vector3 _at;
            private readonly bool _hold;
            private Cow _cow;
            private BasePlayer _bot1, _bot2;
            private int _pass, _fail;
            private readonly List<BaseEntity> _spawned = new List<BaseEntity>();

            public SelfTest(RidableCows plugin, Vector3 at, bool hold) { _p = plugin; _at = at; _hold = hold; }

            public static string Cleanup(RidableCows p)
            {
                int killed = 0;
                foreach (var entity in new List<BaseNetworkable>(BaseNetworkable.serverEntities))
                    if (entity is BasePlayer player && !player.IsConnected && player.displayName != null && player.displayName.StartsWith("CowTester"))
                    { player.EnsureDismounted(); player.Kill(); killed++; }
                foreach (var entity in new List<BaseNetworkable>(BaseNetworkable.serverEntities))
                    if (entity is Cow cow && cow.AnimalName == TestCowName)
                    {
                        if (p._rides.TryGetValue(cow.net.ID.Value, out Ride ride)) p.EndRide(ride);
                        cow.Kill();
                        killed++;
                    }
                return $"Removed {killed} test entities.";
            }

            // Open ground in a direction from the cow, by navmesh raycast (so a test drive doesn't start into a rock)
            private float Clearance(float yaw, float distance = 20f)
            {
                var agent = _cow.GetComponent<RustNavMeshAgent>();
                Vector3 origin = _cow.transform.position;
                Vector3 target = origin + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * distance;
                return agent.Raycast(target, out UnityEngine.AI.NavMeshHit hit) ? Vector3.Distance(origin.WithY(0f), hit.position.WithY(0f)) : distance;
            }

            private void FaceOpenGround()
            {
                float best = 0f, bestClear = -1f;
                for (float yaw = 0f; yaw < 360f; yaw += 22.5f)
                {
                    float clear = Clearance(yaw);
                    if (clear > bestClear) { bestClear = clear; best = yaw; }
                }
                _cow.transform.rotation = Quaternion.Euler(0f, best, 0f);
                _cow.SendNetworkUpdate();
                _p.Puts($"[selftest]   facing {best:0} ({bestClear:0.0} m clear)");
            }

            private void Check(string what, bool ok, string detail = "")
            {
                if (ok) _pass++; else _fail++;
                _p.Puts($"[selftest] {(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? " - " + detail : "")}");
            }

            private Ride RideOf() => _cow != null && _cow.net != null && _p._rides.TryGetValue(_cow.net.ID.Value, out Ride r) ? r : null;

            private BasePlayer SpawnBot(string name, Vector3 pos)
            {
                var bot = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", pos) as BasePlayer;
                if (bot == null) return null;
                bot.enableSaving = false;
                bot.Spawn();
                bot.displayName = name;   // Spawn names bots after their ID
                _spawned.Add(bot);
                return bot;
            }

            private bool Unhurt(BasePlayer bot, float before) => bot != null && !bot.isMounted && !bot.IsDead() && bot.health >= before;

            private void Drive(Keys keys, float seconds, Action then)
            {
                var ride = RideOf();
                if (ride != null) { ride.Input = keys; ride.InputPinned = true; }
                _p.timer.Repeat(0.5f, Mathf.FloorToInt(seconds / 0.5f), () =>
                {
                    var r = RideOf();
                    if (r == null || _cow == null || _cow.IsDestroyed) return;
                    _p.Puts($"[selftest]   {keys}: pos {_cow.transform.position} yaw {_cow.transform.eulerAngles.y:0} heading {r.Heading:0} gait {r.Gait} speed {CurrentSpeed(r):0.00} stamina {r.Stamina:0.00} seat pitch {r.SeatPitch:0.0} lift {r.SeatLift:0.00} path {r.Agent.hasPath} paused {r.Agent.IsPaused}");
                });
                _p.timer.Once(seconds, () =>
                {
                    var r = RideOf();
                    if (r != null) { r.Input = Keys.None; }
                    then();
                });
            }

            public void Run()
            {
                _p._selfTestRunning = true;
                _p.Puts($"[selftest] start at {_at}");
                _cow = _p.SpawnCow(_at, Quaternion.identity);
                if (_cow == null) { Check("spawn cow", false); Finish(); return; }
                _spawned.Add(_cow);
                _cow.AnimalName = TestCowName;
                Check("spawn cow", true, $"{_cow.net.ID} {(_cow.IsMale ? "bull" : "cow")} adult={_cow.IsAdult()}");
                // Let the agent bind the navmesh and the FSM leave WaitForNavMesh
                _p.timer.Once(3f, Driver);
            }

            private int _mountTries;

            private void Driver()
            {
                if (_cow == null || _cow.IsDestroyed) { Check("cow alive", false); Finish(); return; }
                if (_bot1 == null)
                {
                    FaceOpenGround();
                    _bot1 = SpawnBot("CowTester1", _cow.transform.position + _cow.transform.right * 1.5f);
                    _bot2 = SpawnBot("CowTester2", _cow.transform.position - _cow.transform.right * 1.5f);
                    if (_bot1 == null || _bot2 == null) { Check("spawn riders", false); Finish(); return; }
                    // The rider leads the cow first (vanilla E on a trusting cow): getting on drops the lead
                    _cow.SetFamiliarity(_bot1.userID, 1000000f);
                    bool led = _cow.TryLead(_bot1, true);
                    Check("rider can lead a trusting cow", led && _cow.IsLeading());
                    Check("a trusting cow offers the Lead / Ride choice", CanChoose(_bot1, _cow));
                    Check("pie menu is available (CommunityEntity)", CommunityEntity.ServerInstance != null);
                }
                bool mounted = _p.TryRide(_bot1, _cow);   // the real path, not forced
                if (!mounted && ++_mountTries < 4 && (_cow.IsOffItsFeet() || _cow.IsHeadDown()))
                {
                    // A cow grazing or lying down at spawn gets up first; a player would simply press E again
                    _p.Puts($"[selftest]   cow is getting up, trying again (attempt {_mountTries + 1})");
                    _p.timer.Once(2.5f, Driver);
                    return;
                }
                var ride = RideOf();
                Check("driver mounts (unforced)", mounted && ride != null && _bot1.GetMounted() == ride.Front, mounted ? "" : "TryRide returned false");
                Check("rider's own lead dropped on mount", !_cow.IsLeading());
                if (ride == null) { Finish(); return; }
                _p.Puts($"[selftest]   ride method {_p._config.RideMethod} ({MethodName(_p._config.RideMethod)})");
                if (ride.Carrier != null)
                {
                    var horse = ride.Carrier;
                    Check("carrier horse spawned, kinematic and frozen", !horse.IsDestroyed && Carriers.Contains(horse) && (horse.rigidBody == null || horse.rigidBody.isKinematic));
                    Check("carrier carried clear of the cow, not parented", !horse.HasParent() && Mathf.Abs(horse.transform.position.y - _cow.transform.position.y - _p._config.CarrierLift - _p._config.CarrierSpot.Y) < 0.3f, $"horse y {horse.transform.position.y:0.00} cow y {_cow.transform.position.y:0.00} offset {_p._config.CarrierLift}");
                    Check("driver sits on the carrier's saddle, on the cow's back", ride.Front.GetParentEntity() == horse && _bot1.GetMounted() == ride.Front && Mathf.Abs(ride.Front.transform.position.y - _cow.transform.position.y) < 1.5f, $"seat y {ride.Front.transform.position.y:0.00} cow y {_cow.transform.position.y:0.00}");
                    Check("the game's horse bars read the cow's health", Mathf.Abs(horse.health - _cow.health) < 1f && Mathf.Abs(horse.MaxHealth() - _cow.MaxHealth()) < 1f, $"horse {horse.health:0}/{horse.MaxHealth():0} cow {_cow.health:0}/{_cow.MaxHealth():0}");
                    _p.timer.Once(_p._config.CarrierHideDelay + 0.5f, () => Check("carrier hidden after the mount", horse.IsDestroyed || horse.HasFlag(BaseEntity.Flags.Disabled)));
                }
                else
                {
                    Check("driver seat fixed to the cow itself", ride.Front.GetParentEntity() == _cow && ride.Front.parentBone == 0, $"bone {ride.Front.parentBone}, local {ride.Front.transform.localPosition}");
                    Check("driver seat sends its position", !_p._config.SeatFollowsGround || ride.Front.syncPosition, $"syncPosition {ride.Front.syncPosition}");
                }
                Check("no passenger seat until a passenger gets on", ride.Rear == null);
                Check("fsm parked", FsmRunningField == null || !(bool)FsmRunningField.GetValue(ride.Fsm));
                Check("agent free to move", !ride.Agent.IsPaused);
                int riders = _p._config.RidersPerCow;
                _p._config.RidersPerCow = 1;
                Check("passenger refused when the cow carries one", !_p.TryRide(_bot2, _cow, force: true));
                _p._config.RidersPerCow = riders;

                // The waiting passenger must not stand in front of the cow: Shift near a target charges it
                Vector3 behind = _cow.transform.position - _cow.transform.forward * 4f;
                behind.y = TerrainMeta.HeightMap.GetHeight(behind) + 0.05f;
                _bot2.MovePosition(behind);
                Vector3 start = _cow.transform.position;
                string fast = ParseGait(_p._config.FastGait, RustNavMeshAgent.Speeds.Sprint).ToString();
                Drive(Keys.Forward | Keys.Faster, 4f, () =>
                {
                    var r = RideOf();
                    float moved = Vector3.Distance(start.WithY(0), _cow.transform.position.WithY(0));
                    Check($"W+Shift moves the cow at {fast}", moved > 5f && r?.Gait == fast, $"moved {moved:0.0} m in 4 s, gait {r?.Gait}");
                    Check("sprinting drains stamina", r != null && r.Stamina < 0.95f, $"stamina {r?.Stamina:0.00}");
                    if (r?.Carrier != null)
                    {
                        Check("carrier follows the cow", Vector3.Distance(r.Carrier.transform.position, _p.CarrierPoint(r)) < 0.5f, $"off by {Vector3.Distance(r.Carrier.transform.position, _p.CarrierPoint(r)):0.00} m");
                        Check("the game's stamina bar reads the ride's stamina", Mathf.Abs(r.Carrier.currentStamina / r.Carrier.maxStamina - r.Stamina) < 0.05f, $"horse {r.Carrier.currentStamina / r.Carrier.maxStamina:0.00} ride {r.Stamina:0.00}");
                    }
                    Check("rider follows the cow", _bot1.GetMounted() != null && Vector3.Distance(_bot1.transform.position, _cow.transform.position) < 2.5f,
                          $"rider {Vector3.Distance(_bot1.transform.position, _cow.transform.position):0.00} m from cow");
                    float yaw0 = _cow.transform.eulerAngles.y;
                    Keys side = Clearance(yaw0 + 60f) >= Clearance(yaw0 - 60f) ? Keys.Right : Keys.Left;
                    Drive(Keys.Forward | side, 2.5f, () =>
                    {
                        float turned = Mathf.Abs(Mathf.DeltaAngle(yaw0, _cow.transform.eulerAngles.y));
                        Check("A/D steers the cow", turned > 45f, $"turned {turned:0} deg");
                        Vector3 released = _cow.transform.position;
                        Drive(Keys.None, 2.5f, () =>
                        {
                            float eased = Vector3.Distance(released.WithY(0f), _cow.transform.position.WithY(0f));
                            Check("letting go eases the cow to a halt (not a dead stop)", eased > 0.4f && eased < 6f, $"rolled on {eased:0.00} m after release");
                            Vector3 stopped = _cow.transform.position;
                            _p.timer.Once(1f, () =>
                            {
                                float drift = Vector3.Distance(stopped, _cow.transform.position);
                                Check("then stands still", drift < 0.3f, $"drifted {drift:0.00} m");
                                var rr = RideOf();
                                Check("stopped cow settles with an idle clip", !_p._config.IdleClipOnStop || (rr != null && rr.Idle != null), $"idle playing {rr?.Idle != null && rr.Idle.isPlaying}");
                                Vector3 before = _cow.transform.position; Vector3 fwd = _cow.transform.forward;
                                Drive(Keys.Back, 2f, () =>
                                {
                                    float backed = Vector3.Dot(before - _cow.transform.position, fwd);
                                    Check("S backs the cow up", backed > 0.6f, $"moved {backed:0.00} m backwards in 2 s");
                                    Charge();
                                });
                            });
                        });
                    });
                });
            }

            // A target bot stands in front of the cow; Shift should charge it
            private void Charge()
            {
                var ride = RideOf();
                if (ride == null) { Finish(); return; }
                Vector3 ahead = _cow.transform.position + _cow.transform.forward * 2.5f;
                ahead.y = TerrainMeta.HeightMap.GetHeight(ahead) + 0.05f;
                var dummy = SpawnBot("CowTester3", ahead);
                if (dummy == null) { Check("spawn charge target", false); Passenger(); return; }
                float health = dummy.health;
                bool allowed = _p.ChargeAllowed(_cow) && _p.ChargeAnimation(ride) != null;
                _p.Puts($"[selftest]   charge allowed {allowed}, animation {_p.ChargeAnimation(ride)?.name ?? "-"}, target {Vector3.Distance(dummy.transform.position, _cow.transform.position):0.0} m ahead");
                int before = ride.Charges;
                Drive(Keys.Faster, 0.6f, () =>
                {
                    var r = RideOf();
                    Check("Shift near a target starts a charge", !allowed || (r != null && r.Charges > before), $"charges {r?.Charges}, gait {r?.Gait}");
                    _p.timer.Once(2.5f, () =>
                    {
                        var r2 = RideOf();
                        Check("the charge hurts what it hits", !allowed || dummy.IsDead() || dummy.health < health, $"target health {health:0} -> {dummy.health:0}, hits {r2?.Hits}");
                        Check("charge ends and the cow is free again", r2 != null && r2.Charge == null && !r2.Agent.IsPaused, $"gait {r2?.Gait} paused {r2?.Agent.IsPaused}");
                        if (!dummy.IsDestroyed) dummy.Kill();
                        Passenger();
                    });
                });
            }

            private void Passenger()
            {
                // The second player walks up and gets on behind
                Vector3 beside = _cow.transform.position - _cow.transform.right * 1.5f;
                beside.y = TerrainMeta.HeightMap.GetHeight(beside) + 0.05f;
                _bot2.MovePosition(beside);
                bool on = _p.TryRide(_bot2, _cow);
                var ride = RideOf();
                Check("passenger gets on behind", on && ride != null && _bot2.GetMounted() == ride.Rear);
                if (ride == null || ride.Rear == null) { Finish(); return; }
                // in the cow's frame: the carrier's saddle seat hangs under a horse bone whose axes are its own
                Check("passenger seat behind the driver", _cow.transform.InverseTransformPoint(ride.Rear.transform.position).z < _cow.transform.InverseTransformPoint(ride.Front.transform.position).z - 0.2f,
                      $"front z {_cow.transform.InverseTransformPoint(ride.Front.transform.position).z:0.00} rear z {_cow.transform.InverseTransformPoint(ride.Rear.transform.position).z:0.00}");
                var third = SpawnBot("CowTester4", beside);
                Check("third rider refused", !_p.TryRide(third, _cow, force: true));
                if (_hold)
                {
                    _p.Puts($"[selftest] holding: cow {_cow.net.ID} with two riders. Reload the plugin, then 'ridablecows.selftest cleanup'. {_pass} passed, {_fail} failed so far");
                    return;
                }
                FaceOpenGround();
                ride.Heading = _cow.transform.eulerAngles.y;
                Vector3 start = _cow.transform.position;
                Drive(Keys.Forward, 3f, () =>
                {
                    float moved = Vector3.Distance(start.WithY(0), _cow.transform.position.WithY(0));
                    Check("cow carries two riders", moved > 4f && _bot1.GetMounted() != null && _bot2.GetMounted() != null, $"moved {moved:0.0} m in 3 s");
                    float health1 = _bot1.health;
                    _p.OnPlayerWantsDismount(_bot1, RideOf().Front);
                    _p.timer.Once(0.5f, () =>
                    {
                        var r = RideOf();
                        Check("driver gets off unhurt", Unhurt(_bot1, health1), $"health {health1:0} -> {_bot1.health:0}");
                        Check("passenger stays when the driver leaves", r != null && _bot2.GetMounted() == r.Rear);
                        float health2 = _bot2.health;
                        var rear = r?.Rear;
                        if (rear != null) _p.OnPlayerWantsDismount(_bot2, rear);
                        _p.timer.Once(0.5f, () =>
                        {
                            Check("passenger gets off unhurt", Unhurt(_bot2, health2), $"health {health2:0} -> {_bot2.health:0}");
                            Check("ride ended, seats removed", RideOf() == null && (rear == null || rear.IsDestroyed));
                            Check("no carrier horse left behind", !AnyCarrierNear(_cow.transform.position));
                            Check("fsm back on", FsmRunningField == null || (bool)FsmRunningField.GetValue(_cow.GetComponent<FSMComponent>()));
                            Death();
                        });
                    });
                });
            }

            private void Death()
            {
                // Both back on, then the cow dies under them
                _p._mountCooldown.Remove(_bot1.userID);
                _p._mountCooldown.Remove(_bot2.userID);
                bool on = _p.TryRide(_bot1, _cow, force: true) && _p.TryRide(_bot2, _cow, force: true);
                Check("both remount", on);
                float health1 = _bot1.health, health2 = _bot2.health;
                _cow.Hurt(10000f, Rust.DamageType.Generic, null, false);
                _p.timer.Once(0.5f, () =>
                {
                    Check("cow death puts both riders down unhurt", Unhurt(_bot1, health1) && Unhurt(_bot2, health2),
                          $"health {health1:0} -> {_bot1.health:0}, {health2:0} -> {_bot2.health:0}");
                    Check("ride ended with the cow", RideOf() == null);
                    Finish();
                });
            }

            private static bool AnyCarrierNear(Vector3 at)
            {
                foreach (var e in BaseNetworkable.serverEntities)
                    if (e is RidableHorse h && Carriers.Contains(h) && !h.IsDestroyed && Vector3.Distance(h.transform.position, at) < 30f) return true;
                return false;
            }

            private void Finish()
            {
                var ride = RideOf();
                if (ride != null) _p.EndRide(ride);
                foreach (var e in _spawned) if (e != null && !e.IsDestroyed) e.Kill();
                foreach (var corpse in UnityEngine.Object.FindObjectsOfType<BaseCorpse>())
                    if (_cow != null && corpse != null && !corpse.IsDestroyed && Vector3.Distance(corpse.transform.position, _cow.transform.position) < 5f) corpse.Kill();
                _p._selfTestRunning = false;
                _p.Puts($"[selftest] done: {_pass} passed, {_fail} failed");
            }
        }
    }
}
