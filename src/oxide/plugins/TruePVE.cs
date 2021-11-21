using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TruePVE", "nivex", "2.0.5")]
    [Description("Improvement of the default Rust PVE behavior")]
    // Thanks to the original author, ignignokt84.
    class TruePVE : RustPlugin
    {
        #region Variables
        private static TruePVE Instance;

        // config/data container
        private Configuration data = new Configuration();

        [PluginReference]
        Plugin ZoneManager, LiteZones, Clans, Friends;

        // usage information string with formatting
        public string usageString;
        // valid commands
        private enum Command { def, sched, trace, usage, enable, sleepers };

        // default values array

        // flags for RuleSets
        [Flags]
        private enum RuleFlags
        {
            None = 0,
            SuicideBlocked = 1,
            AuthorizedDamage = 1 << 1,
            NoHeliDamage = 1 << 2,
            HeliDamageLocked = 1 << 3,
            NoHeliDamagePlayer = 1 << 4,
            HumanNPCDamage = 1 << 5,
            LockedBoxesImmortal = 1 << 6,
            LockedDoorsImmortal = 1 << 7,
            AdminsHurtSleepers = 1 << 8,
            ProtectedSleepers = 1 << 9,
            CupboardOwnership = 1 << 10,
            SelfDamage = 1 << 11,
            TwigDamage = 1 << 12,
            NoHeliDamageQuarry = 1 << 13,
            TrapsIgnorePlayers = 1 << 14,
            TurretsIgnorePlayers = 1 << 15,
            TurretsIgnoreScientist = 1 << 16,
            TrapsIgnoreScientist = 1 << 17,
            SamSitesIgnorePlayers = 1 << 18,
            TwigDamageRequiresOwnership = 1 << 19,
            AuthorizedDamageRequiresOwnership = 1 << 20,
            VehiclesTakeCollisionDamageWithoutDriver = 1 << 21,
            FriendlyFire = 1 << 22,
            AnimalsIgnoreSleepers = 1 << 23
        }
        //
        // timer to check for schedule updates
        private Timer scheduleUpdateTimer;
        // current ruleset
        private RuleSet currentRuleSet;
        // current broadcast message
        private string currentBroadcastMessage;
        // internal useZones flag
        private bool useZones = false;
        // constant "any" string for rules
        private const string Any = "any";
        // constant "allzones" string for mappings
        private const string AllZones = "allzones";
        private bool serverInitialized = false;
        // permission for mapping command
        private const string PermCanMap = "truepve.canmap";
        private bool animalsIgnoreSleepers;
        // trace flag
        private bool trace = false;
        // tracefile name
        private const string traceFile = "ruletrace";
        // auto-disable trace after 300s (5m)
        private const float traceTimeout = 300f;
        // trace timeout timer
        private Timer traceTimer;
        private bool tpveEnabled = true;
        private HashSet<string> _deployables = new HashSet<string>();
        #endregion

        #region Loading/Unloading
        // load things
        private void Loaded()
        {
            Instance = this;
            LoadDefaultMessages();
            string baseCommand = "tpve";
            // register console commands automagically
            foreach (Command command in Enum.GetValues(typeof(Command)))
            {
                cmd.AddConsoleCommand(baseCommand + "." + command.ToString(), this, nameof(CommandDelegator));
                //Puts(baseCommand + "." + command.ToString());
            }
            // register chat commands
            cmd.AddChatCommand(baseCommand + "_prod", this, nameof(HandleProd));
            cmd.AddChatCommand(baseCommand + "_enable", this, nameof(EnableToggle));
            cmd.AddChatCommand(baseCommand, this, nameof(ChatCommandDelegator));

            // build usage string for console (without sizing)
            usageString = WrapColor("orange", GetMessage("Header_Usage")) + "\n" +
                          WrapColor("cyan", $"{baseCommand}.{Command.def}") + $" - {GetMessage("Cmd_Usage_def")}{Environment.NewLine}" +
                          WrapColor("cyan", $"{baseCommand}.{Command.trace}") + $" - {GetMessage("Cmd_Usage_trace")}{Environment.NewLine}" +
                          WrapColor("cyan", $"{baseCommand}.{Command.sched} [enable|disable]") + $" - {GetMessage("Cmd_Usage_sched")}{Environment.NewLine}" +
                          WrapColor("cyan", $"/{baseCommand}_prod") + $" - {GetMessage("Cmd_Usage_prod")}{Environment.NewLine}" +
                          WrapColor("cyan", $"/{baseCommand} map") + $" - {GetMessage("Cmd_Usage_map")}";
            permission.RegisterPermission(PermCanMap, this);
        }

        // on unloaded
        private void Unload()
        {
            if (scheduleUpdateTimer != null)
                scheduleUpdateTimer.Destroy();
            Instance = null;
        }

        // plugin loaded
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "ZoneManager")
                ZoneManager = plugin;
            if (plugin.Name == "LiteZones")
                LiteZones = plugin;
            if (!serverInitialized) return;
            if (ZoneManager != null || LiteZones != null)
                useZones = data.config.useZones;
        }

        // plugin unloaded
        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == "ZoneManager")
                ZoneManager = null;
            if (plugin.Name == "LiteZones")
                LiteZones = null;
            if (!serverInitialized) return;
            if (ZoneManager == null && LiteZones == null)
                useZones = false;
            traceTimer?.Destroy();
        }

        private void Init()
        {
            Unsubscribe(nameof(CanBeTargeted));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnPlayerConnected));
            Unsubscribe(nameof(OnSamSiteTarget));
            Unsubscribe(nameof(OnTrapTrigger));
            Unsubscribe(nameof(OnNpcTarget));
        }

        // server initialized
        private void OnServerInitialized()
        {
            if (!isLoaded)
            {
                Puts("Plugin has been disabled.");
                return;
            }
            // check for server pve setting
            if (ConVar.Server.pve) WarnPve();
            // load configuration
            data.Init();
            currentRuleSet = data.GetDefaultRuleSet();
            if (currentRuleSet == null)
                PrintWarning(GetMessage("Warning_NoRuleSet"), data.defaultRuleSet);
            useZones = data.config.useZones && (LiteZones != null || ZoneManager != null);
            if (useZones && data.mappings.Count == 1 && data.mappings.First().Key.Equals(data.defaultRuleSet))
                useZones = false;
            if (data.schedule.enabled)
                TimerLoop(true);
            if (data.ruleSets.Any(ruleSet => ruleSet.HasFlag(RuleFlags.AnimalsIgnoreSleepers))) Subscribe(nameof(OnNpcTarget));
            if (currentRuleSet == null) return;
            InitializeDeployables();
            Subscribe(nameof(CanBeTargeted));
            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnPlayerConnected));
            Subscribe(nameof(OnSamSiteTarget));
            Subscribe(nameof(OnTrapTrigger));
            serverInitialized = true;
        }
        #endregion

        #region Command Handling
        // delegation method for console commands
        private void CommandDelegator(ConsoleSystem.Arg arg)
        {
            // return if user doesn't have access to run console command
            if (!HasAccess(arg)) return;

            string cmd = arg.cmd.Name;
            if (!Enum.IsDefined(typeof(Command), cmd))
            {
                // shouldn't hit this
                SendMessage(arg, "Error_InvalidParameter");
            }
            else
            {
                switch ((Command)Enum.Parse(typeof(Command), cmd))
                {
                    case Command.sleepers:
                        HandleSleepers(arg);
                        return;
                    case Command.def:
                        HandleDef(arg);
                        return;
                    case Command.sched:
                        HandleScheduleSet(arg);
                        return;
                    case Command.trace:                        
                        trace = !trace;
                        if (!trace) // || !arg.HasArgs())
                        {
                            tracePlayer = null;
                            traceEntity = null;
                        }
                        else tracePlayer = arg.Player(); 
                        SendMessage(arg, "Notify_TraceToggle", new object[] { trace ? "on" : "off" });
                        if (trace)
                        {
                            traceTimer = timer.In(traceTimeout, () => trace = false);
                        }
                        else traceTimer?.Destroy();
                        return;
                    case Command.enable:
                        tpveEnabled = !tpveEnabled;
                        SendMessage(arg, "Enable", new object[] { tpveEnabled.ToString() });
                        return;
                    case Command.usage:
                        ShowUsage(arg);
                        return;
                }
                SendMessage(arg, "Error_InvalidParamForCmd", new object[] { cmd });
            }
            ShowUsage(arg);
        }

        private void HandleSleepers(ConsoleSystem.Arg arg)
        {
            if (animalsIgnoreSleepers)
            {
                animalsIgnoreSleepers = false;
                if (!data.ruleSets.Any(ruleSet => ruleSet.HasFlag(RuleFlags.AnimalsIgnoreSleepers))) Unsubscribe(nameof(OnNpcTarget));
                arg.ReplyWith("Sleepers are no longer protected from animals.");
            }
            else
            {
                animalsIgnoreSleepers = true;
                Subscribe(nameof(OnNpcTarget));
                arg.ReplyWith("Sleepers are now protected from animals.");
            }
        }

        private void EnableToggle(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
            {
                SendMessage(player, "Error_NoPermission");
                return;
            }

            tpveEnabled = !tpveEnabled;
            SendMessage(player, "Enable", new object[] { tpveEnabled.ToString() });
        }

        // handle setting defaults
        private void HandleDef(ConsoleSystem.Arg arg)
        {
            LoadDefaultConfiguration();
            SendMessage(arg, "Notify_DefConfigLoad");
            LoadDefaultData();
            SendMessage(arg, "Notify_DefDataLoad");

            SaveData();
        }

        // handle prod command (raycast to determine what player is looking at)
        private void HandleProd(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
            {
                SendMessage(player, "Error_NoPermission");
                return;
            }

            object entity;
            if (!GetRaycastTarget(player, out entity) || entity == null)
            {
                SendReply(player, WrapSize(12, WrapColor("red", GetMessage("Error_NoEntityFound", player.UserIDString))));
                return;
            }
            SendMessage(player, "Notify_ProdResult", new object[] { entity.GetType(), (entity as BaseEntity).ShortPrefabName });
        }

        // delegation method for chat commands
        private void ChatCommandDelegator(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermCanMap))
            {
                SendMessage(player, "Error_NoPermission");
                return;
            }

            // assume args[0] is the command (beyond /tpve)
            if (args.Length > 0)
                command = args[0];

            // shift arguments
            args = args.Length > 1 ? args = args.Skip(1).ToArray() : new string[0];

            string message = "";
            object[] opts = new object[] { };

            if (command != "map")
            {
                message = "Error_InvalidCommand";
            }
            else if (args.Length == 0)
            {
                message = "Error_InvalidParamForCmd";
                opts = new object[] { command };
            }
            else
            {
                // args[0] should be mapping name
                // args[1] if exists should be target ruleset or "exclude"
                // if args[1] is empty, delete mapping
                string from = args[0];
                string to = args.Length == 2 ? args[1] : null;

                if (to != null && !data.ruleSets.Select(r => r.name).Contains(to) && to != "exclude")
                {
                    // target ruleset must exist, or be "exclude"
                    message = "Error_InvalidMapping";
                    opts = new object[] { from, to };
                }
                else
                {
                    bool dirty = false;
                    if (to != null)
                    {
                        dirty = true;
                        if (data.HasMapping(from))
                        {
                            // update existing mapping
                            string old = data.mappings[from];
                            data.mappings[from] = to;
                            message = "Notify_MappingUpdated";
                            opts = new object[] { from, old, to };
                        }
                        else
                        {
                            // add new mapping
                            data.mappings.Add(from, to);
                            message = "Notify_MappingCreated";
                            opts = new object[] { from, to };
                        }
                    }
                    else
                    {
                        if (data.HasMapping(from))
                        {
                            dirty = true;
                            // remove mapping
                            string old = data.mappings[from];
                            data.mappings.Remove(from);
                            message = "Notify_MappingDeleted";
                            opts = new object[] { from, old };
                        }
                        else
                        {
                            message = "Error_NoMappingToDelete";
                            opts = new object[] { from };
                        }
                    }

                    if (dirty)
                    {
                        SaveData(); // save changes to config file
                    }
                }
            }

            SendMessage(player, message, opts);
        }

        // handles schedule enable/disable
        private void HandleScheduleSet(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs())
            {
                SendMessage(arg, "Error_InvalidParamForCmd");
                return;
            }
            string message = string.Empty;
            if (!data.schedule.valid)
            {
                message = "Notify_InvalidSchedule";
            }
            else if (arg.Args[0] == "enable")
            {
                if (data.schedule.enabled) return;
                data.schedule.enabled = true;
                TimerLoop();
                message = "Notify_SchedSetEnabled";
            }
            else if (arg.Args[0] == "disable")
            {
                if (!data.schedule.enabled) return;
                data.schedule.enabled = false;
                if (scheduleUpdateTimer != null)
                    scheduleUpdateTimer.Destroy();
                message = "Notify_SchedSetDisabled";
            }
            object[] opts = new object[] { };
            if (message == string.Empty)
            {
                message = "Error_InvalidParameter";
                opts = new object[] { arg.Args[0] };
            }
            SendMessage(arg, message, opts);
        }
        #endregion

        #region Configuration/Data

        private bool isLoaded;

        // load config
        protected override void LoadConfig()
        {
            try
            {
                base.LoadConfig();

                CheckVersion();

                data = Config.ReadObject<Configuration>();
            }
            catch (Exception ex)
            {
                Puts(ex.Message);
                return;
            }

            if (data == null || data.schedule == null)
            {
                LoadDefaultConfig();
            }

            CheckData();

            // check config version, update version to current version
            if (data.configVersion == null || !data.configVersion.Equals(Version.ToString()))
            {
                data.configVersion = Version.ToString();
            }

            SaveData();

            isLoaded = true;
        }

        // save data
        private void SaveData() => Config.WriteObject(data);

        // check rulesets and groups
        private bool CheckData()
        {
            bool dirty = false;
            if ((data.ruleSets == null || data.ruleSets.Count == 0) || (data.groups == null || data.groups.Count == 0))
            {
                dirty = LoadDefaultData();
            }
            if (data.schedule == null)
            {
                data.schedule = new Schedule();
                dirty = true;
            }
            dirty |= CheckMappings();
            return dirty;
        }

        // rebuild mappings
        private bool CheckMappings()
        {
            bool dirty = false;
            foreach (RuleSet rs in data.ruleSets)
            {
                if (!data.mappings.ContainsValue(rs.name))
                {
                    data.mappings[rs.name] = rs.name;
                    dirty = true;
                }
            }
            return dirty;
        }

        // default config creation
        protected override void LoadDefaultConfig()
        {
            data = new Configuration
            {
                configVersion = Version.ToString()
            };
            LoadDefaultConfiguration();
            LoadDefaultData();
            SaveData();
            Puts("Created new configuration file.");
        }

        private void CheckVersion()
        {
            if (Config["configVersion"] == null) return;
            Version config = new Version(Config["configVersion"].ToString());
            if (config < new Version("0.7.0"))
            {
                string fname = Config.Filename.Replace(".json", ".old.json");
                Config.Save(fname);
                PrintWarning(string.Format(GetMessage("Warning_OldConfig"), fname));
                Config.Clear();
            }
        }

        // populates default configuration entries
        private bool LoadDefaultConfiguration()
        {
            data.config = new ConfigurationOptions();
            return true;
        }

        // load default data to mappings, rulesets, and groups
        private bool LoadDefaultData()
        {
            data.mappings.Clear();
            data.ruleSets.Clear();
            data.groups.Clear();
            data.schedule = new Schedule();
            data.defaultRuleSet = "default";

            // build groups first
            data.groups.Add(new EntityGroup("barricades")
            {
                members = "Barricade, icewall, GraveYardFence", // "barricade.cover.wood, door_barricade_a, door_barricade_a_large, door_barricade_b, door_barricade_dbl_a, door_barricade_dbl_a_large, door_barricade_dbl_b, door_barricade_dbl_b_large",
                exclusions = "barricade.concrete, barricade.sandbags, barricade.metal, barricade.stone, barricade.wood, barricade.woodwire"
            });

            data.groups.Add(new EntityGroup("dispensers")
            {
                members = "BaseCorpse, HelicopterDebris, PlayerCorpse, NPCPlayerCorpse, HorseCorpse"
            });

            data.groups.Add(new EntityGroup("fire")
            {
                members = "FireBall, FlameExplosive, FlameThrower, BaseOven, FlameTurret, rocket_heli_napalm, napalm, oilfireball2"
            });

            data.groups.Add(new EntityGroup("guards")
            {
                members = "bandit_guard, scientistpeacekeeper, sentry.scientist.static"
            });

            data.groups.Add(new EntityGroup("heli")
            {
                members = "BaseHelicopter"
            });

            data.groups.Add(new EntityGroup("highwalls")
            {
                members = "SimpleBuildingBlock, wall.external.high.ice, gates.external.high.stone, gates.external.high.wood"
            });

            data.groups.Add(new EntityGroup("ridablehorses")
            {
                members = "RidableHorse"
            });

            data.groups.Add(new EntityGroup("cars")
            {
                members = "BasicCar, ModularCar, BaseModularVehicle, BaseVehicleModule, VehicleModuleEngine, VehicleModuleSeating, VehicleModuleStorage, VehicleModuleTaxi, ModularCarSeat"
            });

            data.groups.Add(new EntityGroup("mini")
            {
                members = "MiniCopter"
            });

            data.groups.Add(new EntityGroup("scrapheli")
            {
                members = "ScrapTransportHelicopter"
            });

            data.groups.Add(new EntityGroup("ch47")
            {
                members = "ch47.entity"
            });

            data.groups.Add(new EntityGroup("npcs")
            {
                members = "ch47scientists.entity, BradleyAPC, HTNAnimal, HTNPlayer, HumanNPC, HumanNPCNew, NPCMurderer, NPCPlayer, Scientist, ScientistNPC, ScientistNPCNew, TunnelDweller, SimpleShark, UnderwaterDweller, Zombie"
            });

            data.groups.Add(new EntityGroup("players")
            {
                members = "BasePlayer"
            });

            data.groups.Add(new EntityGroup("resources")
            {
                members = "ResourceEntity, TreeEntity, OreResourceEntity, LootContainer",
                exclusions = "hobobarrel.deployed"
            });

            data.groups.Add(new EntityGroup("samsites")
            {
                members = "sam_site_turret_deployed",
                exclusions = "sam_static"
            });

            data.groups.Add(new EntityGroup("traps")
            {
                members = "AutoTurret, BearTrap, FlameTurret, Landmine, GunTrap, ReactiveTarget, TeslaCoil, spikes.floor"
            });

            data.groups.Add(new EntityGroup("junkyard")
            {
                members = "magnetcrane.entity, carshredder.entity"
            });

            // create default ruleset
            RuleSet defaultRuleSet = new RuleSet(data.defaultRuleSet)
            {
                _flags = RuleFlags.HumanNPCDamage | RuleFlags.LockedBoxesImmortal | RuleFlags.LockedDoorsImmortal | RuleFlags.SamSitesIgnorePlayers | RuleFlags.TrapsIgnorePlayers | RuleFlags.TurretsIgnorePlayers,
                flags = "HumanNPCDamage, LockedBoxesImmortal, LockedDoorsImmortal, SamSitesIgnorePlayers, TrapsIgnorePlayers, TurretsIgnorePlayers"
            };

            // create rules and add to ruleset
            defaultRuleSet.AddRule("anything can hurt dispensers");
            defaultRuleSet.AddRule("anything can hurt resources");
            defaultRuleSet.AddRule("anything can hurt barricades");
            defaultRuleSet.AddRule("anything can hurt traps");
            defaultRuleSet.AddRule("anything can hurt heli");
            defaultRuleSet.AddRule("anything can hurt npcs");
            defaultRuleSet.AddRule("anything can hurt players");
            defaultRuleSet.AddRule("nothing can hurt ch47");
            defaultRuleSet.AddRule("nothing can hurt cars");
            defaultRuleSet.AddRule("nothing can hurt mini");
            //defaultRuleSet.AddRule("nothing can hurt guards");
            defaultRuleSet.AddRule("nothing can hurt ridablehorses");
            defaultRuleSet.AddRule("cars cannot hurt anything");
            defaultRuleSet.AddRule("mini cannot hurt anything");
            defaultRuleSet.AddRule("ch47 cannot hurt anything");
            defaultRuleSet.AddRule("scrapheli cannot hurt anything");
            defaultRuleSet.AddRule("players cannot hurt players");
            defaultRuleSet.AddRule("players cannot hurt traps");
            defaultRuleSet.AddRule("guards cannot hurt players");
            defaultRuleSet.AddRule("fire cannot hurt players");
            defaultRuleSet.AddRule("traps cannot hurt players");
            defaultRuleSet.AddRule("highwalls cannot hurt players");
            defaultRuleSet.AddRule("barricades cannot hurt players");
            defaultRuleSet.AddRule("mini cannot hurt mini");
            defaultRuleSet.AddRule("npcs can hurt players");
            defaultRuleSet.AddRule("junkyard cannot hurt anything");
            defaultRuleSet.AddRule("junkyard can hurt cars");

            data.ruleSets.Add(defaultRuleSet); // add ruleset to rulesets list

            data.mappings[data.defaultRuleSet] = data.defaultRuleSet; // create mapping for ruleset

            return true;
        }

        private bool ResetRules(string key)
        {
            if (!serverInitialized || string.IsNullOrEmpty(key))
            {
                return false;
            }

            string old = data.defaultRuleSet;

            data.defaultRuleSet = key;
            currentRuleSet = data.GetDefaultRuleSet();

            if (currentRuleSet == null)
            {
                data.defaultRuleSet = old;
                currentRuleSet = data.GetDefaultRuleSet();
                return false;
            }

            return true;
        }
        #endregion

        #region Trace
        private BaseEntity traceEntity;
        private BasePlayer tracePlayer;

        private void Trace(string message, int indentation = 0)
        {
            if (data.config.PlayerConsole || data.config.ServerConsole)
            {
                if (traceEntity != null && !traceEntity.IsDestroyed && tracePlayer != null && tracePlayer.IsConnected)
                {
                    if (data.config.MaxTraceDistance == 0 || tracePlayer.Distance(traceEntity) <= data.config.MaxTraceDistance)
                    {
                        if (data.config.PlayerConsole)
                        {
                            tracePlayer.ConsoleMessage(message);
                        }

                        if (data.config.ServerConsole)
                        {
                            Puts(message);
                        }

                        //_tsb.AppendLine("".PadLeft(indentation, ' ') + message);
                        _tsb.AppendLine(message);
                    }
                }
            }
            else _tsb.AppendLine("".PadLeft(indentation, ' ') + message);
        }

        private void LogTrace()
        {
            var text = _tsb.ToString();
            traceEntity = null;
            try
            {
                LogToFile(traceFile, text, this);
            }
            catch (IOException)
            {
                timer.Once(1f, () => LogToFile(traceFile, text, this));
                return;
            }
            _tsb.Length = 0;
        }
        private StringBuilder _tsb = new StringBuilder();
        #endregion Trace

        #region Hooks/Handler Procedures
        private void OnPlayerConnected(BasePlayer player)
        {
            if (data.schedule.enabled && data.schedule.broadcast && currentBroadcastMessage != null)
            {
                SendReply(player, GetMessage("Prefix") + currentBroadcastMessage);
            }
        }

        private string CurrentRuleSetName() => currentRuleSet.name;
        private bool IsEnabled() => tpveEnabled;

        // handle damage - if another mod must override TruePVE damages or take priority,
        // set handleDamage to false and reference HandleDamage from the other mod(s)
        private object OnEntityTakeDamage(ResourceEntity entity, HitInfo hitInfo)
        {
            // if default global is not enabled, return true (allow all damage)
            if (hitInfo == null || currentRuleSet == null || currentRuleSet.IsEmpty() || !currentRuleSet.enabled)
            {
                return null;
            }

            // get entity and initiator locations (zones)
            List<string> entityLocations = GetLocationKeys(entity);
            List<string> initiatorLocations = GetLocationKeys(hitInfo.Initiator);
            // check for exclusion zones (zones with no rules mapped)
            if (CheckExclusion(entityLocations, initiatorLocations, trace))
            {
                if (trace) Trace("Exclusion found; allow and return", 1);
                return null;
            }

            if (trace) Trace("No exclusion found - looking up RuleSet...", 1);
            // process location rules
            RuleSet ruleSet = GetRuleSet(entityLocations, initiatorLocations);

            return EvaluateRules(entity, hitInfo, ruleSet) ? (object)null : true;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null || AllowKillingSleepers(entity, hitInfo))
            {
                return null;
            }

            object extCanTakeDamage = Interface.CallHook("CanEntityTakeDamage", new object[] { entity, hitInfo });

            if (extCanTakeDamage is bool)
            {
                if ((bool)extCanTakeDamage)
                {
                    return null;
                }

                CancelHit(hitInfo);
                return true;
            }

            if (!tpveEnabled || !data.config.handleDamage)
            {
                return null;
            }

            var majority = hitInfo.damageTypes.GetMajorityDamageType();

            if (majority == DamageType.Decay || majority == DamageType.Fall)
            {
                return null;
            }

            if (!AllowDamage(entity, hitInfo))
            {
                if (trace) LogTrace();
                CancelHit(hitInfo);
                return true;
            }

            if (trace) LogTrace();
            return null;
        }

        private void CancelHit(HitInfo hitInfo)
        {
            hitInfo.damageTypes = new DamageTypeList();
            //hitInfo.HitMaterial = 0;
            //hitInfo.PointStart = Vector3.zero;
            //hitInfo.HitEntity = null;
            hitInfo.DidHit = false;
            hitInfo.DoHitEffects = false;
        }

        private bool AllowKillingSleepers(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (animalsIgnoreSleepers && hitInfo.Initiator is BaseNpc) return false;

            return data.AllowKillingSleepers && entity is BasePlayer && entity.ToPlayer().IsSleeping();
        }

        // determines if an entity is "allowed" to take damage
        private bool AllowDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (trace)
            {
                traceEntity = entity;
                _tsb.Length = 0;
            }

            // if default global is not enabled or entity is npc, allow all damage
            if (currentRuleSet == null || currentRuleSet.IsEmpty() || !currentRuleSet.enabled || entity is BaseNpc || entity is BaseHelicopter)
            {
                return true;
            }

            // allow damage to door barricades and covers
            if (entity is Barricade && (entity.ShortPrefabName.Contains("door_barricade") || entity.ShortPrefabName.Contains("cover")))
            {
                return true;
            }

            // if entity is a barrel, trash can, or giftbox, allow damage (exclude water and hobo barrels)
            if (!entity.ShortPrefabName.Equals("waterbarrel") && entity.prefabID != 1748062128)
            {
                if (entity.ShortPrefabName.Contains("barrel") || entity.ShortPrefabName.Equals("loot_trash") || entity.ShortPrefabName.Equals("giftbox_loot"))
                {
                    return true;
                }
            }

            var weapon = hitInfo.Initiator ?? hitInfo.Weapon ?? hitInfo.WeaponPrefab;

            //hitInfo.ProjectilePrefab.sourceWeaponPrefab
            if (trace)
            {
                // Sometimes the initiator is not the attacker (turrets)
                Trace("======================" + Environment.NewLine +
                  "==  STARTING TRACE  ==" + Environment.NewLine +
                  "==  " + DateTime.Now.ToString("HH:mm:ss.fffff") + "  ==" + Environment.NewLine +
                  "======================");
                Trace($"From: {weapon?.GetType().Name ?? "Unknown_Weapon"}, {weapon?.ShortPrefabName ?? "Unknown_Prefab"}", 1);
                Trace($"To: {entity.GetType().Name}, {entity.ShortPrefabName}", 1);
            }

            // get entity and initiator locations (zones)
            List<string> entityLocations = GetLocationKeys(entity);
            List<string> initiatorLocations = GetLocationKeys(weapon);
            // check for exclusion zones (zones with no rules mapped)
            if (CheckExclusion(entityLocations, initiatorLocations, trace))
            {
                if (trace) Trace("Exclusion found; allow and return", 1);
                return true;
            }

            if (trace) Trace("No exclusion found - looking up RuleSet...", 1);

            // process location rules
            RuleSet ruleSet = GetRuleSet(entityLocations, initiatorLocations);

            if (trace) Trace($"Using RuleSet \"{ruleSet.name}\"", 1);

            if (weapon?.ShortPrefabName == "maincannonshell" || weapon is BradleyAPC)
            {
                if (trace) Trace("Initiator is BradleyAPC; evaluating RuleSet rules...", 1);
                return EvaluateRules(entity, weapon, ruleSet);
            }

            if (ruleSet.HasFlag(RuleFlags.VehiclesTakeCollisionDamageWithoutDriver) && entity is BaseVehicle && weapon == entity)
            {
                var vehicle = entity as BaseVehicle;
                var player = vehicle.GetDriver();

                if (trace) Trace($"Vehicle collision: { (player == null ? "No driver; allow and return" : "Has driver; continue checks") }", 1);

                if (player == null)
                {
                    return true;
                }
            }

            // Check storage containers and doors for locks
            if (ruleSet.HasFlag(RuleFlags.LockedBoxesImmortal) && entity is StorageContainer || ruleSet.HasFlag(RuleFlags.LockedDoorsImmortal) && entity is Door)
            {
                if (!(entity is LootContainer))
                {
                    // check for lock
                    object hurt = CheckLock(ruleSet, entity, hitInfo);
                    if (trace) Trace($"Door/StorageContainer detected with immortal flag; lock check results: { (hurt == null ? "null (no lock or unlocked); continue checks" : (bool)hurt ? "allow and return" : "block and return") }", 1);
                    if (hurt is bool)
                    {
                        return (bool)hurt;
                    }
                }
            }

            // check heli and turret
            object heli = CheckHeliInitiator(ruleSet, hitInfo);

            if (heli is bool)
            {
                if (entity is BasePlayer)
                {
                    if (trace) Trace($"Initiator is heli, and target is player; flag check results: { (ruleSet.HasFlag(RuleFlags.NoHeliDamagePlayer) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoHeliDamagePlayer);
                }
                if (entity is MiningQuarry)
                {
                    if (trace) Trace($"Initiator is heli, and target is quarry; flag check results: { (ruleSet.HasFlag(RuleFlags.NoHeliDamageQuarry) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoHeliDamageQuarry);
                }
                if (trace) Trace($"Initiator is heli, target is non-player; results: { ((bool)heli ? "allow and return" : "block and return") }", 1);
                //return EvaluateRules(entity, weapon, ruleSet);
                return (bool)heli;
            }

            if (weapon is BaseProjectile && hitInfo.Initiator == null)
            {
                var projectile = weapon as BaseProjectile;

                hitInfo.Initiator = projectile.GetOwnerPlayer();
            }

            // after heli check, return true if initiator is null
            if (hitInfo.Initiator == null)
            {
                if (hitInfo.damageTypes.Has(DamageType.Explosion))
                {
                    if (trace) Trace($"Initiator empty for explosion; block and return", 1);
                    return false;
                }
                if (trace) Trace($"Initiator empty; allow and return", 1);
                return true;
            }

            if (ruleSet.HasFlag(RuleFlags.SamSitesIgnorePlayers) && hitInfo.Initiator is SamSite)
            {
                var player = entity is BaseMountable ? GetMountedPlayer(entity as BaseMountable) : entity as BasePlayer;

                if (player.IsValid())
                {
                    // check for exclusions in entity groups
                    bool hasExclusion = CheckExclusion(hitInfo.Initiator);
                    if (trace) Trace($"Initiator is samsite, and target is player; {(hasExclusion ? "exclusion found; allow and return" : "no exclusion; block and return")}", 1);
                    return hasExclusion;
                }
            }

            // handle suicide
            if (hitInfo.damageTypes?.Get(DamageType.Suicide) > 0 && !entity.IsNpc && entity is BasePlayer)
            {
                if (trace) Trace($"DamageType is suicide; blocked? { (ruleSet.HasFlag(RuleFlags.SuicideBlocked) ? "true; block and return" : "false; allow and return") }", 1);
                if (ruleSet.HasFlag(RuleFlags.SuicideBlocked))
                {
                    SendMessage(entity as BasePlayer, "Error_NoSuicide");
                    return false;
                }
                return true;
            }

            // allow players to hurt themselves
            if (ruleSet.HasFlag(RuleFlags.SelfDamage) && !entity.IsNpc && hitInfo.Initiator == entity && entity is BasePlayer)
            {
                if (trace) Trace($"SelfDamage flag; player inflicted damage to self; allow and return", 1);
                return true;
            }

            if (hitInfo.Initiator is MiniCopter && entity is BuildingBlock)
            {
                if (trace) Trace("Initiator is minicopter, target is building; evaluate and return", 1);
                return EvaluateRules(entity, hitInfo, ruleSet);
            }

            if (hitInfo.Initiator is BaseNpc)
            {
                // check for sleeper protection - return false if sleeper protection is on (true)
                if (ruleSet.HasFlag(RuleFlags.ProtectedSleepers) && entity is BasePlayer && (entity as BasePlayer).IsSleeping())
                {
                    if (trace) Trace("Target is sleeping player, with ProtectedSleepers flag set; block and return", 1);
                    return false;
                }

                if (trace) Trace("Initiator is NPC animal; allow and return", 1);
                return true; // allow NPC damage to other entities if sleeper protection is off
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (attacker.IsValid())
            {
                if (attacker.isMounted)
                {
                    if (!EvaluateRules(entity, attacker.GetMounted(), ruleSet, false))
                    {
                        return false;
                    }
                }

                if (entity is AdvancedChristmasLights)
                {
                    if (entity.OwnerID == 0)
                    {
                        return attacker.CanBuild();
                    }
                }
                else if (entity is GrowableEntity)
                {
                    if (attacker.CanBuild())
                    {
                        return true;
                    }

                    var ge = entity as GrowableEntity;
                    var planter = ge.GetPlanter();

                    return planter == null || !planter.OwnerID.IsSteamId() || planter.OwnerID == attacker.userID;
                }
                else if (entity is BuildingBlock)
                {
                    var block = entity as BuildingBlock;

                    if (block.grade == BuildingGrade.Enum.Twigs)
                    {
                        if (ruleSet.HasFlag(RuleFlags.TwigDamageRequiresOwnership)) // Allow twig damage by owner or anyone authed if ruleset flag is set
                        {
                            if (entity.OwnerID == attacker.userID)
                            {
                                return true;
                            }

                            return attacker.IsBuildingAuthed(entity.transform.position, entity.transform.rotation, entity.bounds);
                        }

                        if (ruleSet.HasFlag(RuleFlags.TwigDamage)) // Allow twig damage by anyone if ruleset flag is set
                        {
                            return true;
                        }
                    }
                }

                if (entity is BasePlayer)
                {
                    var victim = entity as BasePlayer;

                    if (ruleSet.HasFlag(RuleFlags.FriendlyFire) && !victim.IsNpc && AreAllies(attacker, victim))
                    {
                        if (trace) Trace("Initiator and target are allied players, with FriendlyFire flag set; allow and return", 1);
                        return true;
                    }

                    // allow sleeper damage by admins if configured
                    if (ruleSet.HasFlag(RuleFlags.AdminsHurtSleepers))
                    {
                        if (victim.IsSleeping() && IsAdmin(attacker))
                        {
                            if (trace) Trace("Initiator is admin player and target is sleeping player, with AdminsHurtSleepers flag set; allow and return", 1);
                            return true;
                        }
                    }

                    // allow Human NPC damage if configured
                    if (ruleSet.HasFlag(RuleFlags.HumanNPCDamage))
                    {
                        if (IsHumanNPC(attacker) || IsHumanNPC(victim))
                        {
                            if (trace) Trace("Initiator or target is HumanNPC, with HumanNPCDamage flag set; allow and return", 1);
                            return true;
                        }
                    }
                }
                else if (ruleSet.HasFlag(RuleFlags.AuthorizedDamage) && !entity.IsNpc) // ignore checks if authorized damage enabled (except for players and npcs)
                {
                    if (ruleSet.HasFlag(RuleFlags.AuthorizedDamageRequiresOwnership) && entity.OwnerID != attacker.userID && CanAuthorize(entity, attacker, ruleSet))
                    {
                        if (trace) Trace("Initiator is player who does not own non-player target; block and return", 1);
                        return false;
                    }

                    if (CheckAuthorized(entity, attacker, ruleSet))
                    {
                        if (entity is SamSite)
                        {
                            if (trace) Trace("Target is SamSite; evaluate and return", 1);
                            return EvaluateRules(entity, hitInfo, ruleSet);
                        }
                        if (trace) Trace("Initiator is player with authorization over non-player target; allow and return", 1);
                        return true;
                    }
                }
            }

            if (trace) Trace("No match in pre-checks; evaluating RuleSet rules...", 1);
            return EvaluateRules(entity, hitInfo, ruleSet);
        }

        public bool AreAllies(BasePlayer attacker, BasePlayer victim)
        {
            if (attacker.currentTeam != 0uL && attacker.Team.members.Contains(victim.userID))
            {
                return true;
            }

            if (Clans != null && Convert.ToBoolean(Clans?.Call("IsMemberOrAlly", attacker.UserIDString, victim.UserIDString)))
            {
                return true;
            }

            if (Friends != null && Convert.ToBoolean(Friends?.Call("AreFriends", attacker.UserIDString, victim.UserIDString)))
            {
                return true;
            }

            return false;
        }

        private const uint WALL_LOW_JPIPE = 310235277;

        private bool CanAuthorize(BaseEntity entity, BasePlayer attacker, RuleSet ruleSet)
        {
            if (entity is BaseVehicle && !EvaluateRules(entity, attacker, ruleSet, false))
            {
                return false;
            }

            if (entity.OwnerID == 0)
            {
                return entity.prefabID == WALL_LOW_JPIPE && !entity.enableSaving;
            }

            return entity is BuildingBlock || entity.PrefabName.Contains("building") || _deployables.Contains(entity.PrefabName);
        }

        private void InitializeDeployables()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                var imd = def.GetComponent<ItemModDeployable>();
                if (imd == null || _deployables.Contains(imd.entityPrefab.resourcePath)) continue;
                _deployables.Add(imd.entityPrefab.resourcePath);
            }
        }

        // process rules to determine whether to allow damage
        private bool EvaluateRules(BaseEntity entity, BaseEntity attacker, RuleSet ruleSet, bool returnDefaultValue = true)
        {
            List<string> e0Groups = data.ResolveEntityGroups(attacker);
            List<string> e1Groups = data.ResolveEntityGroups(entity);

            if (trace)
            {
                Trace($"Initator EntityGroup matches: { (e0Groups == null || e0Groups.Count == 0 ? "none" : string.Join(", ", e0Groups.ToArray())) }", 2);
                Trace($"Target EntityGroup matches: { (e1Groups == null || e1Groups.Count == 0 ? "none" : string.Join(", ", e1Groups.ToArray())) }", 2);
            }

            return ruleSet.Evaluate(e0Groups, e1Groups, returnDefaultValue);
        }

        private bool EvaluateRules(BaseEntity entity, HitInfo hitInfo, RuleSet ruleSet)
        {
            return EvaluateRules(entity, hitInfo.Initiator ?? hitInfo.WeaponPrefab, ruleSet);
        }

        // checks an entity to see if it has a lock
        private object CheckLock(RuleSet ruleSet, BaseEntity entity, HitInfo hitInfo)
        {
            var slot = entity.GetSlot(BaseEntity.Slot.Lock); // check for lock

            if (slot == null || !slot.IsLocked())
            {
                return null; // no lock or unlocked, continue checks
            }

            // if HeliDamageLocked flag is false or NoHeliDamage flag, all damage is cancelled from immortal flag
            if (!ruleSet.HasFlag(RuleFlags.HeliDamageLocked) || ruleSet.HasFlag(RuleFlags.NoHeliDamage))
            {
                return false;
            }

            object heli = CheckHeliInitiator(ruleSet, hitInfo);

            return Convert.ToBoolean(heli); // cancel damage except from heli
        }

        private object CheckHeliInitiator(RuleSet ruleSet, HitInfo hitInfo)
        {
            // Check for heli initiator
            if (hitInfo.Initiator is BaseHelicopter || (hitInfo.Initiator != null && (hitInfo.Initiator.ShortPrefabName.Equals("oilfireballsmall") || hitInfo.Initiator.ShortPrefabName.Equals("napalm"))))
            {
                return !ruleSet.HasFlag(RuleFlags.NoHeliDamage);
            }
            else if (hitInfo.WeaponPrefab != null && (hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli") || hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli_napalm")))
            {
                return !ruleSet.HasFlag(RuleFlags.NoHeliDamage);
            }
            return null;
        }

        // checks if the player is authorized to damage the entity
        private bool CheckAuthorized(BaseEntity entity, BasePlayer player, RuleSet ruleSet)
        {
            if (!ruleSet.HasFlag(RuleFlags.CupboardOwnership))
            {
                if (entity.OwnerID == player.userID || entity.OwnerID == 0)
                {
                    return true; // allow damage to entities that the player owns
                }

                return player.IsBuildingAuthed(entity.WorldSpaceBounds());
            }

            // treat entities outside of cupboard range as unowned, and entities inside cupboard range require authorization
            return player.CanBuild(entity.WorldSpaceBounds());
        }

        private bool IsFunTurret(bool isAutoTurret, BaseEntity entity)
        {
            if (!isAutoTurret)
            {
                return false;
            }

            var weapon = (entity as AutoTurret).GetAttachedWeapon()?.GetItem();

            if (weapon?.info?.shortname?.StartsWith("fun.") ?? false)
            {
                return true;
            }

            return false;
        }

        private BasePlayer GetMountedPlayer(BaseMountable m)
        {
            if (m.GetMounted())
            {
                return m.GetMounted();
            }

            if (m is BaseVehicle)
            {
                var vehicle = m as BaseVehicle;

                foreach (var point in vehicle.mountPoints)
                {
                    if (point.mountable.IsValid() && point.mountable.GetMounted())
                    {
                        return point.mountable.GetMounted();
                    }
                }
            }

            return null;
        }

        private object OnSamSiteTarget(SamSite ss, BaseMountable m)
        {
            var entity = GetMountedPlayer(m) as BaseCombatEntity ?? m as BaseCombatEntity;
            object extCanEntityBeTargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { entity, ss });

            if (extCanEntityBeTargeted is bool)
            {
                if ((bool)extCanEntityBeTargeted)
                {
                    if (trace) Trace($"CanEntityBeTargeted allowed {entity.ShortPrefabName} to be targetted by SamSite", 1);
                    return null;
                }

                if (trace) Trace($"CanEntityBeTargeted blocked {entity.ShortPrefabName} from being targetted by SamSite", 1);
                ss.CancelInvoke(ss.WeaponTick);
                return false;
            }

            RuleSet ruleSet = GetRuleSet(entity, ss);

            if (ruleSet == null)
            {
                return null;
            }

            if (ruleSet.HasFlag(RuleFlags.SamSitesIgnorePlayers))
            {
                var entityLocations = GetLocationKeys(m);
                if (trace) Trace($"OnSamSiteTarget allowed {entity.ShortPrefabName} to be targetted; no ruleset found.", 1);
                var initiatorLocations = GetLocationKeys(ss);

                // check for exclusion zones (zones with no rules mapped)
                if (CheckExclusion(entityLocations, initiatorLocations, false))
                {
                    return null;
                }

                // check for exclusions in entity groups
                if (CheckExclusion(ss))
                {
                    return null;
                }

                ss.CancelInvoke(ss.WeaponTick);
                return false;
            }

            return null;
        }

        private object CanBeTargeted(BaseMountable m, MonoBehaviour mb)
        {
            var player = GetMountedPlayer(m);

            return CanBeTargeted(player, mb);
        }

        // check if entity can be targeted
        private object CanBeTargeted(BasePlayer target, MonoBehaviour mb)
        {
            //if (trace) Trace($"CanBeTargeted called for {target.name}", 2);
            if (target == null || mb == null || mb is HelicopterTurret)
            {
                return null;
            }

            var entity = mb as BaseEntity;

            object extCanEntityBeTargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { target, entity });

            if (extCanEntityBeTargeted is bool)
            {
                if ((bool)extCanEntityBeTargeted)
                {
                    if (trace) Trace($"CanEntityBeTargeted allowed {target.displayName} to be targetted by {entity.ShortPrefabName}", 1);
                    return null;
                }

                if (trace) Trace($"CanEntityBeTargeted allowed {target.displayName} from being targetted by {entity.ShortPrefabName}", 1);
                return false;
            }

            RuleSet ruleSet = GetRuleSet(target, entity);

            if (ruleSet == null)
            {
                return null;
            }
                        
            var isAutoTurret = entity is AutoTurret;

            if (target.IsNpc)
            {
                if (isAutoTurret)
                {
                    var obj = ruleSet.HasFlag(RuleFlags.TurretsIgnoreScientist) ? false : (object)null;
                    if (trace) Trace($"CanBeTargeted {target.ShortPrefabName} targetted by {entity.ShortPrefabName} was {(obj is bool ? "blocked" : "allowed")}", 1);
                    return obj;
                }
                else
                {
                    var obj = ruleSet.HasFlag(RuleFlags.TrapsIgnoreScientist) ? false : (object)null;
                    if (trace) Trace($"CanBeTargeted {target.ShortPrefabName} targetted by {entity.ShortPrefabName} was {(obj is bool ? "blocked" : "allowed")}", 1);
                    return obj;
                }
            }
            else if (isAutoTurret && ruleSet.HasFlag(RuleFlags.TurretsIgnorePlayers) || !isAutoTurret && ruleSet.HasFlag(RuleFlags.TrapsIgnorePlayers))
            {
                if (IsFunTurret(isAutoTurret, entity))
                {
                    if (trace) Trace($"CanBeTargeted {target.displayName} targetted by turret with fun weapon was allowed", 1);
                    return null;
                }

                var entityLocations = GetLocationKeys(target);
                var initiatorLocations = GetLocationKeys(entity);

                // check for exclusion zones (zones with no rules mapped)
                if (CheckExclusion(entityLocations, initiatorLocations, true))
                {
                    if (trace) Trace($"CanBeTargeted {target.displayName} targetted by {entity.ShortPrefabName} was allowed by zone exclusion", 1);
                    return null;
                }

                // check for exclusions in entity group
                if (CheckExclusion(target, entity))
                {
                    if (trace) Trace($"CanBeTargeted {target.displayName} targetted by {entity.ShortPrefabName} was allowed by entity group exclusion", 1);
                    return null;
                }

                var source = isAutoTurret && ruleSet.HasFlag(RuleFlags.TurretsIgnorePlayers) ? "TurretsIgnorePlayers" : "TrapsIgnorePlayers";
                if (trace) Trace($"CanBeTargeted {target.displayName} targetted by {entity.ShortPrefabName} was blocked by {source}", 1);
                return false;
            }

            return null;
        }

        // ignore players stepping on traps if configured
        private object OnTrapTrigger(BaseTrap trap, GameObject go)
        {
            var player = go.GetComponent<BasePlayer>();

            if (player == null || trap == null)
            {
                return null;
            }

            object extCanEntityTrapTrigger = Interface.CallHook("CanEntityTrapTrigger", new object[] { trap, player });

            if (extCanEntityTrapTrigger is bool)
            {
                if ((bool)extCanEntityTrapTrigger)
                {
                    return null;
                }

                return true;
            }

            var entityLocations = GetLocationKeys(player);
            var initiatorLocations = GetLocationKeys(trap);

            // check for exclusion zones (zones with no rules mapped)
            if (CheckExclusion(entityLocations, initiatorLocations, false))
            {
                return null;
            }

            if (CheckExclusion(player, trap))
            {
                return null;
            }

            RuleSet ruleSet = GetRuleSet(player, trap);

            if (ruleSet == null)
            {
                return null;
            }

            return (player.IsNpc && ruleSet.HasFlag(RuleFlags.TrapsIgnoreScientist)) || (!player.IsNpc && ruleSet.HasFlag(RuleFlags.TrapsIgnorePlayers)) ? true : (object)null;
        }

        private object OnNpcTarget(BaseNpc npc, BasePlayer target)
        {
            if (!target.IsValid() || target.IsNpc || !target.IsSleeping())
            {
                return null;
            }

            RuleSet ruleSet = GetRuleSet(target, npc);

            if (ruleSet == null || !ruleSet.HasFlag(RuleFlags.AnimalsIgnoreSleepers) && !animalsIgnoreSleepers)
            {
                return null;
            }

            var entityLocations = GetLocationKeys(target);
            var initiatorLocations = GetLocationKeys(npc);

            // check for exclusion zones (zones with no rules mapped)
            if (CheckExclusion(entityLocations, initiatorLocations, false))
            {
                return null;
            }

            return false;
        }

        // Check for exclusions in entity groups (attacker)
        private bool CheckExclusion(BaseEntity attacker)
        {
            string attackerName = attacker.GetType().Name;

            return data.groups.Any(group => group.IsExclusion(attacker.ShortPrefabName) || group.IsExclusion(attackerName));
        }

        // Check for exclusions in entity groups (target, attacker)
        private bool CheckExclusion(BaseEntity target, BaseEntity attacker)
        {
            string targetName = target.GetType().Name;

            if (!data.groups.Any(group => group.IsMember(target.ShortPrefabName) || group.IsExclusion(targetName)))
            {
                return false;
            }

            string attackerName = attacker.GetType().Name;

            return data.groups.Any(group => group.IsExclusion(attacker.ShortPrefabName) || group.IsExclusion(attackerName));
        }

        private RuleSet GetRuleSet(List<string> vicLocations, List<string> atkLocations)
        {
            RuleSet ruleSet = currentRuleSet;

            if (atkLocations == null) atkLocations = vicLocations; // Allow TruePVE to be used on PVP servers that want to add PVE zones via Zone Manager (just do this inside of Zone Manager instead...)

            if (vicLocations?.Count > 0 && atkLocations?.Count > 0)
            {
                if (trace) Trace($"Beginning RuleSet lookup for [{ (vicLocations.Count == 0 ? "empty" : string.Join(", ", vicLocations.ToArray())) }] and [{ (atkLocations.Count == 0 ? "empty" : string.Join(", ", atkLocations.ToArray())) }]", 2);

                var locations = GetSharedLocations(vicLocations, atkLocations);

                if (trace) Trace($"Shared locations: { (locations.Count == 0 ? "none" : string.Join(", ", locations.ToArray())) }", 3);

                if (locations?.Count > 0)
                {
                    var names = locations.Select(s => data.mappings[s]).ToList();
                    var sets = data.ruleSets.Where(r => names.Contains(r.name)).ToList();

                    if (trace) Trace($"Found {names.Count} location names, with {sets.Count} mapped RuleSets", 3);

                    if (sets.Count == 0 && data.mappings.ContainsKey(AllZones) && data.ruleSets.Any(r => r.name == data.mappings[AllZones]))
                    {
                        sets.Add(data.ruleSets.FirstOrDefault(r => r.name == data.mappings[AllZones]));
                        if (trace) Trace($"Found allzones mapped RuleSet", 3);
                    }

                    if (sets.Count > 1)
                    {
                        if (trace) Trace($"WARNING: Found multiple RuleSets: {string.Join(", ", sets.Select(s => s.name).ToArray())}", 3);
                        PrintWarning(GetMessage("Warning_MultipleRuleSets"), string.Join(", ", sets.Select(s => s.name).ToArray()));
                    }

                    ruleSet = sets.FirstOrDefault();
                    if (trace) Trace($"Found RuleSet: {ruleSet?.name ?? "null"}", 3);
                }
            }

            if (ruleSet == null)
            {
                ruleSet = currentRuleSet;
                if (trace) Trace($"No RuleSet found; assigned current global RuleSet: {ruleSet?.name ?? "null"}", 3);
            }

            return ruleSet;
        }

        private RuleSet GetRuleSet(BaseEntity e0, BaseEntity e1)
        {
            //if (!serverInitialized) return currentRuleSet;
            List<string> e0Locations = GetLocationKeys(e0);
            List<string> e1Locations = GetLocationKeys(e1);
            return GetRuleSet(e0Locations, e1Locations);
        }

        // get locations shared between the two passed location lists
        private List<string> GetSharedLocations(List<string> e0Locations, List<string> e1Locations)
        {
            return e0Locations.Intersect(e1Locations).Where(s => data.HasMapping(s)).ToList();
        }

        // Check exclusion for given entity locations
        private bool CheckExclusion(List<string> e0Locations, List<string> e1Locations, bool trace)
        {
            if (e0Locations == null || e1Locations == null)
            {
                if (trace) Trace("No shared locations (empty location) - no exclusions", 3);
                return false;
            }
            if (trace) Trace($"Checking exclusions between [{ (e0Locations.Count == 0 ? "empty" : string.Join(", ", e0Locations.ToArray())) }] and [{ (e1Locations.Count == 0 ? "empty" : string.Join(", ", e1Locations.ToArray())) }]", 2);
            List<string> locations = GetSharedLocations(e0Locations, e1Locations);
            if (trace) Trace($"Shared locations: {(locations.Count == 0 ? "none" : string.Join(", ", locations.ToArray()))}", 3);
            if (locations != null && locations.Count > 0)
            {
                foreach (string loc in locations)
                {
                    if (data.HasEmptyMapping(loc))
                    {
                        if (trace) Trace($"Found exclusion mapping for location: {loc}", 3);
                        return true;
                    }
                }
            }
            if (trace) Trace("No shared locations, or no matching exclusion mapping - no exclusions", 3);
            return false;
        }

        // add or update a mapping
        private bool AddOrUpdateMapping(string key, string ruleset)
        {
            if (!serverInitialized || string.IsNullOrEmpty(key) || ruleset == null || (!data.ruleSets.Select(r => r.name).Contains(ruleset) && ruleset != "exclude"))
                return false;

            if (data.HasMapping(key))
                // update existing mapping
                data.mappings[key] = ruleset;
            else
                // add new mapping
                data.mappings.Add(key, ruleset);
            SaveData();

            return true;
        }

        // remove a mapping
        private bool RemoveMapping(string key)
        {
            if (!serverInitialized || string.IsNullOrEmpty(key))
                return false;
            if (data.mappings.Remove(key))
            {
                SaveData();
                return true;
            }
            return false;
        }
        #endregion

        #region Messaging
        // send message to player (chat)
        private void SendMessage(BasePlayer player, string key, object[] options = null) => SendReply(player, BuildMessage(player, key, options));

        // send message to player (console)
        private void SendMessage(ConsoleSystem.Arg arg, string key, object[] options = null) => SendReply(arg, RemoveFormatting(BuildMessage(null, key, options)));

        // build message string
        private string BuildMessage(BasePlayer player, string key, object[] options = null)
        {
            string message = GetMessage(key, player?.UserIDString);
            if (options?.Length > 0)
                message = string.Format(message, options);
            string type = key.Split('_')[0];
            if (player != null)
            {
                string size = GetMessage("Format_" + type + "Size");
                string color = GetMessage("Format_" + type + "Color");
                return WrapSize(size, WrapColor(color, message));
            }
            else
            {
                string color = GetMessage("Format_" + type + "Color");
                return WrapColor(color, message);
            }
        }

        // prints the value of an Option
        private void PrintValue(ConsoleSystem.Arg arg, string text, bool value)
        {
            SendReply(arg, WrapSize(GetMessage("Format_NotifySize"), WrapColor(GetMessage("Format_NotifyColor"), text + ": ") + value));
        }

        // wrap string in <size> tag, handles parsing size string to integer
        private string WrapSize(string size, string input)
        {
            int i;
            if (int.TryParse(size, out i))
                return WrapSize(i, input);
            return input;
        }

        // wrap a string in a <size> tag with the passed size
        private string WrapSize(int size, string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            return "<size=" + size + ">" + input + "</size>";
        }

        // wrap a string in a <color> tag with the passed color
        private string WrapColor(string color, string input)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(color))
                return input;
            return "<color=" + color + ">" + input + "</color>";
        }

        // show usage information
        private void ShowUsage(ConsoleSystem.Arg arg) => SendReply(arg, RemoveFormatting(usageString));

        public string RemoveFormatting(string source) => source.Contains(">") ? Regex.Replace(source, "<.*?>", string.Empty) : source;

        // warn that the server is set to PVE mode
        private void WarnPve() => PrintWarning(GetMessage("Warning_PveMode"));
        #endregion

        #region Helper Procedures
        // is admin
        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (!player.IsConnected) return true;
            return player.net.connection.authLevel > 0;
        }

        // is player a HumanNPC
        private bool IsHumanNPC(BasePlayer player)
        {
            return player.userID < 76560000000000000L && player.userID > 0L;
        }

        // get location keys from ZoneManager (zone IDs) or LiteZones (zone names)
        private List<string> GetLocationKeys(BaseEntity entity)
        {
            if (!useZones || entity == null) return null;
            List<string> locations = new List<string>();
            string zname;
            if (ZoneManager != null)
            {
                List<string> zmloc = new List<string>();
                if (ZoneManager.Version >= new VersionNumber(3, 0, 1))
                {
                    if (entity is BasePlayer)
                    {
                        // BasePlayer fix from chadomat
                        string[] zmlocplr = (string[])ZoneManager.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                        foreach (string s in zmlocplr)
                        {
                            zmloc.Add(s);
                        }
                    }
                    else if (entity.IsValid())
                    {
                        string[] zmlocent = (string[])ZoneManager.Call("GetEntityZoneIDs", new object[] { entity });
                        foreach (string s in zmlocent)
                        {
                            zmloc.Add(s);
                        }
                    }
                }
                else if (ZoneManager.Version < new VersionNumber(3, 0, 0))
                {
                    if (entity is BasePlayer)
                    {
                        string[] zmlocplr = (string[])ZoneManager.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                        foreach (string s in zmlocplr)
                        {
                            zmloc.Add(s);
                        }
                    }
                    else if (entity.IsValid())
                    {
                        zmloc = (List<string>)ZoneManager.Call("GetEntityZones", new object[] { entity });
                    }
                }
                else // Skip ZM version 3.0.0
                {
                    zmloc = null;
                }

                if (zmloc != null && zmloc.Count > 0)
                {
                    // Add names into list of ID numbers
                    foreach (string s in zmloc)
                    {
                        locations.Add(s);
                        zname = (string)ZoneManager.Call("GetZoneName", s);
                        if (zname != null) locations.Add(zname);
                        if (trace) Puts($"Found zone {zname}: {s}");
                    }
                }
            }
            if (LiteZones != null)
            {
                List<string> lzloc = (List<string>)LiteZones?.Call("GetEntityZones", new object[] { entity });
                if (lzloc != null && lzloc.Count > 0)
                {
                    locations.AddRange(lzloc);
                }
            }
            //if (locations == null || locations.Count == 0) return null;
            return locations;
        }

        // check user access
        bool HasAccess(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                if (arg.Connection.authLevel < 1)
                {
                    SendMessage(arg, "Error_NoPermission");
                    return false;
                }
            }
            return true;
        }

        // handle raycast from player (for prodding)
        private bool GetRaycastTarget(BasePlayer player, out object closestEntity)
        {
            closestEntity = false;

            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
            {
                closestEntity = hit.GetEntity();
                return true;
            }
            return false;
        }

        // loop to update current ruleset
        private void TimerLoop(bool firstRun = false)
        {
            string ruleSetName;
            data.schedule.ClockUpdate(out ruleSetName, out currentBroadcastMessage);
            if (currentRuleSet.name != ruleSetName || firstRun)
            {
                currentRuleSet = data.ruleSets.FirstOrDefault(r => r.name == ruleSetName);
                if (currentRuleSet == null)
                    currentRuleSet = new RuleSet(ruleSetName); // create empty ruleset to hold name
                if (data.schedule.broadcast && currentBroadcastMessage != null)
                {
                    Server.Broadcast(currentBroadcastMessage, GetMessage("Prefix"));
                    Puts(RemoveFormatting(GetMessage("Prefix") + " Schedule Broadcast: " + currentBroadcastMessage));
                }
            }

            if (data.schedule.enabled)
                scheduleUpdateTimer = timer.Once(data.schedule.useRealtime ? 30f : 3f, () => TimerLoop());
        }

        #endregion

        #region Subclasses
        // configuration and data storage container

        private class ConfigurationOptions
        {
            [JsonProperty(PropertyName = "handleDamage")] // (true) enable TruePVE damage handling hooks
            public bool handleDamage { get; set; } = true;

            [JsonProperty(PropertyName = "useZones")] // (true) use ZoneManager/LiteZones for zone-specific damage behavior (requires modification of ZoneManager.cs)
            public bool useZones { get; set; } = true;

            [JsonProperty(PropertyName = "Trace To Player Console")]
            public bool PlayerConsole { get; set; }

            [JsonProperty(PropertyName = "Trace To Server Console")]
            public bool ServerConsole { get; set; }

            [JsonProperty(PropertyName = "Maximum Distance From Player To Trace")]
            public float MaxTraceDistance { get; set; }
        }

        private class Configuration
        {
            [JsonProperty(PropertyName = "Config Version")]
            public string configVersion = null;
            [JsonProperty(PropertyName = "Default RuleSet")]
            public string defaultRuleSet = "default";
            [JsonProperty(PropertyName = "Configuration Options")]
            public ConfigurationOptions config = new ConfigurationOptions();
            [JsonProperty(PropertyName = "Mappings")]
            public Dictionary<string, string> mappings = new Dictionary<string, string>();
            [JsonProperty(PropertyName = "Schedule")]
            public Schedule schedule = new Schedule();
            [JsonProperty(PropertyName = "RuleSets")]
            public List<RuleSet> ruleSets = new List<RuleSet>();
            [JsonProperty(PropertyName = "Entity Groups")]
            public List<EntityGroup> groups { get; set; } = new List<EntityGroup>();
            [JsonProperty(PropertyName = "Allow Killing Sleepers")]
            public bool AllowKillingSleepers;

            Dictionary<uint, List<string>> groupCache = new Dictionary<uint, List<string>>();

            public void Init()
            {                
                schedule.Init();
                foreach (RuleSet rs in ruleSets)
                    rs.Build();
                ruleSets.Remove(null);
            }

            public List<string> ResolveEntityGroups(BaseEntity entity)
            {
                if (entity != null)
                {
                    if (entity.net != null)
                    {
                        List<string> groupList;
                        if (!groupCache.TryGetValue(entity.net.ID, out groupList))
                        {
                            groupList = groups.Where(g => g.Contains(entity)).Select(g => g.name).ToList();
                            groupCache[entity.net.ID] = groupList;
                        }
                        return groupList;
                    }

                    return groups.Where(g => g.Contains(entity)).Select(g => g.name).ToList();
                }

                return null;
            }

            public bool HasMapping(string key)
            {
                return mappings.ContainsKey(key) || mappings.ContainsKey(AllZones);
            }

            public bool HasEmptyMapping(string key)
            {
                if (mappings.ContainsKey(AllZones) && mappings[AllZones].Equals("exclude")) return true; // exlude all zones
                if (!mappings.ContainsKey(key)) return false;
                if (mappings[key].Equals("exclude")) return true;
                RuleSet r = ruleSets.FirstOrDefault(rs => rs.name.Equals(mappings[key]));
                if (r == null) return true;
                return r.IsEmpty();
            }

            public RuleSet GetDefaultRuleSet()
            {
                try
                {
                    return ruleSets.Single(r => r.name == defaultRuleSet);
                }
                catch (Exception)
                {
                    Interface.Oxide.LogWarning($"Warning - duplicate ruleset found for default RuleSet: '{defaultRuleSet}'");
                    return ruleSets.FirstOrDefault(r => r.name == defaultRuleSet);
                }
            }
        }

        private class RuleSet
        {
            public string name;
            public bool enabled = true;
            public bool defaultAllowDamage = false;
            public string flags = string.Empty;
            [JsonIgnore]
            public RuleFlags _flags = RuleFlags.None;

            public HashSet<string> rules = new HashSet<string>();
            HashSet<Rule> parsedRules = new HashSet<Rule>();

            public RuleSet() { }
            public RuleSet(string name) { this.name = name; }

            // evaluate the passed lists of entity groups against rules
            public bool Evaluate(List<string> eg1, List<string> eg2, bool returnDefaultValue = true)
            {
                if (Instance.trace) Instance.Trace("Evaluating Rules...", 3);
                if (parsedRules == null || parsedRules.Count == 0)
                {
                    if (Instance.trace) Instance.Trace($"No rules found; returning default value: {defaultAllowDamage}", 4);
                    return defaultAllowDamage;
                }
                bool? res;
                if (Instance.trace) Instance.Trace("Checking direct initiator->target rules...", 4);
                // check all direct links
                if (eg1 != null && eg1.Count > 0 && eg2 != null && eg2.Count > 0)
                    foreach (string s1 in eg1)
                        foreach (string s2 in eg2)
                            if ((res = Evaluate(s1, s2)).HasValue) return res.Value;

                if (Instance.trace) Instance.Trace("No direct match rules found; continuing...", 4);
                if (eg1 != null && eg1.Count > 0)
                    // check group -> any
                    foreach (string s1 in eg1)
                        if ((res = Evaluate(s1, Any)).HasValue) return res.Value;

                if (Instance.trace) Instance.Trace("No matching initiator->any rules found; continuing...", 4);
                if (eg2 != null && eg2.Count > 0)
                    // check any -> group
                    foreach (string s2 in eg2)
                        if ((res = Evaluate(Any, s2)).HasValue) return res.Value;

                if (returnDefaultValue)
                {
                    if (Instance.trace) Instance.Trace($"No matching any->target rules found; returning default value: {defaultAllowDamage}", 4);
                    return defaultAllowDamage;
                }

                return true;
            }

            // evaluate two entity groups against rules
            public bool? Evaluate(string eg1, string eg2)
            {
                if (eg1 == null || eg2 == null || parsedRules == null || parsedRules.Count == 0) return null;
                if (Instance.trace) Instance.Trace($"Evaluating \"{eg1}->{eg2}\"...", 5);
                Rule rule = parsedRules.FirstOrDefault(r => r.valid && r.key.Equals(eg1 + "->" + eg2));
                if (rule != null)
                {
                    if (Instance.trace) Instance.Trace($"Match found; allow damage? {rule.hurt}", 6);
                    return rule.hurt;
                }
                if (Instance.trace) Instance.Trace($"No match found", 6);
                return null;
            }

            // build rule strings to rules
            public void Build()
            {
                foreach (string ruleText in rules)
                    parsedRules.Add(new Rule(ruleText));
                parsedRules.Remove(null);
                ValidateRules();
                if (flags.Contains(","))
                {
                    foreach (string flagText in flags.Split(','))
                    {
                        RuleFlags flag;
                        if (Enum.TryParse(flagText, out flag))
                        {
                            if (!_flags.HasFlag(flag))
                            {
                                _flags |= flag;
                            }
                        }
                        else Instance.Puts("WARNING - invalid flag: {0} (does this flag still exist?)", flagText.Trim());
                    }
                }
            }

            public void ValidateRules()
            {
                foreach (Rule rule in parsedRules)
                    if (!rule.valid)
                        Interface.Oxide.LogWarning($"Warning - invalid rule: {rule.ruleText}");
            }

            // add a rule
            public void AddRule(string ruleText)
            {
                rules.Add(ruleText);
                parsedRules.Add(new Rule(ruleText));
            }

            public bool HasAnyFlag(RuleFlags flags) { return (this._flags | flags) != RuleFlags.None; }
            public bool HasFlag(RuleFlags flag) { return (_flags & flag) == flag; }
            public bool IsEmpty() { return (rules == null || rules.Count == 0) && _flags == RuleFlags.None; }
        }

        private class Rule
        {
            public string ruleText;
            [JsonIgnore]
            public string key;
            [JsonIgnore]
            public bool hurt;
            [JsonIgnore]
            public bool valid;

            public Rule() { }
            public Rule(string ruleText)
            {
                this.ruleText = ruleText;
                valid = RuleTranslator.Translate(this);
            }

            public override int GetHashCode() { return key.GetHashCode(); }

            public override bool Equals(object obj)
            {
                if (obj == null) return false;
                if (obj == this) return true;
                if (obj is Rule)
                    return key.Equals((obj as Rule).key);
                return false;
            }
        }

        // helper class to translate rule text to rules
        private class RuleTranslator
        {
            static readonly Regex regex = new Regex(@"\s+");
            static readonly List<string> synonyms = new List<string>() { "anything", "nothing", "all", "any", "none", "everything" };
            public static bool Translate(Rule rule)
            {
                if (rule.ruleText == null || rule.ruleText.Equals("")) return false;
                string str = rule.ruleText;
                string[] splitStr = regex.Split(str);
                // first and last words should be ruleset names
                string rs0 = splitStr[0];
                string rs1 = splitStr[splitStr.Length - 1];
                string[] mid = splitStr.Skip(1).Take(splitStr.Length - 2).ToArray();
                if (mid == null || mid.Length == 0) return false;

                bool canHurt = true;
                foreach (string s in mid)
                    if (s.Equals("cannot") || s.Equals("can't"))
                        canHurt = false;

                // rs0 and rs1 shouldn't ever be "nothing" simultaneously
                if (rs0.Equals("nothing") || rs1.Equals("nothing") || rs0.Equals("none") || rs1.Equals("none")) canHurt = !canHurt;

                if (synonyms.Contains(rs0)) rs0 = Any;
                if (synonyms.Contains(rs1)) rs1 = Any;

                rule.key = rs0 + "->" + rs1;
                rule.hurt = canHurt;
                return true;
            }
        }

        // container for mapping entities
        private class EntityGroup
        {
            private List<string> memberList { get; set; } = new List<string>();
            private List<string> exclusionList { get; set; } = new List<string>();
            public string name { get; set; }

            public string members
            {
                get
                {
                    if (memberList.Count == 0) return string.Empty;
                    return string.Join(", ", memberList.ToArray());
                }
                set
                {
                    if (string.IsNullOrEmpty(value)) return;
                    memberList = value.Split(',').Select(s => s.Trim()).ToList();
                }
            }

            public string exclusions
            {
                get
                {
                    if (exclusionList.Count == 0) return string.Empty;
                    return string.Join(", ", exclusionList.ToArray());
                }
                set
                {
                    if (string.IsNullOrEmpty(value)) return;
                    exclusionList = value.Split(',').Select(s => s.Trim()).ToList();
                }
            }

            public EntityGroup()
            {

            }

            public EntityGroup(string name)
            {
                this.name = name;
            }

            public bool IsMember(string value) => memberList.Any(member => member.Equals(value, StringComparison.OrdinalIgnoreCase));

            public bool IsExclusion(string value) => exclusionList.Any(exclusion => exclusion.Equals(value, StringComparison.OrdinalIgnoreCase));

            public bool Contains(BaseEntity entity)
            {
                if (entity == null) return false;
                return (memberList.Contains(entity.GetType().Name) || memberList.Contains(entity.ShortPrefabName)) && !(exclusionList.Contains(entity.GetType().Name) || exclusionList.Contains(entity.ShortPrefabName));
            }
        }

        // scheduler
        private class Schedule
        {
            public bool enabled = false;
            public bool useRealtime = false;
            public bool broadcast = false;
            public List<string> entries = new List<string>();
            List<ScheduleEntry> parsedEntries = new List<ScheduleEntry>();
            [JsonIgnore]
            public bool valid = false;

            public void Init()
            {
                foreach (string str in entries)
                    parsedEntries.Add(new ScheduleEntry(str));
                // schedule not valid if entries are empty, there are less than 2 entries, or there are less than 2 rulesets defined
                if (parsedEntries == null || parsedEntries.Count == 0 || parsedEntries.Count(e => e.valid) < 2 || parsedEntries.Select(e => e.ruleSet).Distinct().Count() < 2)
                    enabled = false;
                else
                    valid = true;
            }

            // returns delta between current time and next schedule entry
            public void ClockUpdate(out string ruleSetName, out string message)
            {
                TimeSpan time = useRealtime ? new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0).Add(DateTime.Now.TimeOfDay) : TOD_Sky.Instance.Cycle.DateTime.TimeOfDay;
                try
                {
                    ScheduleEntry se = null;
                    // get the most recent schedule entry
                    if (parsedEntries.Where(t => !t.isDaily).Count() > 0)
                        se = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.time <= time && ((useRealtime && !t.isDaily) || !useRealtime)).Max(t => t.time));
                    // if realtime, check for daily
                    if (useRealtime)
                    {
                        ScheduleEntry daily = null;
                        try
                        {
                            daily = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.time <= DateTime.Now.TimeOfDay && t.isDaily).Max(t => t.time));
                        }
                        catch (Exception)
                        { // no daily entries
                        }
                        if (daily != null && se == null)
                            se = daily;
                        if (daily != null && daily.time.Add(new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0)) > se.time)
                            se = daily;
                    }
                    ruleSetName = se.ruleSet;
                    message = se.message;
                }
                catch (Exception)
                {
                    ScheduleEntry se = null;
                    // if time is earlier than all schedule entries, use max time
                    if (parsedEntries.Where(t => !t.isDaily).Count() > 0)
                        se = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && ((useRealtime && !t.isDaily) || !useRealtime)).Max(t => t.time));
                    if (useRealtime)
                    {
                        ScheduleEntry daily = null;
                        try
                        {
                            daily = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.isDaily).Max(t => t.time));
                        }
                        catch (Exception)
                        { // no daily entries
                        }
                        if (daily != null && se == null)
                            se = daily;
                        if (daily != null && daily.time.Add(new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0)) > se.time)
                            se = daily;
                    }
                    ruleSetName = se?.ruleSet;
                    message = se?.message;
                }
            }
        }

        // helper class to translate schedule text to schedule entries
        private class ScheduleTranslator
        {
            static readonly Regex regex = new Regex(@"\s+");
            public static bool Translate(ScheduleEntry entry)
            {
                if (entry.scheduleText == null || entry.scheduleText.Equals("")) return false;
                string str = entry.scheduleText;
                string[] splitStr = regex.Split(str, 3); // split into 3 parts
                // first word should be a timespan
                string ts = splitStr[0];
                // second word should be a ruleset name
                string rs = splitStr[1];
                // remaining should be message
                string message = splitStr.Length > 2 ? splitStr[2] : null;

                try
                {
                    if (ts.StartsWith("*."))
                    {
                        entry.isDaily = true;
                        ts = ts.Substring(2);
                    }
                    entry.time = TimeSpan.Parse(ts);
                    entry.ruleSet = rs;
                    entry.message = message;
                    return true;
                }
                catch
                { }

                return false;
            }
        }

        private class ScheduleEntry
        {
            public string ruleSet;
            public string message;
            public string scheduleText;
            public bool valid;
            public TimeSpan time { get; set; }
            [JsonIgnore]
            public bool isDaily = false;

            public ScheduleEntry() { }
            public ScheduleEntry(string scheduleText)
            {
                this.scheduleText = scheduleText;
                valid = ScheduleTranslator.Translate(this);
            }
        }
        #endregion

        #region Lang
        // load default messages to Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Prefix", "<color=#FFA500>[ TruePVE ]</color>" },
                {"Enable", "TruePVE enable set to {0}" },

                {"Header_Usage", "---- TruePVE usage ----"},
                {"Cmd_Usage_def", "Loads default configuration and data"},
                {"Cmd_Usage_sched", "Enable or disable the schedule" },
                {"Cmd_Usage_prod", "Show the prefab name and type of the entity being looked at"},
                {"Cmd_Usage_map", "Create/remove a mapping entry" },
                {"Cmd_Usage_trace", "Toggle tracing on/off" },

                {"Warning_PveMode", "ConVar server.pve is TRUE!  TruePVE is designed for PVP mode, and may cause unexpected behavior in PVE mode."},
                {"Warning_OldConfig", "Old config detected - moving to {0}" },
                {"Warning_NoRuleSet", "No RuleSet found for \"{0}\"" },
                {"Warning_DuplicateRuleSet", "Multiple RuleSets found for \"{0}\"" },

                {"Error_InvalidCommand", "Invalid command" },
                {"Error_InvalidParameter", "Invalid parameter: {0}"},
                {"Error_InvalidParamForCmd", "Invalid parameters for command \"{0}\""},
                {"Error_InvalidMapping", "Invalid mapping: {0} => {1}; Target must be a valid RuleSet or \"exclude\"" },
                {"Error_NoMappingToDelete", "Cannot delete mapping: \"{0}\" does not exist" },
                {"Error_NoPermission", "Cannot execute command: No permission"},
                {"Error_NoSuicide", "You are not allowed to commit suicide"},
                {"Error_NoEntityFound", "No entity found"},

                {"Notify_AvailOptions", "Available Options: {0}"},
                {"Notify_DefConfigLoad", "Loaded default configuration"},
                {"Notify_DefDataLoad", "Loaded default mapping data"},
                {"Notify_ProdResult", "Prod results: type={0}, prefab={1}"},
                {"Notify_SchedSetEnabled", "Schedule enabled" },
                {"Notify_SchedSetDisabled", "Schedule disabled" },
                {"Notify_InvalidSchedule", "Schedule is not valid" },
                {"Notify_MappingCreated", "Mapping created for \"{0}\" => \"{1}\"" },
                {"Notify_MappingUpdated", "Mapping for \"{0}\" changed from \"{1}\" to \"{2}\"" },
                {"Notify_MappingDeleted", "Mapping for \"{0}\" => \"{1}\" deleted" },
                {"Notify_TraceToggle", "Trace mode toggled {0}" },

                {"Format_EnableColor", "#00FFFF"}, // cyan
                {"Format_EnableSize", "12"},
                {"Format_NotifyColor", "#00FFFF"}, // cyan
                {"Format_NotifySize", "12"},
                {"Format_HeaderColor", "#FFA500"}, // orange
                {"Format_HeaderSize", "14"},
                {"Format_ErrorColor", "#FF0000"}, // red
                {"Format_ErrorSize", "12"},
            }, this);
        }

        // get message from Lang
        private string GetMessage(string key, string userId = null) => lang.GetMessage(key, this, userId);
        #endregion
    }
}