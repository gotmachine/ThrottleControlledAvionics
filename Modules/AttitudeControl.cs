//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using UnityEngine;
using AT_Utils;

namespace ThrottleControlledAvionics
{
	public abstract class AttitudeControlBase : ThrustDirectionControl
	{
		public new class Config : ModuleConfig
		{
			[Persistent] public PIDv_Controller2 PID = new PIDv_Controller2(
				Vector3.one*10f, Vector3.one*0.02f, Vector3.one*0.5f, -Vector3.one, Vector3.one
			);

            //new 2xPID tuning parameters
            public class PIDCascadeConfig : ConfigNodeObject
            {
                [Persistent] public float avI_Scale = 0.4f;
            }

            public class CascadeConfigMixed : CascadeConfigFast
            {
                [Persistent] public float avP_A = 100f;
                [Persistent] public float avP_B = 0.04f;
                [Persistent] public float avP_C = -100f;
                [Persistent] public float avP_D = 0.7f;

                [Persistent] public float avD_A = 0.65f;
                [Persistent] public float avD_B = -0.01f;
                [Persistent] public float avD_C = 0.5f;
                [Persistent] public float avD_D = 0.7f;
            }

            public class CascadeConfigFast : PIDCascadeConfig
            {
                [Persistent] public float atP_ErrThreshold = 0.7f;
                [Persistent] public float atP_ErrCurve = 0.5f;

                [Persistent] public float atP_LowAA_Scale = 1.2f;
                [Persistent] public float atP_LowAA_Curve = 0.3f;
                [Persistent] public float atD_LowAA_Scale = 1;
                [Persistent] public float atD_LowAA_Curve = 0.5f;

                [Persistent] public float atP_HighAA_Scale = 0.8f;
                [Persistent] public float atP_HighAA_Curve = 0.3f;
                [Persistent] public float atP_HighAA_Max = 3.95f;
                [Persistent] public float atD_HighAA_Scale = 1;
                [Persistent] public float atD_HighAA_Curve = 2;

                [Persistent] public float atI_Scale = 0.01f;
                [Persistent] public float atI_AV_Scale = 10;
                [Persistent] public float atI_ErrThreshold = 0.9f;
                [Persistent] public float atI_ErrCurve = 2;

                [Persistent] public float avP_MaxAA_Intersect = 9;
                [Persistent] public float avP_MaxAA_Inclination = 0.7f;
                [Persistent] public float avP_MaxAA_Curve = 0.8f;
                [Persistent] public float avP_Min = 0.2f;
            }

            public class CascadeConfigSlow : PIDCascadeConfig
            {
                [Persistent] public float avP_HighAA_Scale = 5;
                [Persistent] public float avD_HighAA_Intersect = 10;
                [Persistent] public float avD_HighAA_Inclination = 2;
                [Persistent] public float avD_HighAA_Max = 2;

                [Persistent] public float avP_LowAA_Scale = 8;
                [Persistent] public float avD_LowAA_Intersect = 25;
                [Persistent] public float avD_LowAA_Inclination = 10;

                [Persistent] public float SlowTorqueF = 0.0f;
            }

            [Persistent] public CascadeConfigFast  FastConfig = new CascadeConfigFast();
            [Persistent] public CascadeConfigMixed MixedConfig = new CascadeConfigMixed();
            [Persistent] public CascadeConfigSlow  SlowConfig = new CascadeConfigSlow();

            [Persistent] public float atPID_Clamp = 10*Mathf.PI;
            [Persistent] public float avPID_Clamp = 1000;
            [Persistent] public float AxisCorrection = 2f;

            [Persistent] public float FastThreshold = 0.7f; //% of the AA is instant
            [Persistent] public float MixedThreshold = 0.3f; //% of the AA is instant
            [Persistent] public float SlowThreshold = 0.005f; //% of the AA is instant

            //attitude error and other stats
			[Persistent] public float AngleThreshold         = 60f;
			[Persistent] public float MaxAttitudeError       = 10f;  //deg
			[Persistent] public float AttitudeErrorThreshold = 3f;   //deg
			[Persistent] public float MaxTimeToAlignment     = 15f;  //s
			[Persistent] public float DragResistanceF        = 10f;
		}
		protected static Config ATCB { get { return Globals.Instance.ATCB; } }

		public struct Rotation 
		{ 
			public Vector3 current, needed; 
			public Rotation(Vector3 current, Vector3 needed)
			{ this.current = current; this.needed = needed; }
			public static Rotation Local(Vector3 current, Vector3 needed, VesselWrapper VSL)
			{ return new Rotation(VSL.LocalDir(current), VSL.LocalDir(needed)); }

			public override string ToString()
			{ return Utils.Format("[Rotation]: current {}, needed {}", current, needed); }
		}

		protected AttitudeControlBase(ModuleTCA tca) : base(tca) {}

		protected Vector3 steering;
		protected Vector3 angle_error;

