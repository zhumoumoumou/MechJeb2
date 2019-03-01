﻿using System;

using UnityEngine;
using KSP.UI.Screens;
using System.Collections.Generic;
using System.Reflection;

namespace MuMech
{
    public enum PVGStatus { ENABLED, INITIALIZING, CONVERGED, BURNING, COASTING, BURNING_STAGING, COASTING_STAGING, TERMINAL, TERMINAL_RCS, FINISHED, FAILED };

    public class MechJebModuleGuidanceController : ComputerModule
    {
        public MechJebModuleGuidanceController(MechJebCore core) : base(core) { }

        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pvgInterval = new EditableDouble(1.00);

        // these variables will persist even if Reset() completely blows away the solution, so that pitch+heading will still be stable
        // until a new solution is found.
        public Vector3d lambda;
        public Vector3d lambdaDot;
        public double t_lambda;
        public Vector3d iF;
        public double pitch;
        public double heading;
        public double tgo;
        public double vgo;

        // this is a public setting to control autowarping
        public bool autowarp = false;

        public PontryaginBase.Solution solution { get { return ( p != null ) ? p.solution : null; } }
        public List<PontryaginBase.Arc> arcs { get { return ( solution != null) ? p.solution.arcs : null; } }

        public int successful_converges { get { return ( p != null ) ? p.successful_converges : 0; } }
        public int max_lm_iteration_count { get { return ( p != null ) ? p.max_lm_iteration_count : 0; } }
        public int last_lm_iteration_count { get { return ( p != null ) ? p.last_lm_iteration_count : 0; } }
        public int last_lm_status { get { return ( p != null ) ? p.last_lm_status : 0; } }
        public double last_znorm { get { return ( p != null ) ? p.last_znorm : 0; } }
        public String last_failure_cause { get { return ( p != null ) ? p.last_failure_cause : null; } }
        public double last_success_time = 0.0;
        public double staleness { get { return ( last_success_time > 0 ) ? vesselState.time - last_success_time : 0; } }

        public PVGStatus status;
        public PVGStatus oldstatus;

        public double last_stage_time = 0.0;
        public double last_optimizer_time = 0.0;

        public override void OnStart(PartModule.StartState state)
        {
            GameEvents.onStageActivate.Add(handleStageEvent);
        }

        public override void OnDestroy()
        {
            GameEvents.onStageActivate.Remove(handleStageEvent);
        }

        private void handleStageEvent(int data)
        {
            last_stage_time = vesselState.time;
        }

        public override void OnModuleEnabled()
        {
            // coast phases are deliberately not reset in Reset() so we never get a completed coast phase again after whacking Reset()
            status = PVGStatus.ENABLED;
            core.attitude.users.Add(this);
            core.thrust.users.Add(this);
            core.stageTracking.users.Add(this);
        }

        public override void OnModuleDisabled()
        {
            core.attitude.attitudeDeactivate();
            if (!core.rssMode)
                core.thrust.ThrustOff();
            core.thrust.users.Remove(this);
            core.stageTracking.users.Remove(this);
            status = PVGStatus.FINISHED;
            last_success_time = 0.0;
            if (p != null)
                p.KillThread();
            p = null;
        }

        public bool allow_execution = false;

        // ENABLED just means the module is enabled, by calling Reset here we transition from ENABLED to INITIALIZING
        //
        // by setting allow_execution here we allow moving to COASTING or BURNING from INITIALIZED
        // FIXME: we don't seem to need allow_execution = false now?
        public void AssertStart(bool allow_execution = true)
        {
            if (status == PVGStatus.ENABLED )
                Reset();
            this.allow_execution = allow_execution;
        }

