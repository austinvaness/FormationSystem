using Sandbox.ModAPI.Ingame;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using VRageMath;
using VRage;
using VRage.Game.ModAPI.Ingame.Utilities;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // Follower Script Version 1.2

        // ============= Settings ==============
        // Settings are located entirely in Custom Data.
        // Run the script once to generate an initial config.
        // =====================================

        // ============= Commands ==============
        // setoffset;x;y;z : sets the offset variable to this value
        // addoffset;x;y;z : adds these values to the current offset
        // stop : stops the script and releases control to you
        // start : starts the script after a stop command
        // starthere : starts the script in the current position
        // clear : forces the script to forget a previous leader when calculateMissingTicks is true
        // reset : loads the current configuration, or the first offset
        // save(;name) : saves the configuration to the current offset
        // savehere(;name) : saves the configuration to the current position
        // load;name : loads the offset from the configuration
        // =====================================

        StorageData storage;
        Settings settings;

        IMyShipController rc;
        ThrusterControl thrust;
        GyroControl gyros;
        List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();
        Vector3D? obstacleOffset = null;
        MatrixD leaderMatrix = MatrixD.Zero;
        Vector3D leaderVelocity;
        Random r = new Random();
        IMyBroadcastListener leaderListener;
        IMyBroadcastListener commandListener;
        readonly string transmitTag = "FSLeader";
        readonly string transmitCommandTag = "FSCommand";

        readonly int echoFrequency = 100; // every 100th game tick
        int runtime = 0;
        int updated = 0;

        bool prevControl = false;
        
        public Program ()
        {
            settings = new Settings(Me);
            settings.Load();

            storage = new StorageData(this);
            storage.Load();

            transmitTag += settings.followerSystemId.Value;
            transmitCommandTag += settings.followerSystemId.Value;

            // Prioritize the given cockpit name
            rc = GetBlock<IMyShipController>(settings.cockpitName.Value, true);
            if (rc == null) // Second priority cockpit
                rc = GetBlock<IMyCockpit>();
            if (rc == null) // Third priority remote control
                rc = GetBlock<IMyRemoteControl>();
            if (rc == null) // No cockpits found.
                throw new Exception("No cockpit/remote control found. Set the cockpitName field in settings.");

            thrust = new ThrusterControl(rc, settings.tickSpeed.Value, GetBlocks<IMyThrust>());
            gyros = new GyroControl(rc, settings.tickSpeed.Value, GetBlocks<IMyGyro>());

            if (settings.enableCollisionAvoidence.Value)
                cameras = GetBlocks<IMyCameraBlock>();

            if (settings.tickSpeed.Value == UpdateFrequency.Update10)
                echoFrequency = 10;
            else if (settings.tickSpeed.Value == UpdateFrequency.Update100)
                echoFrequency = 1;

            leaderListener = IGC.RegisterBroadcastListener(transmitTag);
            leaderListener.SetMessageCallback("");
            commandListener = IGC.RegisterBroadcastListener(transmitCommandTag);
            commandListener.SetMessageCallback("");

            Echo("Ready.");
            if (!storage.IsDisabled)
                Runtime.UpdateFrequency = settings.tickSpeed.Value;
        }

        void ResetMovement()
        {
            gyros.Reset();
            thrust.Reset();
        }

        public void Main (string argument, UpdateType updateSource)
        {
            if (updateSource == UpdateType.Update100 || updateSource == UpdateType.Update10 || updateSource == UpdateType.Update1)
            {
                if (runtime % echoFrequency == 0)
                    WriteEcho();

                // Check to make sure that a message from the leader has been received
                if (leaderMatrix == MatrixD.Zero)
                {
                    runtime++;
                    return;
                }

                if (settings.autoStop.Value)
                {
                    bool control = rc.IsUnderControl;
                    if (control != prevControl)
                    {
                        if (control)
                            ResetMovement();
                        else if (settings.autoStartHere.Value)
                            storage.Offset = CurrentOffset();

                        prevControl = control;
                    }

                    if (prevControl)
                    {
                        runtime++;
                        return;
                    }
                }

                Move();
                runtime++;
            }
            else if (updateSource == UpdateType.IGC)
            {
                if (leaderListener.HasPendingMessage)
                {
                    var data = leaderListener.AcceptMessage().Data;
                    if (data is MyTuple<MatrixD, Vector3D, long>)
                    {
                        MyTuple<MatrixD, Vector3D, long> msg = (MyTuple<MatrixD, Vector3D, long>)data;
                        if (msg.Item3 != Me.CubeGrid.EntityId)
                        {
                            leaderMatrix = msg.Item1;
                            leaderVelocity = msg.Item2;
                            updated = runtime;
                        }
                        else
                        {
                            leaderMatrix = MatrixD.Zero;
                        }
                    }
                    else if (data is MyTuple<MatrixD, Vector3D>)
                    {
                        MyTuple<MatrixD, Vector3D> msg = (MyTuple<MatrixD, Vector3D>)data;
                        leaderMatrix = msg.Item1;
                        leaderVelocity = msg.Item2;
                        updated = runtime;
                    }
                }

                if (commandListener.HasPendingMessage)
                {
                    var data = commandListener.AcceptMessage().Data;
                    if (data is MyTuple<string, string>)
                    {
                        MyTuple<string, string> msg = (MyTuple<string, string>)data;

                        if (msg.Item1.Length > 0)
                        {
                            foreach (string s in msg.Item1.Split(';'))
                            {
                                if (s == settings.followerId.Value)
                                {
                                    RemoteCommand(msg.Item2);
                                    return;
                                }
                            }
                            return;
                        }
                        else
                        {
                            RemoteCommand(msg.Item2);
                            return;
                        }
                    }
                }
            }
            else
            {
                RemoteCommand(argument);
            }
        }

        void WriteEcho()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Running.");
            sb.Append(settings.followerSystemId).Append('.').Append(settings.followerId).AppendLine();
            Vector3D offset = storage.Offset;
            sb.AppendLine(offset.ToString("0.00"));
            if (offset == Vector3D.Zero)
            {
                sb.AppendLine("Offset is zero,");
                sb.AppendLine("Use commands to give the offset a value.");
            }
            if (leaderMatrix == MatrixD.Zero)
                sb.AppendLine("No messages received.");
            else if (settings.calculateMissingTicks.Value && runtime - updated > settings.maxMissingScriptTicks.Value)
                sb.AppendLine($"Weak signal, message received {runtime - updated} ticks ago.");
            if (settings.autoStop.Value && prevControl)
                sb.AppendLine("Cockpit is under control.");
            if (obstacleOffset.HasValue)
                sb.AppendLine("Obstacle Detected! Stopping the ship.");
            sb.AppendLine();
            sb.AppendLine("Configs:");
            foreach (string s in settings.configs.Value.Keys)
            {
                sb.Append(' ').Append(s);
                if (s == storage.CurrentConfig)
                    sb.Append('*');
                sb.AppendLine();
            }

            Echo(sb.ToString());
        }


        void Move ()
        {
            gyros.FaceVectors(leaderMatrix.Forward, leaderMatrix.Up);

            Vector3D offset = storage.Offset;
            if (offset == Vector3D.Zero)
            {
                thrust.ApplyAccel(thrust.ControlVelocity(leaderVelocity));
                return;
            }

            // Apply translations to find the world position that this follower is supposed to be
            Vector3D targetPosition = Vector3D.Transform(offset, leaderMatrix);

            if (settings.calculateMissingTicks.Value)
            {
                int diff = Math.Min(Math.Abs(runtime - updated), settings.maxMissingScriptTicks.Value);
                if (diff > 0)
                {
                    double secPerTick = 1.0 / 60;
                    if (settings.tickSpeed.Value == UpdateFrequency.Update10)
                        secPerTick = 1.0 / 6;
                    else if (settings.tickSpeed.Value == UpdateFrequency.Update100)
                        secPerTick = 5.0 / 3;
                    double secPassed = diff * secPerTick;
                    targetPosition += leaderVelocity * secPassed;
                }
            }

            if (settings.enableCollisionAvoidence.Value)
            {
                CheckForCollistions(targetPosition);
                if (obstacleOffset.HasValue)
                {
                    thrust.ApplyAccel(thrust.ControlPosition(Vector3D.Transform(obstacleOffset.Value, leaderMatrix), leaderVelocity, settings.maxSpeed.Value));
                    return;
                }
            }

            thrust.ApplyAccel(thrust.ControlPosition(targetPosition, leaderVelocity, settings.maxSpeed.Value));
        }
        
        void RemoteCommand (string command)
        {
            string [] args = command.Split(';');

            switch (args [0])
            {
                case "setoffset": // setoffset;x;y;z
                    if (args.Length == 4)
                    {
                        Vector3D offset = storage.Offset;
                        double temp;
                        if (!string.IsNullOrWhiteSpace(args[1]) && double.TryParse(args[1], out temp))
                            offset.X = temp;
                        if (!string.IsNullOrWhiteSpace(args[2]) && double.TryParse(args[2], out temp))
                            offset.Y = temp;
                        if (!string.IsNullOrWhiteSpace(args[3]) && double.TryParse(args[3], out temp))
                            offset.Z = temp;

                        storage.Offset = offset;
                        WriteEcho();
                    }
                    else
                    {
                        return;
                    }
                    break;
                case "addoffset": // addoffset;x;y;z
                    if (args.Length == 4)
                    {
                        Vector3D offset = new Vector3D();
                        double temp;
                        if (!string.IsNullOrWhiteSpace(args[1]) && double.TryParse(args[1], out temp))
                            offset.X = temp;
                        if (!string.IsNullOrWhiteSpace(args[2]) && double.TryParse(args[2], out temp))
                            offset.Y = temp;
                        if (!string.IsNullOrWhiteSpace(args[3]) && double.TryParse(args[3], out temp))
                            offset.Z = temp;

                        storage.Offset += offset;
                        WriteEcho();
                    }
                    else
                    {
                        return;
                    }
                    break;
                case "stop": // stop
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    ResetMovement();
                    storage.IsDisabled = true;
                    Echo("Stopped.");
                    break;
                case "start": // start
                    Runtime.UpdateFrequency = settings.tickSpeed.Value;
                    storage.IsDisabled = false;
                    WriteEcho();
                    break;
                case "starthere": // starthere
                    Runtime.UpdateFrequency = settings.tickSpeed.Value;
                    storage.AutoSave = false;
                    storage.Offset = CurrentOffset();
                    storage.IsDisabled = false;
                    storage.Save();
                    WriteEcho();
                    break;
                case "reset": // reset
                    {
                        Vector3D newOffset;
                        if (!settings.configs.Value.TryGetValue(storage.CurrentConfig, out newOffset))
                            newOffset = settings.configs.Value.First().Value;
                        storage.Offset = newOffset;
                        WriteEcho();
                    }
                    break;
                case "save": // save(;name)
                    {
                        string key = storage.CurrentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        settings.configs.Value [key] = storage.Offset;
                        settings.Save();
                    }
                    break;
                case "savehere": // save(;name)
                    {
                        string key = storage.CurrentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        Vector3D newOffset = CurrentOffset();
                        settings.configs.Value [key] = newOffset;
                        settings.Save();
                    }
                    break;
                case "load": // load;name
                    {
                        Vector3D newOffset;
                        if (args.Length == 1 || !settings.configs.Value.TryGetValue(args[1], out newOffset))
                            return;
                        // Load the new config
                        storage.AutoSave = false;
                        storage.Offset = newOffset;
                        storage.CurrentConfig = args [1];
                        storage.IsDisabled = false;
                        storage.Save();
                        WriteEcho();
                    }
                    break;
                case "clear":
                    leaderMatrix = MatrixD.Zero;
                    break;
            }
        }

        Vector3D CurrentOffset ()
        {
            return Vector3D.TransformNormal(rc.GetPosition() - leaderMatrix.Translation, MatrixD.Transpose(rc.WorldMatrix));
        }

        bool StringToVector (string x, string y, string z, out Vector3D output)
        {
            try
            {
                double x2 = double.Parse(x);
                double y2 = double.Parse(y);
                double z2 = double.Parse(z);
                output = new Vector3D(x2, y2, z2);
                return true;
            } 
            catch (Exception)
            {
                output = new Vector3D();
                return false;
            }
        }

        void CheckForCollistions (Vector3D target)
        {
            double resultDist;
            if (!Raycast(target, out resultDist))
            {
                return;
            }

            if (double.IsInfinity(resultDist))
            {
                obstacleOffset = null;
                return;
            }

            obstacleOffset = CurrentOffset();
        }

        bool Raycast (Vector3D target, out double hitDistance)
        {
            bool raycasted = false;
            hitDistance = double.PositiveInfinity;
            foreach (IMyCameraBlock c in cameras)
            {
                c.Enabled = true;
                c.EnableRaycast = true;
                if (c.CanScan(target))
                {
                    raycasted = true;
                    MyDetectedEntityInfo info = c.Raycast(target);
                    if (info.HitPosition.HasValue)
                    {
                        if (info.EntityId == Me.CubeGrid.EntityId)
                            continue;

                        double dist = Vector3D.Distance(c.GetPosition(), info.HitPosition.Value);
                        if (dist < hitDistance && dist > 0.1)
                        {
                            hitDistance = dist;
                        }

                    }
                }
            }
            return raycasted;
        }
    }
}