        protected Vector3 rotation_axis;
        protected readonly PIDf_Controller2 at_pid_pitch = new PIDf_Controller2();
        protected readonly PIDf_Controller2 at_pid_roll = new PIDf_Controller2();
        protected readonly PIDf_Controller2 at_pid_yaw = new PIDf_Controller2();

        protected readonly PIDf_Controller av_pid_pitch = new PIDf_Controller();
        protected readonly PIDf_Controller av_pid_roll = new PIDf_Controller();
        protected readonly PIDf_Controller av_pid_yaw = new PIDf_Controller();

		protected readonly Timer AuthorityTimer = new Timer();
		protected readonly DifferentialF ErrorDif = new DifferentialF();

		protected Vector3 AA 
		{ 
            get 
            { 
                return VSL.Torque.Slow? 
                    VSL.Torque.Instant.AA + VSL.Torque.SlowMaxPossible.AA*Mathf.Min(VSL.vessel.ctrlState.mainThrottle, VSL.OnPlanetParams.GeeVSF) :
                    VSL.Torque.MaxCurrent.AA;
            } 
        }

		public override void Init() 
		{ 
			base.Init();
            at_pid_pitch.setClamp(0, ATCB.atPID_Clamp);
            at_pid_roll.setClamp(0, ATCB.atPID_Clamp);
            at_pid_yaw.setClamp(0, ATCB.atPID_Clamp);

            av_pid_pitch.setClamp(ATCB.avPID_Clamp);
            av_pid_roll.setClamp(ATCB.avPID_Clamp);
            av_pid_yaw.setClamp(ATCB.avPID_Clamp);
			reset();
		}

		protected override void reset()
		{
			base.reset();
            at_pid_pitch.Reset();
            at_pid_roll.Reset();
            at_pid_yaw.Reset();
            av_pid_pitch.Reset();
            av_pid_roll.Reset();
            av_pid_yaw.Reset();
			VSL.Controls.HaveControlAuthority = true;
			VSL.Controls.SetAttitudeError(180);
            rotation_axis = Vector3.zero;
		}

		protected static Vector3 rotation2steering(Quaternion rotation)
		{
			var euler = rotation.eulerAngles;
			return new Vector3(Utils.CenterAngle(euler.x)*Mathf.Deg2Rad,
			                   Utils.CenterAngle(euler.y)*Mathf.Deg2Rad,
			                   Utils.CenterAngle(euler.z)*Mathf.Deg2Rad);
		}

		protected Vector3 H(Vector3 wDir) { return Vector3.ProjectOnPlane(wDir, VSL.Physics.Up).normalized; }

		protected Quaternion world2local_rotation(Quaternion world_rotation)
		{ return VSL.refT.rotation.Inverse() * world_rotation * VSL.refT.rotation; }

		protected void update_angular_error(Quaternion direct_rotation)
		{
			angle_error = direct_rotation.eulerAngles;
			angle_error = new Vector3(
				Mathf.Abs(Utils.CenterAngle(angle_error.x)/180),
				Mathf.Abs(Utils.CenterAngle(angle_error.y)/180),
				Mathf.Abs(Utils.CenterAngle(angle_error.z)/180));	
		}

        Vector3 MaxComponentV(Vector3 v, float threshold)
        {
            threshold += 1;
            int maxI = 0;
            float maxC = Math.Abs(v.x);
            for(int i = 1; i < 3; i++)
            {
                var c = Math.Abs(v[i]);
                if(maxC <= 0 || c/maxC > threshold)
                {
                    maxC = c;
                    maxI = i;
                }
            }
            var ret = new Vector3();
            ret[maxI] = maxC;
            return ret;
        }

		protected void compute_rotation(Vector3 current, Vector3 needed)
		{
			var cur_inv = current.IsInvalid() || current.IsZero();
			var ned_inv = needed.IsInvalid() || needed.IsZero();
			if(cur_inv || ned_inv)
			{
				Log("compute_steering: Invalid argumetns:\ncurrent {}\nneeded {}\ncurrent thrust {}", 
				    current, needed, VSL.Engines.CurrentDefThrustDir);
				steering = Vector3.zero;
				return;
			}
            needed.Normalize();
            current.Normalize();
            var current_maxI = current.MaxI();
			var direct_rotation = Quaternion.FromToRotation(needed, current);
			update_angular_error(direct_rotation);
			VSL.Controls.SetAttitudeError(Vector3.Angle(needed, current));
            rotation_axis = VSL.Controls.AttitudeError < 175f? 
                Vector3.Cross(current, needed) : 
                -MaxComponentV(VSL.Torque.MaxCurrent.AA.Exclude(current_maxI), 0.01f);
            if(rotation_axis.IsZero() || rotation_axis.IsInvalid())
                rotation_axis = -VSL.vessel.angularVelocity;
            rotation_axis.Normalize();
		}

		protected void compute_rotation(Quaternion rotation)
		{ compute_rotation(Vector3.up, rotation*Vector3.up); }

