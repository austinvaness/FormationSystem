using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System;
using VRage.Collections;
using VRage.Game.Components;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        // MotorStator, LargeStator: 8000
        // MotorSuspension, Suspension1x1: 20000
        // MotorSuspension, Suspension3x3: 60000
        // MotorSuspension, Suspension5x5: 100000
        // MotorSuspension, Suspension1x1mirrored: 20000
        // MotorSuspension, Suspension3x3mirrored: 60000
        // MotorSuspension, Suspension5x5mirrored: 100000
        // MotorSuspension, SmallSuspension1x1: 120
        // MotorSuspension, SmallSuspension3x3: 1920
        // MotorSuspension, SmallSuspension5x5: 4800
        // MotorSuspension, SmallSuspension1x1mirrored: 120
        // MotorSuspension, SmallSuspension3x3mirrored: 1920
        // MotorSuspension, SmallSuspension5x5mirrored: 4800

        public class WheelControl
        {
            PID anglePID;
            PID forwardPID;
            IMyShipController rc;
            bool first = true;
            readonly List<Wheel> wheels = new List<Wheel>();
            Program prg;

            struct Wheel
            {
                public IMyMotorSuspension block;
                public float maxForce;

                public Wheel (IMyMotorSuspension block, float maxForce)
                {
                    this.block = block;
                    this.maxForce = maxForce;
                }
            }

            public WheelControl (Program prg, IMyShipController rc, UpdateFrequency tickSpeed, List<IMyMotorSuspension> wheels)
            {
                this.prg = prg;
                if (rc == null)
                    throw new Exception("Ship controller null.");

                this.rc = rc;

                this.wheels = wheels.Select(x => new Wheel(x, GetForce(x))).ToList();

                double factor = 1;
                if (tickSpeed == UpdateFrequency.Update10)
                    factor = 10;
                else if (tickSpeed == UpdateFrequency.Update100)
                    factor = 100;
                double secondsPerTick = (1.0 / 60) * factor;

                anglePID = new PID(P2 / factor, I2 / factor, D2 / factor, 0.2 / factor, secondsPerTick);
                forwardPID = new PID(P / factor, I / factor, D / factor, 0.2 / factor, secondsPerTick);
                Reset();
            }

            public void Update (Vector3D target)
            {
                MatrixD transpose = MatrixD.Transpose(rc.WorldMatrix);

                Vector3D meToTarget = rc.WorldMatrix.Translation - target;
                Vector3D localError = Vector3D.TransformNormal(meToTarget, transpose);


                prg.Echo(localError.ToString());

                localError.Y = 0;
                if (localError.X > -0.5 && localError.X < 0.5)
                    localError.X = 0;
                if (localError.Z > -0.5 && localError.Z < 0.5)
                    localError.Z = 0;

                float correction = (float)forwardPID.Control(localError.Z);
                float force = correction * rc.CalculateShipMass().TotalMass;

                float rightLeft = (float)anglePID.Control(-localError.X);
                Vector3D localVelocity = Vector3D.TransformNormal(rc.GetShipVelocities().LinearVelocity, transpose);
                float angle = -rightLeft;
                if (localVelocity.Z < 0)
                    angle *= -1;

                foreach (Wheel w in wheels)
                {
                    IMyMotorSuspension wheel = w.block;
                    if (first)
                    {
                        Vector3D center = Vector3D.TransformNormal(rc.CenterOfMass - rc.GetPosition(), transpose);
                        Vector3D local = Vector3D.TransformNormal(wheel.GetPosition() - rc.GetPosition(), transpose);
                        wheel.InvertSteer = (local.Z > center.Z);
                        wheel.InvertPropulsion = (wheel.Orientation.Left != rc.Orientation.Forward);
                        wheel.Brake = false;
                    }

                    if (wheel.Steering)
                        wheel.SetValueFloat("Steer override", angle);

                    if (wheel.Propulsion)
                    {
                        float maxForce = w.maxForce;
                        if (maxForce <= 0)
                            continue;
                        float percent = MathHelper.Clamp(force / maxForce, -1, 1);
                        force -= percent * maxForce;
                        wheel.SetValueFloat("Propulsion override", percent);
                    }
                }
                first = false;
            }

            float GetForce (IMyMotorSuspension wheel)
            {
                switch (wheel.BlockDefinition.SubtypeId)
                {
                    case "Suspension1x1mirrored":
                    case "Suspension1x1":
                        return 20000;
                    case "Suspension3x3mirrored":
                    case "Suspension3x3":
                        return 60000;
                    case "Suspension5x5mirrored":
                    case "Suspension5x5":
                        return 100000;
                    case "SmallSuspension1x1mirrored":
                    case "SmallSuspension1x1":
                        return 120;
                    case "SmallSuspension3x3mirrored":
                    case "SmallSuspension3x3":
                        return 1920;
                    case "SmallSuspension5x5mirrored":
                    case "SmallSuspension5x5":
                        return 4800;
                }
                throw new Exception("Unknown wheel type.");
            }

            public void Reset ()
            {
                foreach (Wheel w in wheels)
                {
                    IMyMotorSuspension wheel = w.block;
                    wheel.SetValueFloat("Propulsion override", 0);
                    wheel.SetValueFloat("Steer override", 0);
                    wheel.InvertPropulsion = false;
                    wheel.InvertSteer = false;
                    wheel.Brake = true;
                }
                first = true;
            }
        }
    }
}