        public override void OnFixedUpdate()
        {
            update_pitch_and_heading();

            if ( isLoadedPrincipia )
            {
                // Debug.Log("FOUND PRINCIPIA!!!");
            }

            if ( !HighLogic.LoadedSceneIsFlight )
            {
                Debug.Log("MechJebModuleGuidanceController [BUG]: PVG enabled in non-flight mode.  How does this happen?");
                Done();
            }

            if ( !enabled || status == PVGStatus.ENABLED )
                return;

            if ( status == PVGStatus.FINISHED )
            {
                Done();
                return;
            }

            if ( status == PVGStatus.FAILED )
                Reset();

            if (p != null)
            {
                // propagate exceptions and debug information out of the solver thread
                p.Janitorial();

                // update the position (safe to call on every tick, will not update if a thread is running)
                p.UpdatePosition(vesselState.orbitalPosition, vesselState.orbitalVelocity, lambda, lambdaDot, tgo, vgo);
            }

            if ( p != null && p.solution != null && isTerminalGuidance() )
            {
                bool has_rcs = vessel.hasEnabledRCSModules(); // && ( vesselState.rcsThrustAvailable.up > 0 );

                // stopping one tick short is more accurate for rockets without RCS, but sometimes we overshoot with only one tick
                int ticks = 1;
                if (has_rcs)
                    ticks = 2;

                if (status == PVGStatus.TERMINAL_RCS && !vessel.ActionGroups[KSPActionGroup.RCS])  // if someone manually disables RCS
                {
                    Done();
                    return;
                }

                // bit of a hack to predict velocity + position in the next tick or two
                // FIXME: what exactly does KSP do to integrate over timesteps?
                Vector3d a0 = vessel.acceleration_immediate - vessel.graviticAcceleration;
                double dt = ticks * TimeWarp.fixedDeltaTime;
                Vector3d v1 = vesselState.orbitalVelocity + a0 * dt;
                Vector3d x1 = vesselState.orbitalPosition + vesselState.orbitalVelocity * dt + 1/2 * a0 * dt * dt;

                double current = p.znormAtStateVectors(vesselState.orbitalPosition, vesselState.orbitalVelocity);
                double future = p.znormAtStateVectors(x1, v1);

                if ( future > current )
                {
                    if ( has_rcs && status == PVGStatus.TERMINAL )
                    {
                        status = PVGStatus.TERMINAL_RCS;
                        if (!vessel.ActionGroups[KSPActionGroup.RCS])
                            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                    }
                    else
                    {
                        Done();
                        return;
                    }
                }
            }

            handle_throttle();

            if ( isStaging() )
                return;

            core.stageTracking.Update();

            converge();

        }

        /*
         * TARGET APIs
         */

        public void TargetNode(ManeuverNode node, double burntime)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            if (p == null || isCoasting())
            {
                PontryaginNode solver = p as PontryaginNode;
                if (solver == null)
                    solver = new PontryaginNode(core: core, mu: mainBody.gravParameter, r0: vesselState.orbitalPosition, v0: vesselState.orbitalVelocity, pv0: node.GetBurnVector(orbit).normalized, pr0: Vector3d.zero, dV: node.GetBurnVector(orbit).magnitude, bt: burntime);
                solver.intercept(node.nextPatch);
                p = solver;
            }
        }

        // FIXME: convert v0m/r0m to vTm/rTm because its terminal not initial
        /* converts PeA + ApA into r0m/v0m for periapsis insertion.
           - handles hyperbolic orbits
           - remaps ApA < PeA onto circular orbits */
        private void ConvertToRadVel(double PeA, double ApA, out double r0m, out double v0m, out double sma)
        {
            double PeR = mainBody.Radius + PeA;
            double ApR = mainBody.Radius + ApA;

            r0m = PeR;

            sma = (PeR + ApR) / 2;

            /* remap nonsense ApAs onto circular orbits */
            if ( ApA >= 0 && ApA < PeA )
                sma = PeR;

            v0m = Math.Sqrt( mainBody.gravParameter * ( 2 / PeR - 1 / sma ) );
        }

        // convert PeA/ApA values to SMA+ECC
        private void ConvertToSMAEcc(double PeA, double ApA, out double sma, out double ecc)
        {
            double PeR = mainBody.Radius + PeA;
            double ApR = mainBody.Radius + ApA;

            /* remap nonsense ApAs onto circular orbits */
            if ( ApA >= 0 && ApA < PeA )
                ApR = PeR;

            sma = (PeR + ApR) / 2;

            ecc = (ApR - PeR) / (ApR + PeR);
        }

        double old_PeA;
        double old_ApA;
        double old_inc;
        double old_gamma;
        double old_LAN;
        double old_ArgP;