		protected void compute_rotation(Rotation rotation)
		{ compute_rotation(rotation.current, rotation.needed); }

		protected virtual void correct_steering() {}

        #region PID Tuning
        void tune_at_pid_fast(Config.CascadeConfigFast cfg, PIDf_Controller2 pid, float AV, float AM, float MaxAA, float iMaxAA, float iErr)
        {
            var atP_iErr = Mathf.Pow(Utils.ClampL(iErr - cfg.atP_ErrThreshold, 0), cfg.atP_ErrCurve);
            if(MaxAA >= 1)
            {
                pid.P = Utils.ClampH(1
                                     + cfg.atP_HighAA_Scale * Mathf.Pow(MaxAA, cfg.atP_HighAA_Curve)
                                     + atP_iErr, cfg.atP_HighAA_Max);
                pid.D = cfg.atD_HighAA_Scale * Mathf.Pow(iMaxAA, cfg.atD_HighAA_Curve) * Utils.ClampH(iErr + Mathf.Abs(AM), 1.2f);
            }
            else
            {
                pid.P = (1
                         + cfg.atP_LowAA_Scale * Mathf.Pow(MaxAA, cfg.atP_LowAA_Curve)
                         + atP_iErr);
                pid.D = cfg.atD_LowAA_Scale * Mathf.Pow(iMaxAA, cfg.atD_LowAA_Curve) * Utils.ClampH(iErr + Mathf.Abs(AM), 1.2f);
            }
            var atI_iErr = Utils.ClampL(iErr-ATCB.FastConfig.atI_ErrThreshold, 0);
            if(atI_iErr <= 0 || AV < 0)
            {
                pid.I = 0;
                pid.IntegralError = 0;
            }
            else 
            {
                atI_iErr = Mathf.Pow(atI_iErr, cfg.atI_ErrCurve);
                pid.I = cfg.atI_Scale * MaxAA * atI_iErr / (1+Utils.ClampL(AV, 0)*cfg.atI_AV_Scale*atI_iErr);
            }
        }

        void tune_at_pid_slow(PIDf_Controller2 pid)
        {
            pid.P = 1;
            pid.I = 0;
            pid.D = 0;
        }

        void tune_av_pid_fast(Config.CascadeConfigFast cfg, PIDf_Controller pid, float MaxAA)
        {
            pid.P = Utils.ClampL(cfg.avP_MaxAA_Intersect - 
                                 cfg.avP_MaxAA_Inclination * Mathf.Pow(MaxAA, cfg.avP_MaxAA_Curve),
                                 cfg.avP_Min);
            pid.I = cfg.avI_Scale * pid.P;
            pid.D = 0;
        }

        void tune_av_pid_mixed(PIDf_Controller pid, float AM, float MaxAA, float InstantTorqueRatio)
        {
            pid.P = ((ATCB.MixedConfig.avP_A / (Mathf.Pow(InstantTorqueRatio, ATCB.MixedConfig.avP_D) + ATCB.MixedConfig.avP_B) +
                      ATCB.MixedConfig.avP_C) / Utils.ClampL(Mathf.Abs(AM), 1) / MaxAA);
            pid.D = ((ATCB.MixedConfig.avD_A / (Mathf.Pow(InstantTorqueRatio, ATCB.MixedConfig.avD_D) + ATCB.MixedConfig.avD_B) +
                      ATCB.MixedConfig.avD_C) / MaxAA);
            pid.I = ATCB.MixedConfig.avI_Scale*Utils.ClampH(MaxAA, 1);
        }

        void tune_av_pid_slow(PIDf_Controller pid, float MaxAA, float EnginesResponseTime, float SpecificTorque)
        {
            var slowF = (1 + ATCB.SlowConfig.SlowTorqueF*EnginesResponseTime*SpecificTorque);
            TCAGui.DebugMessage += Utils.Format("SlowF: {}\n", slowF);//debug
            if(MaxAA >= 1)
            {
                pid.P = ATCB.SlowConfig.avP_HighAA_Scale/slowF;
                pid.D = Utils.ClampL(ATCB.SlowConfig.avD_HighAA_Intersect - ATCB.SlowConfig.avD_HighAA_Inclination * MaxAA, ATCB.SlowConfig.avD_HighAA_Max);
            }
            else
            {
                pid.P = ATCB.SlowConfig.avP_LowAA_Scale/slowF;
                pid.D = ATCB.SlowConfig.avD_LowAA_Intersect - ATCB.SlowConfig.avD_LowAA_Inclination * MaxAA;
            }
            pid.I = 0;
        }

