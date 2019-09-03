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
using VRage;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        // Leader Script Version 1.0
        // ============= Settings ==============
        // The id of the system that the followers are listening on.
        // Any character is allowed except ;
        const string followerSystem = "System1";

        // This is the frequency that the script is running at. If you are experiencing lag
        // because of this script try decreasing this value. Valid values:
        // Update1 : Runs the script every tick
        // Update10 : Runs the script every 10th tick
        // Update100 : Runs the script every 100th tick
        // Note due to limitations, the script will only run every 10th tick max when not using itself.
        const UpdateFrequency tickSpeed = UpdateFrequency.Update1;

        // The name of the sensor group that the script will use to keep track of the leader ship. 
        // Leave blank to use all connected sensors.
        const string sensorGroup = "";

        // The distance that the ship will scan while using the scan command.
        const double scanDistance = 1000;

        // The name of the cockpit in the ship. You may leave this blank, but it is highly recommended 
        // to set this field to avoid unexpected behavior related to orientation.
        // If this cockpit is not found, the script will attempt to find a suitable cockpit on its own.
        const string cockpitName = "";

        // When true, the script will attempt to reconnect to the previous leader on start.
        // This functions in a similar way to the find command if an exact match is not found.
        const bool attemptReconnection = true;

        // When true, the script will broadcast its own location when no other leader is specified.
        const bool allowFollowSelf = true;

        // When true, the script will use the cameras to try to keep a lock on the designated leader.
        // This is a CPU intensive process.
        const bool activeRaycasting = false;

        // When true, the script will align the followers with gravity.
        // Recomended for rovers.
        const bool alignFollowersToGravity = false;
        // =====================================

        // ============= Commands ==============
        // <id> : either blank (to send to all) or the shipId on the follower 
        // <id>:setoffset;x;y;z : sets the offset variable to this value on the follower 
        // <id>:addoffset;x;y;z : adds these values to the current offset on the follower 
        // <id>:stop : stops the followers and releases control to you
        // <id>:start : starts the followers after a stop command
        // <id>:starthere : starts the followers in the current position
        // <id>:reset : resets the followers to use the default offset
        // <id>:clear : forces the script to forget a previous leader when calculateMissingTicks is true
        // <id>:save(;name) : saves the configuration on the follower to the current offset
        // <id>:savehere(;name) : saves the configuration on the follower to the current position
        // <id>:load;name : loads the offset from the configuration on the follower 
        //
        // scan : scans forward using cameras to set which ship is the leader ship
        // find;name : scans the sensors using ship name to set which ship is the leader ship
        // stop : stops the script from broadcasting leader data
        // start : resumes broadcasting data after a stop command
        // reset : sets the leader ship to be the current ship (if allowFollowSelf is enabled)
        // =====================================

        // You can ignore any unreachable code warnings that appear in this script.

        IMyShipController rc;
        const string transmitTag = "FSLeader" + followerSystem;
        const string transmitCommandTag = "FSCommand" + followerSystem;
        List<IMySensorBlock> sensors = new List<IMySensorBlock>();
        List<IMyCameraBlock> forwardCameras = new List<IMyCameraBlock>();
        long target = 0;
        string targetName = null;
        readonly int echoFrequency = 100; // every 100th game tick
        int runtime = 0;
        bool isDisabled = false;
        const bool debug = false;

        // Used for active raycasting.
        List<IMyCameraBlock> allCameras;
        MyDetectedEntityInfo previousHit = new MyDetectedEntityInfo();
        int hitRuntime = 0;

        public Program ()
        {
            if (string.IsNullOrWhiteSpace(sensorGroup))
                sensors = GetBlocks<IMySensorBlock>();
            else
                sensors = GetBlocks<IMySensorBlock>(sensorGroup, true);

            // Prioritize the given cockpit name
            rc = GetBlock<IMyShipController>(cockpitName, true);
            if (rc == null) // Second priority cockpit
                rc = GetBlock<IMyCockpit>();
            if (rc == null) // Thrid priority remote control
                rc = GetBlock<IMyRemoteControl>();
            if (rc == null) // No cockpits found.
                throw new Exception("No cockpit/remote control found. Set the cockpitName field in settings.");

            if (activeRaycasting)
                allCameras = new List<IMyCameraBlock>();
            foreach (IMyCameraBlock c in GetBlocks<IMyCameraBlock>())
            {
                if (EqualsPrecision(Vector3D.Dot(rc.WorldMatrix.Forward, c.WorldMatrix.Forward), 1, 0.01))
                {
                    forwardCameras.Add(c);
                    c.EnableRaycast = true;
                }
                if(activeRaycasting)
                    allCameras.Add(c);
            }

            LoadStorage();

            if (tickSpeed == UpdateFrequency.Update10)
                echoFrequency = 10;
            else if (tickSpeed == UpdateFrequency.Update100)
                echoFrequency = 1;

            if(!isDisabled)
                Runtime.UpdateFrequency = tickSpeed;
            Echo("Running.");
        }

        public void Save ()
        {
            char disabled = '0';
            if (isDisabled)
                disabled = '1';
            if (targetName == null)
                Storage = disabled.ToString();
            else
                Storage = $"{disabled};{target};{targetName}";
        }

        public void LoadStorage ()
        {
            if (string.IsNullOrWhiteSpace(Storage))
            {
                Save();
                return;
            }

            try
            {
                string [] args = Storage.Split(new [] { ';' }, 3);
                bool wasDisabled = args [0] == "1";
                if (args.Length == 1)
                {
                    this.target = 0;
                    this.targetName = null;
                    this.isDisabled = wasDisabled;
                    return;
                }

                long target = long.Parse(args [1]);
                string targetName = args [2];
                if (!attemptReconnection)
                {
                    this.target = target;
                    this.targetName = targetName;
                    this.isDisabled = wasDisabled;
                    return;
                }

                // Verify that the target exists
                bool done = false;
                MyDetectedEntityInfo? lastResult = null;
                foreach (IMySensorBlock s in sensors)
                {
                    if (done)
                        break;

                    List<MyDetectedEntityInfo> entites = new List<MyDetectedEntityInfo>();
                    s.DetectedEntities(entites);
                    foreach (MyDetectedEntityInfo i in entites)
                    {
                        if (target != 0 && i.EntityId == target)
                        {
                            // Found an exact match.
                            done = true;
                            lastResult = i;
                            break;
                        }

                        if (i.Name == targetName)
                        {
                            // Found a partial match
                            lastResult = i;
                        }
                    }

                    if (lastResult.HasValue)
                    {
                        // Target exists, update the info.
                        this.target = lastResult.Value.EntityId;
                        this.targetName = lastResult.Value.Name;
                        this.isDisabled = wasDisabled;
                        Save();
                    }
                    else
                    {
                        // Could not find target, use find mode.
                        this.target = 0;
                        this.targetName = targetName;
                        this.isDisabled = wasDisabled;
                        Save();
                    }
                }

            }
            catch
            {
                Save();
            }
        }

        public void Main (string argument, UpdateType updateSource)
        {
            if (updateSource == UpdateType.Update100 || updateSource == UpdateType.Update10 || updateSource == UpdateType.Update1)
            {
                if (debug || runtime % echoFrequency == 0)
                    WriteEcho();
                runtime++;

                if (target != 0 || targetName != null)
                {
                    MyDetectedEntityInfo? info = null;
                    foreach (IMySensorBlock s in sensors)
                    {
                        if (info.HasValue)
                            break;

                        List<MyDetectedEntityInfo> entites = new List<MyDetectedEntityInfo>();
                        s.DetectedEntities(entites);
                        foreach (MyDetectedEntityInfo i in entites)
                        {
                            if (target != 0)
                            {
                                // target id exists
                                if (i.EntityId == target)
                                {
                                    info = i;
                                    break;
                                }
                            }
                            else if(targetName != null)
                            {
                                // target id does not exist
                                if (i.Name == targetName)
                                {
                                    info = i;
                                    target = i.EntityId;
                                    Save(); // Update save data with the id
                                    break;
                                }
                            }
                        }

                    }

                    if (activeRaycasting && target != 0 && !previousHit.IsEmpty() && !info.HasValue)
                    {
                        float sec = Math.Abs(runtime - hitRuntime);
                        if (Runtime.UpdateFrequency == UpdateFrequency.Update10)
                            sec *= 1f / 6;
                        else if (Runtime.UpdateFrequency == UpdateFrequency.Update100)
                            sec *= 5f / 3;
                        else
                            sec *= 1f / 60;

                        Vector3D prediction = previousHit.Position + previousHit.Velocity * sec;
                        Me.CustomData = new MyWaypointInfo("Test", prediction).ToString();
                        foreach (IMyCameraBlock c in allCameras)
                        {
                            if (!c.EnableRaycast)
                                c.EnableRaycast = true;
                            if (c.CanScan(prediction))
                            {
                                MyDetectedEntityInfo result = c.Raycast(prediction);
                                if (result.EntityId == target)
                                    info = result;
                                break;
                            }
                        }
                    }

                    if (info.HasValue)
                    {
                        if (activeRaycasting)
                        {
                            previousHit = info.Value;
                            hitRuntime = runtime;
                        }
                        Vector3D up = info.Value.Orientation.Up;
                        if (alignFollowersToGravity)
                        {
                            Vector3D gravity = rc.GetNaturalGravity();
                            if (gravity != Vector3D.Zero)
                                up = -Vector3D.Normalize(gravity);
                        }
                        MatrixD worldMatrix = MatrixD.CreateWorld(info.Value.Position, info.Value.Orientation.Forward, up);
                        IGC.SendBroadcastMessage<MyTuple<MatrixD, Vector3D, long>>(transmitTag, new MyTuple<MatrixD, Vector3D, long>(worldMatrix, info.Value.Velocity, info.Value.EntityId));
                    }
                }
                else if(allowFollowSelf)
                {
                    MatrixD matrix = rc.WorldMatrix;
                    if (alignFollowersToGravity)
                    {
                        Vector3D gravity = rc.GetNaturalGravity();
                        if (gravity != Vector3D.Zero)
                        {
                            Vector3D up = -Vector3D.Normalize(gravity);
                            Vector3D right = matrix.Forward.Cross(up);
                            Vector3D forward = up.Cross(right);
                            matrix.Up = up;
                            matrix.Right = right;
                            matrix.Forward = forward;
                        }
                    }
                    IGC.SendBroadcastMessage<MyTuple<MatrixD, Vector3D, long>>(transmitTag, new MyTuple<MatrixD, Vector3D, long>(matrix, rc.GetShipVelocities().LinearVelocity, Me.CubeGrid.EntityId));
                }
            }
            else if (updateSource != UpdateType.Antenna)
            {
                ProcessCommand(argument);
            }
        }

        void WriteEcho ()
        {
            if (targetName != null)
            {
                if (target == 0)
                    Echo($"Running.\nSearching for {targetName}...");
                else
                    Echo($"Running.\nFollowing {targetName}");
            }
            else if (allowFollowSelf)
            {
                Echo("Running.\nFollowing me.");
            }
            else
            {
                Echo("Idle.");
            }
        }

        void ProcessCommand (string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
                return;
            
            // Split to find id
            string [] args = argument.Split(new [] { ':' }, 2);

            // Find the index of the command
            if (args.Length > 1)
            {
                TransmitCommand(args [0], args [1]);
            }
            else // Command does not have a follower target, try a command on this script before broadcasting.
            {
                SelfCommand(args [0]);
            }

        }

        void TransmitCommand (string target, string data, TransmissionDistance distance = TransmissionDistance.AntennaRelay)
        {
            MyTuple<string, string> msg = new MyTuple<string, string>(target, data);
            IGC.SendBroadcastMessage<MyTuple<string, string>>(transmitCommandTag, msg, distance);
        }

        void SelfCommand (string argument)
        {
            string [] cmdArgs = argument.Split(';');
            switch (cmdArgs [0])
            {
                case "stop":
                    isDisabled = true;
                    Runtime.UpdateFrequency = UpdateFrequency.None;
                    break;
                case "start":
                    isDisabled = false;
                    Runtime.UpdateFrequency = tickSpeed;
                    break;
                case "reset":
                    isDisabled = false;
                    Runtime.UpdateFrequency = tickSpeed;

                    target = 0;
                    targetName = null;
                    WriteEcho();
                    break;
                case "scan":
                    isDisabled = false;
                    Runtime.UpdateFrequency = tickSpeed;

                    MyDetectedEntityInfo? info = Raycast();
                    if (info.HasValue)
                    {
                        target = info.Value.EntityId;
                        targetName = info.Value.Name;
                        if (tickSpeed == UpdateFrequency.Update1)
                            Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    }
                    WriteEcho();
                    break;
                case "find": // find;name
                    isDisabled = false;
                    Runtime.UpdateFrequency = tickSpeed;

                    target = 0;
                    targetName = cmdArgs [1];
                    if (tickSpeed == UpdateFrequency.Update1)
                        Runtime.UpdateFrequency = UpdateFrequency.Update10;
                    WriteEcho();
                    break;
                default:
                    TransmitCommand("", argument);
                    return;
            }
            Save();
        }

        MyDetectedEntityInfo? Raycast ()
        {
            foreach (IMyCameraBlock c in forwardCameras)
            {
                if (c.CanScan(scanDistance))
                {
                    MyDetectedEntityInfo info = c.Raycast(scanDistance);
                    if (info.IsEmpty())
                        return null;
                    return info;
                }
            }
            return null;
        }

        bool EqualsPrecision (double d1, double d2, double precision)
        {
            return Math.Abs(d1 - d2) <= precision;
        }
#pragma warning restore CS0162 // Unreachable code detected


    }
}