        public void flightangle4constraint(double PeA, double ApA, double inc, double gamma, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double r0m, v0m, sma;

            ConvertToRadVel(PeA, ApA, out r0m, out v0m, out sma);

            if (PeA != old_PeA || ApA != old_ApA || gamma != old_gamma)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up flightangle5constraint");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.flightangle4constraint(r0m, v0m, inc * UtilMath.Deg2Rad, gamma * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA   = PeA;
            old_ApA   = ApA;
            old_inc   = inc;
            old_gamma = gamma;
        }

        public void flightangle5constraint(double PeA, double ApA, double inc, double gamma, double LAN, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double r0m, v0m, sma;

            ConvertToRadVel(PeA, ApA, out r0m, out v0m, out sma);

            if (PeA != old_PeA || ApA != old_ApA || gamma != old_gamma || LAN != old_LAN)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up flightangle5constraint");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.flightangle5constraint(r0m, v0m, inc * UtilMath.Deg2Rad, gamma * UtilMath.Deg2Rad, LAN * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA   = PeA;
            old_ApA   = ApA;
            old_inc   = inc;
            old_gamma = gamma;
            old_LAN   = LAN;
        }


        // guess at delta v based on specific orbital energy
        // https://en.wikipedia.org/wiki/Specific_orbital_energy
        //
        private double approximateDeltaV(double sma)
        {
            double addE = mainBody.gravParameter * ( 2 * sma - mainBody.Radius ) / ( 2 * sma * mainBody.Radius );  // in kJ/t or m^2/s^2
            return Math.Sqrt( 2 * addE ) + 2000; // E = v^2 / 2 solved for v with 2000 thrown in for grav losses and drag
        }

        private PontryaginLaunch NewPontryaginForLaunch(double inc, double sma)
        {
            lambdaDot = Vector3d.zero;
            double desiredHeading = OrbitalManeuverCalculator.HeadingForInclination(inc, vesselState.latitude);
            Vector3d desiredHeadingVector = Math.Sin(desiredHeading * UtilMath.Deg2Rad) * vesselState.east + Math.Cos(desiredHeading * UtilMath.Deg2Rad) * vesselState.north;
            Vector3d desiredThrustVector = Math.Cos(45 * UtilMath.Deg2Rad) * desiredHeadingVector + Math.Sin(45 * UtilMath.Deg2Rad) * vesselState.up;  /* 45 pitch guess */
            lambda = desiredThrustVector;
            Debug.Log("sma = " + sma);
            Debug.Log("deltaV guess = " + approximateDeltaV(sma));
            return new PontryaginLaunch(core: core, mu: mainBody.gravParameter, r0: vesselState.orbitalPosition, v0: vesselState.orbitalVelocity, pr0: Vector3d.zero, dV: approximateDeltaV(sma));
        }

        public void keplerian3constraint(double PeA, double ApA, double inc, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double sma, ecc;

            ConvertToSMAEcc(PeA, ApA, out sma, out ecc);

            if (PeA != old_PeA || ApA != old_ApA)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up keplerian3constraint");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.keplerian3constraint(sma, ecc, inc * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA = PeA;
            old_ApA = ApA;
            old_inc = inc;
        }

        public void keplerian4constraintArgPfree(double PeA, double ApA, double inc, double LAN, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double sma, ecc;

            ConvertToSMAEcc(PeA, ApA, out sma, out ecc);

            if (PeA != old_PeA || ApA != old_ApA || LAN != old_LAN)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up keplerian4constraintArgPfree");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.keplerian4constraintArgPfree(sma, ecc, inc * UtilMath.Deg2Rad, LAN * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA = PeA;
            old_ApA = ApA;
            old_inc = inc;
            old_LAN = LAN;
        }

        public void keplerian4constraintLANfree(double PeA, double ApA, double inc, double ArgP, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double sma, ecc;

            ConvertToSMAEcc(PeA, ApA, out sma, out ecc);

            if (PeA != old_PeA || ApA != old_ApA || ArgP != old_ArgP)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up keplerian4constraintLANfree");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.keplerian4constraintLANfree(sma, ecc, inc * UtilMath.Deg2Rad, ArgP * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA = PeA;
            old_ApA = ApA;
            old_inc = inc;
            old_ArgP = ArgP;
        }

