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
        // Follower Script Version 1.1

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

        Settings settings;

        string currentConfig = "default";

        IMyShipController rc;
        ThrusterControl thrust;
        GyroControl gyros;
        List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();
        Vector3D? obstacleOffset = null;
        MatrixD leaderMatrix = MatrixD.Zero;
        Vector3D leaderVelocity;
        bool isDisabled = false;
        Random r = new Random();
        Vector3D offset = new Vector3D(50, 0, 0);
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

            transmitTag += settings.followerSystemId.Value;
            transmitCommandTag += settings.followerSystemId.Value;

            // Prioritize the given cockpit name
            rc = GetBlock<IMyShipController>(settings.cockpitName.Value, true);
            if (rc == null) // Second priority cockpit
                rc = GetBlock<IMyCockpit>();
            if (rc == null) // Thrid priority remote control
                rc = GetBlock<IMyRemoteControl>();
            if (rc == null) // No cockpits found.
                throw new Exception("No cockpit/remote control found. Set the cockpitName field in settings.");

            thrust = new ThrusterControl(rc, settings.tickSpeed.Value, GetBlocks<IMyThrust>());
            gyros = new GyroControl(rc, settings.tickSpeed.Value, GetBlocks<IMyGyro>());

            LoadStorage();

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

            Echo("Running.");
        }

        void ResetMovement ()
        {
            gyros.Reset();
            thrust.Reset();
        }

        public void SaveStorage ()
        {
            // isDisabled;currentConfig;x;y;z
            StringBuilder sb = new StringBuilder();
            if (isDisabled)
                sb.Append("1;");
            else
                sb.Append("0;");
            sb.Append(currentConfig);
            sb.Append(';');
            sb.Append(offset.X);
            sb.Append(';');
            sb.Append(offset.Y);
            sb.Append(';');
            sb.Append(offset.Z);
            Storage = sb.ToString();
        }

        public void SaveAll ()
        {
            SaveStorage();
            settings.Save();
        }

        void LoadStorage ()
        {
            if (string.IsNullOrWhiteSpace(Storage))// || string.IsNullOrWhiteSpace(Me.CustomData))
            {
                SaveStorage();
                Runtime.UpdateFrequency = settings.tickSpeed.Value;
            }

            try
            {
                // Parse Storage values
                string [] args = Storage.Split(';');
                bool loadedIsDisabled = args [0] == "1";
                string loadedCurrentConfig = args [1];
                Vector3D loadedOffset = new Vector3D(
                    double.Parse(args [2]),
                    double.Parse(args [3]),
                    double.Parse(args [4])
                    );

                if (settings.configs.Value.ContainsKey(loadedCurrentConfig))
                    currentConfig = loadedCurrentConfig;
                else
                    currentConfig = settings.configs.Value.First().Key;
                offset = loadedOffset;
                isDisabled = loadedIsDisabled;
                if (!isDisabled)
                    Runtime.UpdateFrequency = settings.tickSpeed.Value;
            } 
            catch (Exception)
            {
                SaveStorage();
                Runtime.UpdateFrequency = settings.tickSpeed.Value;
            }
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
                            offset = CurrentOffset();

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

        void WriteEcho ()
        {
            Echo("Running.\n"+settings.followerSystemId+"."+settings.followerId+"\nConfigs:");
            foreach (string s in settings.configs.Value.Keys)
            {
                if (s == currentConfig)
                    Echo(s + '*');
                else
                    Echo(s);
            }
            Echo(offset.ToString("0.00"));
            if(leaderMatrix == MatrixD.Zero)
                Echo("No messages received.");
            else if (settings.calculateMissingTicks.Value && runtime - updated > settings.maxMissingScriptTicks.Value)
                Echo($"Weak signal, message receved {runtime - updated} ticks ago.");
            if (settings.autoStop.Value && prevControl)
                Echo("Cockpit is under control.");
            if (obstacleOffset.HasValue)
                Echo("Obstacle Detected! Stopping the ship.");

        }


        void Move ()
        {
            gyros.FaceVectors(leaderMatrix.Forward, leaderMatrix.Up);

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
                        if (args [1] == "")
                            args [1] = this.offset.X.ToString();
                        if (args [2] == "")
                            args [2] = this.offset.Y.ToString();
                        if (args [3] == "")
                            args [3] = this.offset.Z.ToString();

                        Vector3D offset;
                        if (!StringToVector(args [1], args [2], args [3], out offset))
                            return;
                        this.offset = offset;
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
                        Vector3D offset;
                        if (!StringToVector(args [1], args [2], args [3], out offset))
                            return;
                        this.offset += offset;
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
                    isDisabled = true;
                    Echo("Stopped.");
                    break;
                case "start": // start
                    Runtime.UpdateFrequency = settings.tickSpeed.Value;
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "starthere": // starthere
                    Runtime.UpdateFrequency = settings.tickSpeed.Value;
                    offset = CurrentOffset();
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "reset": // reset
                    {
                        Vector3D newOffset;
                        if (!settings.configs.Value.TryGetValue(currentConfig, out newOffset))
                            newOffset = settings.configs.Value.First().Value;
                        offset = newOffset;
                        SaveStorage();
                        WriteEcho();
                    }
                    break;
                case "save": // save(;name)
                    {
                        string key = currentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        settings.configs.Value [key] = offset;
                        SaveAll();
                    }
                    break;
                case "savehere": // save(;name)
                    {
                        string key = currentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        Vector3D newOffset = CurrentOffset();
                        settings.configs.Value [key] = newOffset;
                        SaveAll();
                    }
                    break;
                case "load": // load;name
                    {
                        Vector3D newOffset;
                        if (args.Length == 1 || !settings.configs.Value.TryGetValue(args[1], out newOffset))
                            return;
                        // Load the new config
                        offset = newOffset;
                        currentConfig = args [1];
                        isDisabled = false;
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