        void tune_pids(PIDf_Controller2 at_pid, PIDf_Controller av_pid, 
                       float AV, float AM, float MaxAA, float iMaxAA, float iErr,
                       float InstantTorqueRatio, float EnginesResponseTime, float SpecificTorque)
        {
            if(InstantTorqueRatio > ATCB.FastThreshold)
            {
                tune_at_pid_fast(ATCB.FastConfig, at_pid, AV, AM, MaxAA, iMaxAA, iErr);
                tune_av_pid_fast(ATCB.FastConfig, av_pid, MaxAA);
            }
            else if(InstantTorqueRatio > ATCB.MixedThreshold)
            {
                tune_at_pid_fast(ATCB.MixedConfig, at_pid, AV, AM, MaxAA, iMaxAA, iErr);
                tune_av_pid_fast(ATCB.MixedConfig, av_pid, MaxAA);
            }
            else if(InstantTorqueRatio > ATCB.SlowThreshold)
            {
                tune_at_pid_slow(at_pid);
                tune_av_pid_mixed(av_pid, AM, MaxAA, InstantTorqueRatio);
            }
            else
            {
                tune_at_pid_slow(at_pid);
                tune_av_pid_slow(av_pid, MaxAA, EnginesResponseTime, SpecificTorque);
            }
        }

        Vector3 compute_av_error(Vector3 AV, float Err)
        {
            var avErr = AV-Vector3.Scale(rotation_axis,
                                         new Vector3(at_pid_pitch.Action,
                                                     at_pid_roll.Action,
                                                     at_pid_yaw.Action));
            var axis_correction = Vector3.ProjectOnPlane(AV, rotation_axis);
            if(axis_correction.IsInvalid() || axis_correction.IsZero())
            {
                TCAGui.DebugMessage += Utils.Format("Axis Correction: 0\naxis: {}\navErr: {}\n", axis_correction, avErr);//debug
                return avErr;
            }
            var ACf = axis_correction.sqrMagnitude/AV.sqrMagnitude*Utils.ClampH(Err*ATCB.AxisCorrection, 1);
            TCAGui.DebugMessage += Utils.Format("Axis Correction: {}\naxis: {}\navErr: {}\n", ACf, axis_correction, avErr);//debug
            return Vector3.Lerp(avErr, axis_correction, ACf);
        }

        protected void compute_steering()
        {
            TCAGui.DebugMessage = "";
            VSL.Controls.GimbalLimit = UseGimball && VSL.vessel.ctrlState.mainThrottle > 0? VSL.OnPlanetParams.TWRf*100 : 0;
            if(rotation_axis.IsZero()) return;
            var AV = CFG.BR? 
                Vector3.ProjectOnPlane(VSL.vessel.angularVelocity, VSL.LocalDir(VSL.Engines.refT_thrust_axis)) : 
                VSL.vessel.angularVelocity;
            var AM = VSL.vessel.angularMomentum;
            var abs_rotation_axis = rotation_axis.AbsComponents();
            var Err = VSL.Controls.AttitudeError/180;
            var ErrV = Err*abs_rotation_axis;
            var iErr = Vector3.one-ErrV;
            var MaxAA = AA;
            var iMaxAA = MaxAA.Inverse(0);
            TCAGui.DebugMessage += Utils.Format("\nMoI: {}\nInstAA: {}\nMaxAA: {}\nAV {}\nAM {}\n", 
                                                VSL.Physics.MoI, 
                                                VSL.Torque.Instant.AA, 
                                                MaxAA, 
                                                AV, 
                                                VSL.vessel.angularMomentum);//debug
            if(VSL.Torque.Slow)
            {
                TCAGui.DebugMessage += Utils.Format("Slow: ITR {}\n", VSL.Torque.Instant.AA.ScaleChain(MaxAA.Inverse(0)));
                tune_pids(at_pid_pitch, av_pid_pitch, 
                          AV.x, AM.x, MaxAA.x, iMaxAA.x, iErr.x,
                          MaxAA.x > 0? VSL.Torque.Instant.AA.x/MaxAA.x : 1, 
                          VSL.Torque.EnginesResponseTime.x, 
                          VSL.Torque.MaxPossible.SpecificTorque.x);
                tune_pids(at_pid_roll, av_pid_roll, 
                          AV.y, AM.y, MaxAA.y, iMaxAA.y, iErr.y,
                          MaxAA.y > 0? VSL.Torque.Instant.AA.y/MaxAA.y : 1, 
                          VSL.Torque.EnginesResponseTime.y, 
                          VSL.Torque.MaxPossible.SpecificTorque.y);
                tune_pids(at_pid_yaw, av_pid_yaw, 
                          AV.z, AM.z, MaxAA.z, iMaxAA.z, iErr.z,
                          MaxAA.z > 0? VSL.Torque.Instant.AA.z/MaxAA.z : 1, 
                          VSL.Torque.EnginesResponseTime.z, 
                          VSL.Torque.MaxPossible.SpecificTorque.z);
            }
            else
            {
                TCAGui.DebugMessage += Utils.Format("Fast.\n");
                tune_at_pid_fast(ATCB.FastConfig, at_pid_pitch, AV.x, AM.x, MaxAA.x, iMaxAA.x, iErr.x);
                tune_at_pid_fast(ATCB.FastConfig, at_pid_roll, AV.y, AM.y, MaxAA.y, iMaxAA.y, iErr.y);
                tune_at_pid_fast(ATCB.FastConfig, at_pid_yaw, AV.z, AM.z, MaxAA.z, iMaxAA.z, iErr.z);
                tune_av_pid_fast(ATCB.FastConfig, av_pid_pitch, MaxAA.x);
                tune_av_pid_fast(ATCB.FastConfig, av_pid_roll, MaxAA.y);
                tune_av_pid_fast(ATCB.FastConfig, av_pid_yaw, MaxAA.z);
            }
            at_pid_pitch.Update(ErrV.x*Mathf.PI, -AV.x*rotation_axis.x);
            at_pid_roll.Update(ErrV.y*Mathf.PI, -AV.y*rotation_axis.y);
            at_pid_yaw.Update(ErrV.z*Mathf.PI, -AV.z*rotation_axis.z);
            var avErr = compute_av_error(AV, Err);
            av_pid_pitch.Update(avErr.x);
            av_pid_roll.Update(avErr.y);
            av_pid_yaw.Update(avErr.z);
            steering = new Vector3(av_pid_pitch.Action, av_pid_roll.Action, av_pid_yaw.Action);
            var maxPRY = Mathf.Abs(steering.MaxComponentF());
            if(maxPRY > 1) steering /= maxPRY;
            correct_steering();
            TCAGui.DebugMessage += Utils.Format("\natPID.P: {}\n" +
                                                "avPID.P: {}\n" +
                                                "atPID.R: {}\n" +
                                                "avPID.R: {}\n" +
                                                "atPID.Y: {}\n" +
                                                "avPID.Y: {}\n\n" +
                                                "steering: {}",
                                                at_pid_pitch, av_pid_pitch, at_pid_roll, av_pid_roll, at_pid_yaw, av_pid_yaw, 
                                                Utils.formatComponents(steering));//debug
        }
        #endregion