        public void keplerian5constraint(double PeA, double ApA, double inc, double LAN, double ArgP, bool omitCoast, bool currentInc)
        {
            if ( status == PVGStatus.ENABLED )
                return;

            bool doupdate = false;

            double sma, ecc;

            ConvertToSMAEcc(PeA, ApA, out sma, out ecc);

            if (PeA != old_PeA || ApA != old_ApA || inc != old_inc || LAN != old_LAN || ArgP != old_ArgP)
                doupdate = true;

            // avoid slight drift in the current inclination from resetting guidance constantly
            if (inc != old_inc && !currentInc)
                doupdate = true;

            if (p == null || doupdate)
            {
                if (p != null)
                    p.KillThread();

                Debug.Log("[MechJeb] MechJebModuleGuidanceController: setting up keplerian5constraint");
                PontryaginLaunch solver = NewPontryaginForLaunch(inc, sma);
                solver.omitCoast = omitCoast;
                solver.keplerian5constraint(sma, ecc, inc * UtilMath.Deg2Rad, LAN * UtilMath.Deg2Rad, ArgP * UtilMath.Deg2Rad);
                p = solver;
            }

            old_PeA = PeA;
            old_ApA = ApA;
            old_inc = inc;
            old_LAN = LAN;
            old_ArgP = ArgP;
        }

        /* meta state for consumers that means "is iF usable?" (or pitch/heading) */
        public bool isStable()
        {
            return isNormal() || isTerminalGuidance();
        }

        // not TERMINAL guidance or TERMINAL_RCS
        public bool isNormal()
        {
            return status == PVGStatus.CONVERGED || status == PVGStatus.BURNING || status == PVGStatus.BURNING_STAGING || status == PVGStatus.COASTING || status == PVGStatus.COASTING_STAGING;
        }

        public bool isCoasting()
        {
            return status == PVGStatus.COASTING || status == PVGStatus.COASTING_STAGING;
        }

        public bool isStaging()
        {
            return status == PVGStatus.BURNING_STAGING || status == PVGStatus.COASTING_STAGING;
        }

        public bool isTerminalGuidance()
        {
            return status == PVGStatus.TERMINAL || status == PVGStatus.TERMINAL_RCS;
        }

        /* normal pre-states but not usefully converged */
        public bool isInitializing()
        {
            return status == PVGStatus.ENABLED || status == PVGStatus.INITIALIZING;
        }

        private PontryaginBase p;

        private void converge()
        {
            if (p == null)
            {
                status = PVGStatus.INITIALIZING;
                return;
            }

            if (p.last_success_time > 0)
                last_success_time = p.last_success_time;

            if (p.solution == null)
            {
                /* we have a solver but no solution */
                status = PVGStatus.INITIALIZING;
            }
            else
            {
                /* we have a solver and have a valid solution */
                if ( isTerminalGuidance() )
                    return;

                /* hardcoded 10 seconds of terminal guidance */
                if ( tgo < 10 )
                {
                    // drop out of warp for terminal guidance (smaller time ticks => more accuracy)
                    core.warp.MinimumWarp();
                    status = PVGStatus.TERMINAL;
                    return;
                }
            }

            if ( (vesselState.time - last_optimizer_time) < MuUtils.Clamp(pvgInterval, 1.00, 30.00) )
                return;

            // for last 10 seconds of coast phase don't recompute (FIXME: can this go lower?  it was a workaround for a bug)
            if ( p.solution != null && p.solution.arc(vesselState.time).thrust == 0 && p.solution.current_tgo(vesselState.time) < 10 )
                return;

            p.threadStart(vesselState.time);
            //if ( p.threadStart(vesselState.time) )
                //Debug.Log("MechJeb: started optimizer thread");

            if (status == PVGStatus.INITIALIZING && p.solution != null)
                status = PVGStatus.CONVERGED;

            last_optimizer_time = vesselState.time;
        }

        private int last_burning_stage;
        private bool last_burning_stage_complete;
        private double last_coasting_time = 0.0;

        // if we're transitioning from a complete thrust phase to a coast, wait for staging, otherwise
        // just go off of whatever the solution says for the current time.
        private bool actuallyCoasting()
        {
            PontryaginBase.Arc current_arc = p.solution.arc(vesselState.time);

            if ( last_burning_stage_complete && last_burning_stage <= vessel.currentStage )
                return false;
            return current_arc.thrust == 0;
        }

