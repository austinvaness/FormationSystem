using Sandbox.ModAPI.Ingame;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using VRage.Game;
using VRageMath;
using VRage;
using SpaceEngineers.Game.ModAPI.Ingame;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // Follower Script Version 1.0
        // ============= Settings ==============
        // The id that the ship should listen to.
        // All commands not prefixed by this will be ignored.
        // Any character is allowed except ;
        const string followerSystemId = "System1";
        const string followerId = "Drone1";
        
        // The position that the ship will take relative to the main ship by default
        // In (X, Y, Z)
        // X: +Right -Left
        // Y: +Up -Down
        // Z: +Backward -Forward
        readonly Vector3D defaultOffset = new Vector3D(50, 0, 0);
        
        // If the script is causing lag try disabling collision avoidance. This is a CPU intensive process!
        // Don't rely on this feature to save your ships! Remember, it is detecting objects between itself and the where
        // it is supposed to be, not objects in its path. Enabling this will cause your ship to be renamed.
        const bool enableCollisionAvoidence = false;

        // The name of the cockpit in the ship. You may leave this blank, but it is highly recommended 
        // to set this field to avoid unexpected behavior related to orientation.
        // If this cockpit is not found, the script will attempt to find a suitable cockpit on its own.
        const string cockpitName = "";

        // This allows you to automatically disable the script when the cockpit is in use.
        const bool autoStop = true;

        // When this is true, opon leaving the cockpit, the script will set the offset to the current position instead 
        // of returning to its designated point. Similar to the starthere command.
        // This only applies if autoStop is true.
        const bool autoStartHere = false;

        // The maximum speed that the ship can go to correct itself.
        // This is relative to the speed of the leader ship. 
        // Example: If leader ship is going 50 m/s, the follower can go 50 m/s + maxSpeed m/s
        const double maxSpeed = 20;

        // This is the frequency that the script is running at. If you are experiencing lag
        // because of this script try decreasing this value. Valid values:
        // Update1 : Runs the script every tick
        // Update10 : Runs the script every 10th tick
        // Update100 : Runs the script every 100th tick
        // If you get odd or unexpected behavior, try setting calculateMissingTicks to true.
        const UpdateFrequency tickSpeed = UpdateFrequency.Update1;

        // When the tick speed of the leader is lower than the tick speed of the follower, this workaround can can be activated.
        // When this is enabled, the script will "guess" what the leader position should be in missing ticks. If the leader gets 
        // damaged and stops, the follower will keep going as if the leader is still there until you use the stop command.
        const bool calculateMissingTicks = true;

        // When calculateMissingTicks is enabled, the maximum number of ticks to estimate before 
        // assuming the leader is no longer active.
        // 1 game tick = 1/60 seconds
        const int maxMissingScriptTicks = 100;
        // =====================================

        // =========== Configurations ==========
        // You can save multiple offsets for your ship using the save, savehere, and load commands. The offsets for saved configurations
        // can be directly edited in the CustomData field of the programmable block. By default, the script will have a single 'default'
        // configuration with the default offset saved. When you make changes to the CustomData directly, you should recompile the script to
        // make the changes appear. CustomData will only be updated by the script when using the save and savehere commands.
        // Warning: Any error in the CustomData will cause the entire script to reset. 
        // Syntax:
        // <name1> <x1> <y1> <z1>
        // <name2> <x2> <y2> <z2>
        // =====================================

        // ============= Commands ==============
        // setoffset;x;y;z : sets the offset variable to this value
        // addoffset;x;y;z : adds these values to the current offset
        // stop : stops the script and releases control to you
        // start : starts the script after a stop command
        // starthere : starts the script in the current position
        // reset : resets and loads the default configuration with the default offset
        // clear : forces the script to forget a previous leader when calculateMissingTicks is true
        // save(;name) : saves the configuration to the current offset
        // savehere(;name) : saves the configuration to the current position
        // load;name : loads the offset from the configuration
        // =====================================

        // You can ignore any unreachable code warnings that appear in this script.

        Dictionary<string, Vector3D> configurations = new Dictionary<string, Vector3D>();
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
        Vector3D offset;
        IMyBroadcastListener leaderListener;
        IMyBroadcastListener commandListener;
        const string transmitTag = "FSLeader" + followerSystemId;
        const string transmitCommandTag = "FSCommand" + followerSystemId;

        readonly int echoFrequency = 100; // every 100th game tick
        int runtime = 0;
        int updated = 0;

        bool prevControl = false;
        
        public Program ()
        {
            // Prioritize the given cockpit name
            rc = GetBlock<IMyShipController>(cockpitName, true);
            if (rc == null) // Second priority cockpit
                rc = GetBlock<IMyCockpit>();
            if (rc == null) // Thrid priority remote control
                rc = GetBlock<IMyRemoteControl>();
            if (rc == null) // No cockpits found.
                throw new Exception("No cockpit/remote control found. Set the cockpitName field in settings.");

            thrust = new ThrusterControl(rc, tickSpeed, GetBlocks<IMyThrust>());
            gyros = new GyroControl(rc, tickSpeed, GetBlocks<IMyGyro>());

            // Set up the default configuration for if loading storage fails.
            configurations ["default"] = defaultOffset;
            currentConfig = "default";
            offset = defaultOffset;

            LoadStorage();

            if (enableCollisionAvoidence)
                cameras = GetBlocks<IMyCameraBlock>();

            if (tickSpeed == UpdateFrequency.Update10)
                echoFrequency = 10;
            else if (tickSpeed == UpdateFrequency.Update100)
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

        public void Save ()
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

        void SaveStorage ()
        {
            // Save values stored in Storage
            Save();

            // Save values stored in CustomData
            /* name1 x y z
             * name2 x y z
             */
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, Vector3D> kv in configurations)
            {
                sb.Append(kv.Key);
                sb.Append(' ');
                sb.Append(kv.Value.X);
                sb.Append(' ');
                sb.Append(kv.Value.Y);
                sb.Append(' ');
                sb.Append(kv.Value.Z);
                sb.Append('\n');
            }
            Me.CustomData = sb.ToString();
        }

        void LoadStorage ()
        {
            if (string.IsNullOrWhiteSpace(Storage) || string.IsNullOrWhiteSpace(Me.CustomData))
            {
                SaveStorage();
                Runtime.UpdateFrequency = tickSpeed;
            }

            try
            {
                // Parse CustomData values
                Dictionary<string, Vector3D> loadedConfig = new Dictionary<string, Vector3D>
                {
                    ["default"] = defaultOffset // Ensure that default offset always exists
                };
                string [] config = Me.CustomData.Split('\n');
                foreach (string s in config)
                {
                    if (string.IsNullOrWhiteSpace(s))
                        continue; // Ignore blank lines
                    
                    string [] configValues = s.Split(' ');
                    Vector3D value = new Vector3D(
                        double.Parse(configValues [1]),
                        double.Parse(configValues [2]),
                        double.Parse(configValues [3])
                        );
                    loadedConfig [configValues [0]] = value;
                }

                // Parse Storage values
                string [] args = Storage.Split(';');
                bool loadedIsDisabled = args [0] == "1";
                string loadedCurrentConfig = args [1];
                Vector3D loadedOffset = new Vector3D(
                    double.Parse(args [2]),
                    double.Parse(args [3]),
                    double.Parse(args [4])
                    );

                // Parse succesful, update the real values.
                configurations = loadedConfig;
                currentConfig = loadedCurrentConfig;
                if (configurations.ContainsKey(currentConfig))
                    currentConfig = "default"; // If something went wrong, use the only guaranteed configuration.
                offset = loadedOffset;
                isDisabled = loadedIsDisabled;
                if (!isDisabled)
                    Runtime.UpdateFrequency = tickSpeed;
            } 
            catch (Exception)
            {
                SaveStorage();
                Runtime.UpdateFrequency = tickSpeed;
                //throw;
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

                if (autoStop)
                {
                    bool control = rc.IsUnderControl;
                    if (control != prevControl)
                    {
                        if (control)
                            ResetMovement();
                        else if (autoStartHere)
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
                                if (s == followerId)
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
            else if (updateSource != UpdateType.Antenna)
            {
                RemoteCommand(argument);
            }
        }

        void WriteEcho ()
        {
            Echo("Running.\nConfigs:");
            foreach (string s in configurations.Keys)
            {
                if (s == currentConfig)
                    Echo(s + '*');
                else
                    Echo(s);
            }
            Echo(offset.ToString("0.00"));
            if(leaderMatrix == MatrixD.Zero)
                Echo("No messages received.");
            else if (calculateMissingTicks && runtime - updated > maxMissingScriptTicks)
                Echo($"Weak signal, message receved {runtime - updated} ticks ago.");
            if (autoStop && prevControl)
                Echo("Cockpit is under control.");
            if (obstacleOffset.HasValue)
                Echo("Obstacle Detected! Stopping the ship.");

        }


        void Move ()
        {
            gyros.FaceVectors(leaderMatrix.Forward, leaderMatrix.Up);

            // Apply translations to find the world position that this follower is supposed to be
            Vector3D targetPosition = Vector3D.Transform(offset, leaderMatrix);

            if (calculateMissingTicks)
            {
                int diff = Math.Min(Math.Abs(runtime - updated), maxMissingScriptTicks);
                if (diff > 0)
                {
                    double secPerTick = 1.0 / 60;
                    if (tickSpeed == UpdateFrequency.Update10)
                        secPerTick = 1.0 / 6;
                    else if (tickSpeed == UpdateFrequency.Update100)
                        secPerTick = 5.0 / 3;
                    double secPassed = diff * secPerTick;
                    targetPosition += leaderVelocity * secPassed;
                }
            }

            if (enableCollisionAvoidence)
            {
                CheckForCollistions(targetPosition);
                if (obstacleOffset.HasValue)
                {
                    thrust.ApplyAccel(thrust.ControlPosition(Vector3D.Transform(obstacleOffset.Value, leaderMatrix), leaderVelocity, maxSpeed));
                    return;
                }
            }

            thrust.ApplyAccel(thrust.ControlPosition(targetPosition, leaderVelocity, maxSpeed));
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
                    Runtime.UpdateFrequency = tickSpeed;
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "starthere": // starthere
                    Runtime.UpdateFrequency = tickSpeed;
                    offset = CurrentOffset();
                    isDisabled = false;
                    WriteEcho();
                    break;
                case "reset": // reset
                    offset = defaultOffset;
                    configurations ["default"] = defaultOffset;
                    currentConfig = "default";
                    SaveStorage();
                    WriteEcho();
                    break;
                case "save": // save(;name)
                    {
                        string key = currentConfig;
                        if (args.Length > 1)
                            key = args [1];

                        if (key.Contains(' '))
                            return;

                        configurations [key] = offset;
                        SaveStorage();
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
                        configurations [key] = newOffset;
                        SaveStorage();
                    }
                    break;
                case "load": // load;name
                    if (args.Length == 1 || !configurations.ContainsKey(args [1]))
                        return;
                    // Load the new config
                    offset = configurations [args [1]];
                    currentConfig = args [1];
                    isDisabled = false;
                    WriteEcho();
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
#pragma warning restore CS0162 // Unreachable code detected


    }
}