		protected void set_authority_flag()
		{
			ErrorDif.Update(VSL.Controls.AttitudeError);
			if(ErrorDif.MaxOrder < 1) return;
			var max_alignment_time = VSL.Info.Countdown > 0? VSL.Info.Countdown : ATCB.MaxTimeToAlignment;
			var omega = Mathf.Abs(ErrorDif[1]/TimeWarp.fixedDeltaTime);
			var turn_time = VSL.Controls.MinAlignmentTime-omega/VSL.Torque.MaxCurrent.AA_rad/Mathf.Rad2Deg;
			if(VSL.Controls.HaveControlAuthority && 
			   VSL.Controls.AttitudeError > ATCB.MaxAttitudeError && 
			   (ErrorDif[1] >= 0 || turn_time > max_alignment_time))
				VSL.Controls.HaveControlAuthority = !AuthorityTimer.TimePassed;
			else if(!VSL.Controls.HaveControlAuthority && 
			        (VSL.Controls.AttitudeError < ATCB.AttitudeErrorThreshold || 
			         VSL.Controls.AttitudeError < ATCB.MaxAttitudeError*2 && ErrorDif[1] < 0 && 
			         turn_time < max_alignment_time))
				VSL.Controls.HaveControlAuthority = AuthorityTimer.TimePassed;
			else AuthorityTimer.Reset();
		}

        #if DEBUG
        public static bool UseGimball = true;
        #endif
	}

	[CareerPart]
	[RequireModules(typeof(SASBlocker))]
	[OptionalModules(typeof(TimeWarpControl))]
	public class AttitudeControl : AttitudeControlBase
	{
		public new class Config : ModuleConfig
		{
			[Persistent] public float KillRotThreshold = 1e-5f;
		}
		static Config ATC { get { return Globals.Instance.ATC; } }

		readonly MinimumF momentum_min = new MinimumF();
		Transform refT;
		Quaternion locked_attitude;
		bool attitude_locked;

		BearingControl BRC;
		Vector3 lthrust, needed_lthrust;

		public AttitudeControl(ModuleTCA tca) : base(tca) {}

		public override void Init() 
		{ 
			base.Init();
			CFG.AT.SetSingleCallback(Enable);
		}

		protected override void UpdateState() 
		{ 
			base.UpdateState();
			IsActive &= CFG.AT; 
		}

		public void Enable(Multiplexer.Command cmd)
		{
			reset();
			switch(cmd)
			{
			case Multiplexer.Command.Resume:
				RegisterTo<SASBlocker>();
				break;

			case Multiplexer.Command.On:
				VSL.UpdateOnPlanetStats();
				goto case Multiplexer.Command.Resume;

			case Multiplexer.Command.Off:
				UnregisterFrom<SASBlocker>();
				break;
			}
		}

		public Rotation CustomRotation { get; private set; }

		public void SetCustomRotation(Vector3 current, Vector3 needed)
		{ CustomRotation = new Rotation(current, needed); }

		public void SetCustomRotationW(Vector3 current, Vector3 needed)
		{ CustomRotation = Rotation.Local(current, needed, VSL); }