        private void handle_throttle()
        {
            if ( p == null || p.solution == null )
                return;

            if ( !allow_execution )
                return;

            if ( status == PVGStatus.TERMINAL_RCS )
            {
                RCSOn();
                return;
            }

            PontryaginBase.Arc current_arc = p.solution.arc(vesselState.time);

            if ( current_arc.thrust != 0 )
            {
                last_burning_stage = current_arc.ksp_stage;
                last_burning_stage_complete = current_arc.complete_burn;
            }

            if ( actuallyCoasting() )
            {
                // force RCS on at the state transition
                if ( !isCoasting() )
                {
                    if (!vessel.ActionGroups[KSPActionGroup.RCS])
                        vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                }

                if ( !isTerminalGuidance() )
                {
                    if (vesselState.time < last_stage_time + 4)
                        status = PVGStatus.COASTING_STAGING;
                    else
                        status = PVGStatus.COASTING;
                }
                last_coasting_time = vesselState.time;

                // this turns off autostaging during the coast (which currently affects fairing separation)
                core.staging.autostageLimitInternal = last_burning_stage - 1;
                ThrustOff();
            }
            else
            {
                if ( !isTerminalGuidance() )
                {
                    if ((vesselState.time < last_stage_time + 4) || (vesselState.time < last_coasting_time + 4))
                        status = PVGStatus.BURNING_STAGING;
                    else
                        status = PVGStatus.BURNING;
                }

                if (core.staging.autostageLimitInternal > 0)
                {
                    core.staging.autostageLimitInternal = 0;
                }
                else
                {
                    ThrustOn();
                }
            }
        }

        /* extract pitch and heading off of iF to avoid continuously recomputing on every call */
        private void update_pitch_and_heading()
        {
            // FIXME: if we have no solution update off of lambda + lambdaDot + last update time
            if (p == null || p.solution == null)
                return;

            // if we're not flying yet, continuously update the t0 of the solution
            if ( vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.SPLASHED )
                p.solution.t0 = vesselState.time;

            if ( status == PVGStatus.TERMINAL_RCS )
            {
                /* leave pitch, heading and lambda at the last values, also stop updating vgo/tgo */
                lambdaDot = Vector3d.zero;
            }
            else
            {
                lambda = p.solution.pv(vesselState.time);
                lambdaDot = p.solution.pr(vesselState.time);
                iF = lambda.normalized;
                p.solution.pitch_and_heading(vesselState.time, ref pitch, ref heading);
                tgo = p.solution.tgo(vesselState.time);
                vgo = p.solution.vgo(vesselState.time);
            }
        }

        private void ThrustOn()
        {
            core.thrust.targetThrottle = 1.0F;
        }

        private void RCSOn()
        {
            core.thrust.ThrustOff();
            vessel.ctrlState.Z = -1.0F;
        }

        private void ThrustOff()
        {
            core.thrust.ThrustOff();
        }

        private void Done()
        {
            users.Clear();
            ThrustOff();
            status = PVGStatus.FINISHED;
            enabled = false;
        }

        public void Reset()
        {
            // lambda and lambdaDot are deliberately not cleared here
            //Debug.Log("call stack: + " + Environment.StackTrace);
            if (p != null)
            {
                p.KillThread();
                p = null;
            }
            status = PVGStatus.INITIALIZING;
            last_stage_time = 0.0;
            last_optimizer_time = 0.0;
            last_coasting_time = 0.0;
            last_success_time = 0.0;
            autowarp = false;
            if (!MuUtils.PhysicsRunning()) core.warp.MinimumWarp();
        }

        public static bool isLoadedPrincipia = false;
        public static MethodInfo principiaEGNPCDOF;

        static MechJebModuleGuidanceController()
        {
            isLoadedPrincipia = ReflectionUtils.isAssemblyLoaded("ksp_plugin_adapter");
            if (isLoadedPrincipia)
            {
                principiaEGNPCDOF = ReflectionUtils.getMethodByReflection("ksp_plugin_adapter", "principia.ksp_plugin_adapter.Interface", "ExternalGetNearestPlannedCoastDegreesOfFreedom", BindingFlags.NonPublic | BindingFlags.Static);
                if (principiaEGNPCDOF == null)
                {
                    // Debug.Log("failed to find ExternalGetNearestPlannedCoastDegreesOfFreedom");
                    isLoadedPrincipia = false;
                    return;
                }
            }
        }
    }
}
