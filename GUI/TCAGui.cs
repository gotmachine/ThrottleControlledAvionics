﻿/* The GUI for ThrottleControlledAvionics.
 * Authors: Quinten Feys, Willem van Vliet, Allis Tauri
 * License: BY: Attribution-ShareAlike 3.0 Unported (CC BY-SA 3.0): http://creativecommons.org/licenses/by-sa/3.0/
 */

using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using AT_Utils;

namespace ThrottleControlledAvionics
{
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class TCAGui : AddonWindowBase<TCAGui>
	{
		Vessel vessel;
		public ModuleTCA TCA { get; private set; }
		public VesselWrapper VSL { get { return TCA.VSL; } }
		internal static Globals GLB { get { return Globals.Instance; } }
		public VesselConfig CFG { get { return TCA.CFG; } }

		#region modules
		TimeWarpControl WRP;
		SquadControl SQD;
		AltitudeControl ALT;
		VerticalSpeedControl VSC;
		ThrottleControl THR;

		List<FieldInfo> ModuleFields = typeof(TCAGui)
			.GetFields(BindingFlags.Instance|BindingFlags.NonPublic)
			.Where(fi => fi.FieldType.IsSubclassOf(typeof(TCAModule))).ToList();
		#endregion

		#region GUI Parameters
		public const int ControlsWidth = 650, ControlsHeight = 100, LineHeight = 35;

		NamedConfig selected_config;
		string config_name = string.Empty;
		readonly DropDownList named_configs = new DropDownList();

		[ConfigOption] 
		KeyCode TCA_Key = KeyCode.Y;
		bool selecting_key;
		bool adv_options;

		public static string StatusMessage;
		public static DateTime StatusEndTime;

		public static Blinker EnabledBlinker = new Blinker(0.5);
		#endregion

		#region ControlWindows
//		AttitudeControlWindow ATC_Window;
//		OrbitalControlWindow OrbitalWindow;
		List<ControlWindow> AllWindows = new List<ControlWindow>();
		#endregion

		#region ControlPanels
		public AttitudePanel AttitudeControls;
		public InOrbitPanel InOrbitControls;
		public OnPlanetPanel OnPlanetControls;
		public NavigationPanel NavigationControls;
		public MacrosPanel MacroControls;
		public SquadPanel SquadControls;
		public TogglesPanel Toggles;
		List<ControlPanel> AllPanels = new List<ControlPanel>();
		List<FieldInfo> AllPanelFields = typeof(TCAGui)
			.GetFields(BindingFlags.Instance|BindingFlags.Public)
			.Where(fi => fi.FieldType.IsSubclassOf(typeof(ControlPanel))).ToList();
		#endregion

		#region Initialization
		public override void LoadConfig()
		{
			base.LoadConfig ();
			update_configs();
		}

		void save_config(ConfigNode node) { SaveConfig(); }

		public override void Awake()
		{
			base.Awake();
			AllWindows = subwindows.Where(sw => sw is ControlWindow).Cast<ControlWindow>().ToList();
			GameEvents.onGameStateSave.Add(save_config);
			GameEvents.onVesselChange.Add(onVesselChange);
			NavigationPanel.OnAwake();
		}

		public override void OnDestroy()
		{
			base.OnDestroy();
			clear_fields();
			TCAToolbarManager.AttachTCA(null);
			GameEvents.onGameStateSave.Remove(save_config);
			GameEvents.onVesselChange.Remove(onVesselChange);
		}

		void onVesselChange(Vessel vsl)
		{
			if(vsl == null || vsl.parts == null) return;
			vessel = vsl;
			StatusMessage = "";
			StatusEndTime = DateTime.MinValue;
			StartCoroutine(init_on_load());
		}


		public override void Show(bool show)
		{
			if(TCA == null || CFG == null) return;
			CFG.GUIVisible = show;
			base.Show(show);
		}

		public static void AttachTCA(ModuleTCA tca) 
		{ 
			if(Instance == null || tca.vessel != Instance.vessel) return;
			Instance.StartCoroutine(Instance.init_on_load()); 
		}

		IEnumerator<YieldInstruction> init_on_load()
		{
			do {
				yield return null;
				if(vessel == null) yield break;
			} while(!vessel.loaded || TimeWarp.CurrentRateIndex == 0 && vessel.packed);	
			init();
		}

		void create_fields()
		{
			AllPanels.Clear();
			foreach(var fi in AllPanelFields)
			{
				var panel = TCA.CreateComponent(fi.FieldType) as ControlPanel;
				if(panel != null) AllPanels.Add(panel);
				fi.SetValue(this, panel);
			}
			ModuleFields.ForEach(fi => fi.SetValue(this, TCA.GetModule(fi.FieldType)));
			AllWindows.ForEach(w => w.Init(TCA));
		}

		void clear_fields()
		{
			TCA = null;
			AllPanels.ForEach(p => p.Reset());
			AllWindows.ForEach(w => w.Reset());
			AllPanelFields.ForEach(fi => fi.SetValue(this, null));
			ModuleFields.ForEach(fi => fi.SetValue(this, null));
			AllPanels.Clear();
		}

		bool init()
		{
			clear_fields();
			TCAToolbarManager.AttachTCA(null);
			TCA = ModuleTCA.AvailableTCA(vessel);
			if(TCA == null) return false;
			if(CFG != null) ShowInstance(CFG.GUIVisible);
			TCAToolbarManager.AttachTCA(TCA);
			create_fields();
			update_configs();
			return true;
		}
		#endregion

		#region Configs Selector
		void update_configs()
		{ 
			var configs = TCAScenario.NamedConfigs.Keys.ToList();
			var first = named_configs.Items.Count == 0;
			configs.Add(string.Empty); named_configs.Items = configs; 
			if(first) named_configs.SelectItem(configs.Count-1);
		}

		void SelectConfig_start() 
		{ 
			named_configs.styleListBox  = Styles.list_box;
			named_configs.styleListItem = Styles.list_item;
			named_configs.windowRect    = WindowPos;
			if(TCAScenario.NamedConfigs.Count > 0)
				named_configs.DrawBlockingSelector(); 
		}

		void SelectConfig()
		{
			if(TCAScenario.NamedConfigs.Count == 0)
				GUILayout.Label("[Nothing Saved]", GUILayout.ExpandWidth(true));
			else
			{
				named_configs.DrawButton();
				var new_config = TCAScenario.GetConfig(named_configs.SelectedIndex);
				if(new_config != selected_config)
				{
					selected_config = new_config;
					config_name = selected_config != null? selected_config.Name : string.Empty;
				}
			}
		}

		void SelectConfig_end()
		{
			if(TCAScenario.NamedConfigs.Count == 0) return;
			named_configs.DrawDropDown();
			named_configs.CloseOnOutsideClick();
		}
		#endregion

		#region Status
		public static void ClearStatus() { TCAGui.StatusMessage = ""; StatusEndTime = DateTime.MinValue; }

		public static void Status(double seconds, string msg, params object[] args)
		{
			TCAGui.StatusMessage = string.Format(msg, args);
			TCAGui.StatusEndTime = seconds > 0? DateTime.Now.AddSeconds(seconds) : DateTime.MinValue;
		}

		public static void Status(string msg, params object[] args) { Status(-1, msg, args); }

		public static void Status(double seconds, string color, string msg, params object[] args)
		{ Status(seconds, string.Format("<color={0}>{1}</color>", color, msg), args); }

		public static void Status(string color, string msg, params object[] args) 
		{ Status(-1, color, msg, args); }
		#endregion

		void DrawStatusMessage()
		{
			#if DEBUG
			DebugInfo();
//			EnginesInfo();
			#endif
			if(!string.IsNullOrEmpty(StatusMessage))
			{ 
				if(GUILayout.Button(new GUIContent(StatusMessage, "Click to dismiss"), 
				                       Styles.boxed_label, GUILayout.ExpandWidth(true)) ||
				      StatusEndTime > DateTime.MinValue && DateTime.Now > StatusEndTime)
					StatusMessage = "";
			}
		}

		void DrawMainWindow(int windowID)
		{
			//help button
			if(GUI.Button(new Rect(WindowPos.width - 23f, 2f, 20f, 18f), 
			              new GUIContent("?", "Help"))) TCAManual.ToggleInstance();
			if(TCA.IsControllable)
			{
				//options button
				if(GUI.Button(new Rect(2f, 2f, 70f, 18f), 
				              new GUIContent("advanced", "Advanced configuration"), 
				              adv_options? Styles.enabled_button : Styles.normal_button)) 
					adv_options = !adv_options;
				GUILayout.BeginVertical();
				GUILayout.BeginHorizontal();
				//tca toggle
				var enabled_style = Styles.inactive_button;
				if(CFG.Enabled) enabled_style = Styles.enabled_button;
				else if(!VSL.LandedOrSplashed) 
				{
					if(EnabledBlinker.On) enabled_style = Styles.danger_button;
					Status(0.1, "red", "<b>TCA is disabled</b>");
				}
				if(GUILayout.Button("Enabled", enabled_style, GUILayout.Width(70)))
				{
					if(SQD == null) TCA.ToggleTCA();
					else SQD.Apply(tca => tca.ToggleTCA());
				}
				#if DEBUG
				if(GUILayout.Button("ReGlobals", Styles.active_button, GUILayout.ExpandWidth(false))) 
				{
					Globals.Load();
					Styles.ConfigureButtons();
					TCA.OnReloadGlobals();
				}
				#endif
				//squad mode switch
				SquadControls.Draw();
				GUILayout.FlexibleSpace();
				StatusString();
				GUILayout.EndHorizontal();
				SelectConfig_start();
				AdvancedOptions();
				AttitudeControls.Draw();
				InOrbitControls.Draw();
				OnPlanetControls.Draw();
				NavigationControls.Draw();
				MacroControls.Draw();
				NavigationControls.WaypointList();
				EnginesControl();
				DrawStatusMessage();
				SelectConfig_end();
				GUILayout.EndVertical();
			}
			else 
			{
				GUILayout.BeginVertical();
				GUILayout.BeginHorizontal();
				GUILayout.FlexibleSpace();
				VSL.Info.Draw();
				StatusString();
				GUILayout.EndHorizontal();
				GUILayout.Label("Vessel is Uncontrollable", Styles.label, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
				DrawStatusMessage();
				GUILayout.EndVertical();
			}
			TooltipsAndDragWindow();
		}

		void StatusString()
		{
			var state = "Disabled";
			var style = Styles.grey;
			if(TCA.IsStateSet(TCAState.Enabled))
			{
				if(TCA.IsStateSet(TCAState.ObstacleAhead))
				{ state = "Obstacle On Course"; style = Styles.red; }
				else if(TCA.IsStateSet(TCAState.GroundCollision))
				{ state = "Ground Collision Possible"; style = Styles.red; }
				else if(TCA.IsStateSet(TCAState.LoosingAltitude))
				{ state = "Loosing Altitude"; style = Styles.red; }
				else if(!VSL.Controls.HaveControlAuthority)
				{ state = "Low Control Authority"; style = Styles.red; }
                else if(TCA.IsStateSet(TCAState.Unoptimized))
				{ state = "Engines Unoptimized"; style = Styles.yellow; }
				else if(TCA.IsStateSet(TCAState.Ascending))
				{ state = "Ascending"; style = Styles.yellow; }
				else if(TCA.IsStateSet(TCAState.VTOLAssist))
				{ state = "VTOL Assist On"; style = Styles.yellow; }
				else if(TCA.IsStateSet(TCAState.StabilizeFlight))
				{ state = "Stabilizing Flight"; style = Styles.yellow; }
				else if(TCA.IsStateSet(TCAState.AltitudeControl))
				{ state = "Altitude Control"; style = Styles.green; }
				else if(TCA.IsStateSet(TCAState.VerticalSpeedControl))
				{ state = "Vertical Speed Control"; style = Styles.green; }
				else if(TCA.State == TCAState.Nominal)
				{ state = "Systems Nominal"; style = Styles.green; }
				else if(TCA.State == TCAState.NoActiveEngines)
				{ state = "No Active Engines"; style = Styles.yellow; }
				else if(TCA.State == TCAState.NoEC)
				{ state = "No Electric Charge"; style = Styles.red; }
				else //this should never happen
				{ state = "Unknown State"; style = Styles.magenta; }
			}
			GUILayout.Label(state, style, GUILayout.ExpandWidth(false));
		}

		void AdvancedOptions()
		{
			if(!adv_options) return;
			GUILayout.BeginVertical(Styles.white);
			GUILayout.Label(Title, Styles.label, GUILayout.ExpandWidth(true));
			GUILayout.BeginHorizontal();
			//change key binding
			if(GUILayout.Button(selecting_key? new GUIContent("Change TCA hotkey: ?", "Choose new TCA hotkey") : 
			                    new GUIContent(string.Format("Change TCA hotkey: {0}", TCA_Key), "Select TCA Hotkey"), 
			                    selecting_key? Styles.enabled_button : Styles.active_button, 
			                    GUILayout.ExpandWidth(true)))
			{ selecting_key = true; Utils.Message("Press a key that will toggle TCA"); }
			Utils.ButtonSwitch("Autosave on landing", ref Globals.Instance.AutosaveBeforeLanding);
			if(Utils.ButtonSwitch("Use Stock Toolbar", ref Globals.Instance.UseStockAppLauncher))
				TCAToolbarManager.Init();
			if(GUILayout.Button("Reload TCA Settings", Styles.active_button, GUILayout.ExpandWidth(true))) 
			{
				Globals.Load();
				Styles.ConfigureButtons();
				TCA.OnReloadGlobals();
			}
			GUILayout.EndHorizontal();
			Toggles.Draw();
			if(THR != null)
			{
				GUILayout.BeginHorizontal();
				CFG.ControlSensitivity = Utils.FloatSlider("Keyboard controls sensitivity", CFG.ControlSensitivity, 0.001f, 0.05f, "P2");
				GUILayout.EndHorizontal();
			}
			ControllerProperties();
			ConfigsGUI();
			GUILayout.EndVertical();
		}

		void ConfigsGUI()
		{
			GUILayout.BeginVertical();
			GUILayout.Label("Manage named configurations", Styles.label, GUILayout.ExpandWidth(true));
			GUILayout.BeginHorizontal();
			GUILayout.Label("Name:", GUILayout.Width(50));
			config_name = GUILayout.TextField(config_name, GUILayout.ExpandWidth(true), GUILayout.MinWidth(50));
			if(TCAScenario.NamedConfigs.ContainsKey(config_name))
			{
				if(GUILayout.Button(new GUIContent("Overwrite", "Overwrite selected configuration with the current one"), 
				                    Styles.danger_button, GUILayout.Width(70)))
					TCAScenario.SaveNamedConfig(config_name, CFG, true);
			}
			else if(GUILayout.Button(new GUIContent("Add", "Save current configuration"), 
			                         Styles.add_button, GUILayout.Width(50)) && config_name != string.Empty) 
			{
				TCAScenario.SaveNamedConfig(config_name, CFG);
				update_configs();
				named_configs.SelectItem(TCAScenario.NamedConfigs.IndexOfKey(config_name));
			}
			SelectConfig();
			if(GUILayout.Button(new GUIContent("Load", "Load selected configuration"),
			                    Styles.active_button, GUILayout.Width(50)) && selected_config != null) 
				CFG.CopyFrom(selected_config);
			if(GUILayout.Button(new GUIContent("Delete", "Delete selected configuration"), 
			                    Styles.danger_button, GUILayout.Width(50)) && selected_config != null)
			{ 
				TCAScenario.NamedConfigs.Remove(selected_config.Name);
				named_configs.SelectItem(named_configs.SelectedIndex-1);
				update_configs();
				selected_config = null;
			}
			GUILayout.EndHorizontal();
			GUILayout.EndVertical();
		}

		void ControllerProperties()
		{
			Utils.ButtonSwitch("Autotune engines' controller parameters", ref CFG.AutoTune, "", GUILayout.ExpandWidth(true));
			if(CFG.AutoTune) return;
			//steering modifiers
			GUILayout.BeginHorizontal();
			CFG.SteeringGain = Utils.FloatSlider("Steering Gain", CFG.SteeringGain, 0, 1, "P1");
			CFG.PitchYawLinked = GUILayout.Toggle(CFG.PitchYawLinked, "Link Pitch&Yaw", GUILayout.ExpandWidth(false));
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			if(CFG.PitchYawLinked && !CFG.AutoTune)
			{
				CFG.SteeringModifier.x = Utils.FloatSlider("Pitch&Yaw", CFG.SteeringModifier.x, 0, 1, "P1");
				CFG.SteeringModifier.z = CFG.SteeringModifier.x;
			}
			else
			{
				CFG.SteeringModifier.x = Utils.FloatSlider("Pitch", CFG.SteeringModifier.x, 0, 1, "P1");
				CFG.SteeringModifier.z = Utils.FloatSlider("Yaw", CFG.SteeringModifier.z, 0, 1, "P1");
			}
			CFG.SteeringModifier.y = Utils.FloatSlider("Roll", CFG.SteeringModifier.y, 0, 1, "P1");
			GUILayout.EndHorizontal();
			//engines
			CFG.Engines.DrawControls("Engines Controller", GLB.ENG.MaxP, GLB.ENG.MaxI);
		}

		void EnginesControl()
		{
			GUILayout.BeginVertical();
			if(CFG.ActiveProfile.NumManual > 0)
			{
				if(GUILayout.Button(CFG.ShowManualLimits? "Hide Manual Engines" : "Show Manual Engines", 
				                    Styles.active_button,
				                    GUILayout.ExpandWidth(true)))
					CFG.ShowManualLimits = !CFG.ShowManualLimits;
				if(CFG.ShowManualLimits) CFG.EnginesProfiles.DrawManual(Utils.ClampH(LineHeight*CFG.ActiveProfile.NumManual, ControlsHeight));
			}
			GUILayout.BeginHorizontal();
			if(GUILayout.Button(CFG.ShowEnginesProfiles? "Hide Engines Profiles" : "Show Engines Profiles", 
			                    Styles.active_button,
			                    GUILayout.ExpandWidth(true)))
				CFG.ShowEnginesProfiles = !CFG.ShowEnginesProfiles;
			if(Utils.ButtonSwitch("Smart Engines", ref CFG.UseSmartEngines, 
			                      "Group engines by thrust direction and automatically use appropriate group for a meneuver", GUILayout.ExpandWidth(false)))
			{ if(CFG.UseSmartEngines && !CFG.SmartEngines) CFG.SmartEngines.XOn(SmartEnginesMode.Best); }
			if(Utils.ButtonSwitch("Rotation", CFG.UseSmartEngines && CFG.SmartEngines[SmartEnginesMode.Closest], 
			                      "Minimize amount of rotation needed for maneuvering", GUILayout.ExpandWidth(false)))
			{ if(CFG.UseSmartEngines) CFG.SmartEngines.XOnIfNot(SmartEnginesMode.Closest); }
			if(Utils.ButtonSwitch("Time", CFG.UseSmartEngines && CFG.SmartEngines[SmartEnginesMode.Fastest], 
			                      "Minimize both rotation and thrusting time", GUILayout.ExpandWidth(false)))
			{ if(CFG.UseSmartEngines) CFG.SmartEngines.XOnIfNot(SmartEnginesMode.Fastest); }
			if(Utils.ButtonSwitch("Efficiency", CFG.UseSmartEngines && CFG.SmartEngines[SmartEnginesMode.Best], 
			                      "Minimize rotation, thrusting time and needed fuel", GUILayout.ExpandWidth(false)))
			{ if(CFG.UseSmartEngines) CFG.SmartEngines.XOnIfNot(SmartEnginesMode.Best); }
			GUILayout.EndHorizontal();
			if(CFG.ShowEnginesProfiles) CFG.EnginesProfiles.Draw(ControlsHeight*2);
			GUILayout.EndVertical();
		}

		#if DEBUG
		public static string DebugMessage;

		static Vector2 eInfoScroll;
		void EnginesInfo()
		{
			GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();
			GUILayout.Label(string.Format("Steering: {0}", VSL.Controls.Steering), GUILayout.ExpandWidth(false));
			GUILayout.Label(string.Format("Angular Accel Error: {0:F3}rad/s2", TCA.ENG.TorqueError), GUILayout.ExpandWidth(false));
			GUILayout.Label(string.Format("Vertical Speed Factor: {0:P1}", VSL.OnPlanetParams.VSF), GUILayout.ExpandWidth(false));
			GUILayout.EndHorizontal();
			eInfoScroll = GUILayout.BeginScrollView(eInfoScroll, GUILayout.Height(ControlsHeight*4));
			GUILayout.BeginVertical();
			foreach(var e in VSL.Engines.Active)
			{
				if(!e.Valid(VSL)) continue;
				GUILayout.BeginHorizontal();
				GUILayout.Label(e.name + "\n" +
				                string.Format(
					                "Torque: {0}\n" +
					                "Attitude Modifier: {1:P1}\n" +
					                "Thrust Limit:      {2:F1}%",
					                e.currentTorque,
					                e.limit, e.thrustLimit*100));
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		void DebugInfo()
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(string.Format("vV: {0:0.0}m/s", VSL.VerticalSpeed.Absolute), GUILayout.Width(100));
			GUILayout.Label(string.Format("A: {0:0.0}m/s2", VSL.VerticalSpeed.Derivative), GUILayout.Width(80));
			GUILayout.Label(string.Format("ApA: {0:0.0}m", VSL.orbit.ApA), GUILayout.Width(120));
			GUILayout.Label(string.Format("hV: {0:0.0}m/s", VSL.HorizontalSpeed.Absolute), GUILayout.Width(100));
			GUILayout.Label(string.Format("Rho: {0:0.000}ASL", VSL.Body.atmosphere? VSL.vessel.atmDensity/VSL.Body.atmDensityASL : 0), GUILayout.Width(100));
			GUILayout.Label(string.Format("aV2: {0:0.0E0}", VSL.vessel.angularVelocity.sqrMagnitude), GUILayout.Width(100));
			GUILayout.Label(string.Format("inc: {0:0.000}", VSL.orbit.inclination), GUILayout.Width(100));
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			GUILayout.Label(string.Format("VSP: {0:0.0m/s}", CFG.VerticalCutoff), GUILayout.Width(100));
			GUILayout.Label(string.Format("TWR: {0:0.0}", VSL.OnPlanetParams.DTWR), GUILayout.Width(80));
			if(VSL.Altitude.Ahead.Equals(float.MinValue)) GUILayout.Label("Obst: N/A", GUILayout.Width(120));
			else GUILayout.Label(string.Format("Obst: {0:0.0}m", VSL.Altitude.Ahead), GUILayout.Width(120));
			GUILayout.Label(string.Format("Orb: {0:0.0}m/s", Math.Sqrt(VSL.Physics.StG*VSL.Physics.Radial.magnitude)), GUILayout.Width(100));
			GUILayout.Label(string.Format("dP: {0:0.000}kPa", VSL.vessel.dynamicPressurekPa), GUILayout.Width(100));
			GUILayout.Label(string.Format("Thr: {0:P1}", VSL.vessel.ctrlState.mainThrottle), GUILayout.Width(100));
			GUILayout.Label(string.Format("ecc: {0:0.000}", VSL.orbit.eccentricity), GUILayout.Width(100));
			GUILayout.EndHorizontal();
			if(!string.IsNullOrEmpty(DebugMessage))
				GUILayout.Label(DebugMessage, Styles.boxed_label, GUILayout.ExpandWidth(true));
		}
		#endif

		protected override bool can_draw()
		{ return TCA != null && CFG.GUIVisible && AllPanels.Count > 0; }


		static Rect debug_rect = new Rect(Screen.width*0.75f, 0, 250, 25).clampToScreen();
		protected override void draw_gui()
		{
			Utils.LockIfMouseOver(LockName, WindowPos, !MapView.MapIsEnabled);
			WindowPos = 
				GUILayout.Window(TCA.GetInstanceID(), 
				                 WindowPos, 
				                 DrawMainWindow, 
				                 VSL.vessel.vesselName,
				                 GUILayout.Width(ControlsWidth),
				                 GUILayout.Height(ControlsHeight)).clampToScreen();
			InOrbitControls.OrbitEditorWindow();
			NavigationControls.WaypointEditorWindow();
			if(Event.current.type == EventType.Repaint)
				NavigationControls.WaypointOverlay();
			AllWindows.ForEach(w => w.Draw());
			#if DEBUG
			GUI.Label(debug_rect, 
			          string.Format("[{0}] {1:HH:mm:ss.fff}", 
			                        TCA != null && TCA.vessel != null? vessel.situation.ToString() : "", 
			                        DateTime.Now), 
			          Styles.boxed_label);
			#endif
		}

		public void Update()
		{
			if(TCA == null) return;
			if(!TCA.Available && !init()) return;
			if(!TCA.IsControllable) return;
			AllPanels.ForEach(p => p.Update());
			if(selecting_key)
			{ 
				var e = Event.current;
				if(e.isKey)
				{
					if(e.keyCode != KeyCode.Escape)
					{
						//try to get the keycode if the Unity provided us only with the character
						if(e.keyCode == KeyCode.None && e.character >= 'a' && e.character <= 'z')
						{
							var ec = new string(e.character, 1).ToUpper();
							try { e.keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), ec); }
							catch(Exception ex) { Utils.Log("TCA GUI: exception caught while trying to set hotkey:\n{}", ex); }
						}
						if(e.keyCode == KeyCode.None) 
							Utils.Message("Unable to convert '{0}' to keycode.\nPlease, try an alphabet character.", e.character);
						else TCA_Key = e.keyCode;
						Utils.Log("TCA: new key slected: {}", TCA_Key);
					}
					selecting_key = false;
				}
			}
			else if(!FlightDriver.Pause)
			{
				if(Input.GetKeyDown(TCA_Key)) TCA.ToggleTCA();
				if(CFG.Enabled)
				{
					if(CFG.BlockThrottle && THR != null)
					{
						if(CFG.VF[VFlight.AltitudeControl]) 
						{ if(ALT != null) ALT.ProcessKeys(); }
						else if(VSC != null) VSC.ProcessKeys();
						if(WRP != null) WRP.ProcessKeys();
					}
				}
			}
		}
	}
}