		public void SetThrustDirW(Vector3 needed)
		{ CustomRotation = Rotation.Local(VSL.Engines.CurrentDefThrustDir, needed, VSL); }

		public void ResetCustomRotation() { CustomRotation = default(Rotation); }

		protected override void reset()
		{
			base.reset();
			refT = null;
			momentum_min.Reset();
			attitude_locked = false;
			needed_lthrust = Vector3.zero;
			lthrust = Vector3.zero;
		}

		public void UpdateCues()
		{
			switch(CFG.AT.state)
			{
			case Attitude.Normal:
				needed_lthrust = -VSL.LocalDir(VSL.orbit.h.xzy);
				break;
			case Attitude.AntiNormal:
				needed_lthrust = VSL.LocalDir(VSL.orbit.h.xzy);
				break;
			case Attitude.Radial:
				needed_lthrust = VSL.LocalDir(Vector3d.Cross(VSL.vessel.obt_velocity.normalized, VSL.orbit.h.xzy.normalized));
				break;
			case Attitude.AntiRadial:
				needed_lthrust = -VSL.LocalDir(Vector3d.Cross(VSL.vessel.obt_velocity.normalized, VSL.orbit.h.xzy.normalized));
				break;
			case Attitude.Target:
			case Attitude.AntiTarget:
			case Attitude.TargetCorrected:
				if(!VSL.HasTarget) 
				{ 
					Message("No target");
					CFG.AT.On(Attitude.KillRotation);
					break;
				}
				var dpos = VSL.vessel.transform.position-VSL.Target.GetTransform().position;
				if(CFG.AT.state == Attitude.TargetCorrected)
				{
					var dvel = VSL.vessel.GetObtVelocity()-VSL.Target.GetObtVelocity();
					needed_lthrust = VSL.LocalDir((dpos.normalized+Vector3.ProjectOnPlane(dvel, dpos).ClampMagnitudeH(1)).normalized);
				}
				else
				{
					needed_lthrust = VSL.LocalDir(dpos.normalized);
					if(CFG.AT.state == Attitude.AntiTarget) needed_lthrust *= -1;
				}
				break;
			}
		}

		protected void compute_rotation()
		{
			Vector3 v;
			momentum_min.Update(VSL.vessel.angularMomentum.sqrMagnitude);
			lthrust = VSL.LocalDir(VSL.Engines.CurrentDefThrustDir);
			steering = Vector3.zero;
			switch(CFG.AT.state)
			{
			case Attitude.Custom:
				if(CustomRotation.Equals(default(Rotation)))
					goto case Attitude.KillRotation;
				lthrust = CustomRotation.current;
				needed_lthrust = CustomRotation.needed;
				break;
			case Attitude.HoldAttitude:
				if(refT != VSL.refT || !attitude_locked)
				{
					refT = VSL.refT;
					locked_attitude = refT.rotation;
					attitude_locked = true;
				}
				if(refT != null)
				{
					lthrust = Vector3.up;
					needed_lthrust = refT.rotation.Inverse()*locked_attitude*lthrust;
				}
				break;
			case Attitude.KillRotation:
				if(refT != VSL.refT || momentum_min.True)
				{
					refT = VSL.refT;
					locked_attitude = refT.rotation;
				}
				if(refT != null)
				{
					lthrust = Vector3.up;
					needed_lthrust = refT.rotation.Inverse()*locked_attitude*lthrust;
				}
				break;
			case Attitude.Prograde:
			case Attitude.Retrograde:
				v = VSL.InOrbit? VSL.vessel.obt_velocity : VSL.vessel.srf_velocity;
				if(v.magnitude < GLB.THR.MinDeltaV) { CFG.AT.On(Attitude.KillRotation); break; }
				if(CFG.AT.state == Attitude.Prograde) v *= -1;
				needed_lthrust = VSL.LocalDir(v.normalized);
				VSL.Engines.RequestNearestClusterActivation(needed_lthrust);
				break;
			case Attitude.RelVel:
			case Attitude.AntiRelVel:
				if(!VSL.HasTarget) 
				{ 
					Message("No target");
					CFG.AT.On(Attitude.KillRotation);
					break;
				}
				v = VSL.InOrbit? 
					VSL.Target.GetObtVelocity()-VSL.vessel.obt_velocity : 
					VSL.Target.GetSrfVelocity()-VSL.vessel.srf_velocity;
				if(v.magnitude < GLB.THR.MinDeltaV) { CFG.AT.On(Attitude.KillRotation); break; }
				if(CFG.AT.state == Attitude.AntiRelVel) v *= -1;
				needed_lthrust = VSL.LocalDir(v.normalized);
				VSL.Engines.RequestClusterActivationForManeuver(v);
				break;
			case Attitude.ManeuverNode:
				var solver = VSL.vessel.patchedConicSolver;
				if(solver == null || solver.maneuverNodes.Count == 0)
				{ 
					Message("No maneuver node");
					CFG.AT.On(Attitude.KillRotation); 
					break; 
				}
				v = -solver.maneuverNodes[0].GetBurnVector(VSL.orbit);
				needed_lthrust = VSL.LocalDir(v.normalized);
				VSL.Engines.RequestClusterActivationForManeuver(v);
				break;
			case Attitude.Normal:
			case Attitude.AntiNormal:
			case Attitude.Radial:
			case Attitude.AntiRadial:
			case Attitude.Target:
			case Attitude.AntiTarget:
				VSL.Engines.RequestNearestClusterActivation(needed_lthrust);
				break;
			}
            #if DEBUG
            if(FollowMouse)
                needed_lthrust = VSL.LocalDir(FlightCamera.fetch.mainCamera.ScreenPointToRay(Input.mousePosition).direction);
            #endif
			compute_rotation(lthrust.normalized, needed_lthrust.normalized);
			ResetCustomRotation();
		}

		protected override void correct_steering()
		{
			if(BRC != null && BRC.IsActive)
				steering = Vector3.ProjectOnPlane(steering, lthrust);
		}


		protected override void OnAutopilotUpdate(FlightCtrlState s)
		{
			//need to check all the prerequisites, because the callback is called asynchroniously
			if(!(CFG.Enabled && CFG.AT && VSL.refT != null && VSL.orbit != null)) return;
			if(VSL.AutopilotDisabled) { reset(); return; }
			compute_rotation();
            compute_steering();
			set_authority_flag();
			VSL.Controls.AddSteering(steering);
		}

		#if DEBUG
        public bool FollowMouse;

//        ThrottleControl THR;
//
//        OscillationDetector3D OD = new OscillationDetector3D(3, 30, 100, 200, 1);
//        Timer test_timer = new Timer(1);
//        static readonly Vector3[] axes = {Vector3.up, Vector3.right};
//        Vector3 needed_thrust;
//        int direction = 1, axis = 0;
//        float throttle = 0.1f, dThrottle = 0.1f;
//        float curAAf = 0.1f, dAAf = 0.05f;
//        enum TestStage {START, TESTING, KILL_ROT, NEXT_AAf, NEXT_THROTTLE, NEXT_AXIS, DONE};
//        TestStage stage = TestStage.START;
//
//        void tune_steering_test(Vector3 AAf)
//        {
//            VSL.Controls.GimbalLimit = 0;//VSL.OnPlanetParams.TWRf*100;
//            //tune PID parameters
//            var angularV = VSL.vessel.angularVelocity;
//            var angularM = Vector3.Scale(angularV, VSL.Physics.MoI);
//            var slow = VSL.Engines.Slow? 
//                (Vector3.one+Vector3.Scale(VSL.Torque.EnginesResponseTime, 
//                                           VSL.Torque.Engines.SpecificTorque)*ATCB.SlowTorqueF)
//                .ClampComponentsH(ATCB.MaxSlowF) : Vector3.one;
//            var slowi = slow.Inverse();
//            var iErr = (Vector3.one-angle_error);
//            var PIf = AAf.ScaleChain(iErr.ClampComponentsL(1/ATCB.MaxEf)*ATCB.MaxEf, slowi);
////            var AA_clamped = AA.ClampComponentsH(ATCB.MaxAA);
//            steering_pid.P = Vector3.Scale(ATCB.PID.P, PIf);
//            steering_pid.I = Vector3.Scale(ATCB.PID.I, PIf);
//            steering_pid.D = ATCB.PID.D.ScaleChain((iErr
////                                                    + (Vector3.one-AA_clamped/ATCB.MaxAA)
//                                                    + angularM.AbsComponents()*ATCB.AngularMf
//                                                   ).ClampComponentsH(1),
//
//                                                   AAf, slow,slow).ClampComponentsL(0);
//            //add inertia to handle constantly changing needed direction
//            var inertia = angularM.Sign()
//                .ScaleChain(iErr, 
//                            angularM, angularM, 
//                            Vector3.Scale(VSL.Torque.MaxCurrent.Torque, VSL.Physics.MoI).Inverse(0))
//                .ClampComponents(-Mathf.PI, Mathf.PI)
//                /Mathf.Lerp(ATCB.InertiaFactor, 1, VSL.Physics.MoI.magnitude*ATCB.MoIFactor);
//            steering += inertia;
//            //update PID
//            steering_pid.Update(steering, angularV);
//            steering = Vector3.Scale(steering_pid.Action, slowi);
//            //postprocessing by derived classes
//            correct_steering();
//        }
//
//
//        protected override void OnAutopilotUpdate(FlightCtrlState s)
//        {
//            if(!(CFG.Enabled && stage != TestStage.DONE && VSL.refT != null && VSL.orbit != null)) return;
//            if(VSL.AutopilotDisabled) { reset(); return; }
//            lthrust = VSL.LocalDir(VSL.Engines.CurrentDefThrustDir).normalized;
//            if(VSL.IsActiveVessel)
//                TCAGui.DebugMessage = Utils.Format("stage: {}, axis: {}\ntimer: {}\ncur AAf {}, throttle {}\n", 
//                                                   stage, axis, test_timer, curAAf, throttle);
//            switch(stage)
//            {
//            case TestStage.START:
//                CheatOptions.InfinitePropellant = true;
//                CheatOptions.InfiniteElectricity = true;
//                CheatOptions.IgnoreMaxTemperature = true;
//                VSL.vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
//                Debug.ClearDeveloperConsole();
//                needed_thrust = VSL.WorldDir(Quaternion.AngleAxis(120*direction, axes[axis])*lthrust);
//                OD.Reset();
//                test_timer.Reset();
//                direction = -direction;
//                stage = TestStage.TESTING;
//                break;
//            case TestStage.KILL_ROT:
//                THR.Throttle = 0;
//                CFG.AT.Off();
//                VSL.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
//                if(VSL.vessel.angularVelocity.sqrMagnitude > 1e-4) break;
//                VSL.vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
//                stage = TestStage.START;
//                break;
//            case TestStage.TESTING:
//                CFG.AT.OnIfNot(Attitude.Custom);
//                THR.Throttle = throttle;
//                needed_lthrust = VSL.LocalDir(needed_thrust);
//                compute_steering(lthrust, needed_lthrust);
//                tune_steering_test(Vector3.one*curAAf);
//                VSL.Controls.AddSteering(steering);
//                //detect oscillations
//                OD.Update(steering, TimeWarp.fixedDeltaTime);
//                if(VSL.IsActiveVessel)
//                    TCAGui.DebugMessage += 
//                        string.Format("pid: {0}\nsteering: {1}%\ngimbal limit: {2}\nOD: {3}",
//                                      steering_pid, steering_pid.Action*100, VSL.Controls.GimbalLimit, OD.Value);
//                
//                if(OD.Value.x > 0.1 ||
//                   OD.Value.y > 0.1 ||
//                   OD.Value.z > 0.1)
//                    stage = TestStage.NEXT_THROTTLE;
//                else if(VSL.Controls.AttitudeError < 1)
//                    stage = TestStage.NEXT_AAf;
//                break;
//            case TestStage.NEXT_AAf:
//                curAAf += dAAf;
//                stage = curAAf > 5+dAAf/2 ? TestStage.NEXT_THROTTLE : TestStage.KILL_ROT;
//                break;
//            case TestStage.NEXT_THROTTLE:
//                CSV(axes[axis] == Vector3.up? 0 : 1, throttle, AA, OD.Value, steering_pid.D, curAAf-dAAf);
//                curAAf = 0.1f;
//                throttle += dThrottle;
//                if(throttle > 1 && throttle < 1+dThrottle/2)
//                    throttle = 1;
//                stage = throttle > 1 ? TestStage.NEXT_AXIS : TestStage.KILL_ROT;
//                break;
//            case TestStage.NEXT_AXIS:
//                throttle = 0.1f;
//                axis += 1;
//                stage = axis < axes.Length ? TestStage.KILL_ROT : TestStage.DONE;
//                break;
//            }
//        }


		public void DrawDebugLines()
		{
			if(!CFG.AT || VSL == null || VSL.vessel == null || VSL.refT == null) return;
//			Utils.GLVec(VSL.refT.position, VSL.OnPlanetParams.Heading.normalized*2500, Color.white);
			Utils.GLVec(VSL.refT.position, VSL.WorldDir(lthrust.normalized)*20, Color.yellow);
			Utils.GLVec(VSL.refT.position, VSL.WorldDir(needed_lthrust.normalized)*20, Color.red);
            Utils.GLVec(VSL.refT.position, VSL.WorldDir(VSL.vessel.angularVelocity*20), Color.cyan);
            Utils.GLVec(VSL.refT.position, VSL.WorldDir(Vector3.Scale(rotation_axis, 
                                                                      new Vector3(at_pid_pitch.Action,
                                                                                  at_pid_roll.Action,
                                                                                  at_pid_yaw.Action))*20), Color.green);
//			Utils.GLVec(VSL.refT.position, VSL.WorldDir(steering*20), Color.cyan);
//			Utils.GLVec(VSL.refT.position, VSL.WorldDir(steering_pid.Action*20), Color.magenta);

//			Utils.GLVec(VSL.refT.position, VSL.refT.right*2, Color.red);
//			Utils.GLVec(VSL.refT.position, VSL.refT.forward*2, Color.blue);
//			Utils.GLVec(VSL.refT.position, VSL.refT.up*2, Color.green);

//			if(VSL.Target != null)
//				Utils.GLDrawPoint(VSL.Target.GetTransform().position, Color.red, 5);
//
//			VSL.Engines.All.ForEach(e => 
//			{
//				Utils.GLVec(e.wThrustPos, e.wThrustDir*2, Color.red);
//				Utils.GLVec(e.wThrustPos, e.defThrustDir*2, Color.yellow);
//			});
		}
		#endif
	}
}
