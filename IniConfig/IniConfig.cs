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
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game;
using VRage;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        public abstract class IniValue<T>
        {
            private readonly string comment;
            protected readonly MyIniKey key;
            public IniValue(string section, string key, T defaultValue = default(T), string comment = null)
            {
                this.key = new MyIniKey(section, key);
                value = defaultValue;
                this.comment = comment;
            }

            protected T value;
            public T Value 
            { 
                get
                {
                    return value;
                }
            }

            public void Load (MyIni ini)
            {
                if (!TryGetValue(ini.Get(key), out value))
                    throw new IniMissingException(key.Name);
            }

            public void Save (MyIni ini)
            {
                Set(ini);
                ini.SetComment(key, comment);
            }

            protected abstract void Set(MyIni ini);

            protected abstract bool TryGetValue (MyIniValue storage, out T value);

            public override string ToString ()
            {
                return value.ToString();
            }

            public override bool Equals (object obj)
            {
                IniValue<T> value = obj as IniValue<T>;
                return value != null &&
                       EqualityComparer<T>.Default.Equals(this.value, value.value);
            }

            public override int GetHashCode ()
            {
                return -1584136870 + EqualityComparer<T>.Default.GetHashCode(value);
            }
        }

        public class IniValueBool : IniValue<bool>
        {
            public IniValueBool (string section, string key, bool defaultValue = false, string comment = null) : base(section, key, defaultValue, comment) { }
            protected override void Set (MyIni ini)
            {
                ini.Set(key, value);
            }
            protected override bool TryGetValue (MyIniValue storage, out bool value)
            {
                return storage.TryGetBoolean(out value);
            }
        }

        public class IniValueString : IniValue<string>
        {
            public IniValueString (string section, string key, string defaultValue = null, string comment = null) : base(section, key, defaultValue, comment) { }
            protected override void Set (MyIni ini)
            {
                ini.Set(key, value);
            }
            protected override bool TryGetValue (MyIniValue storage, out string value)
            {
                return storage.TryGetString(out value);
            }
        }

        public class IniValueVector3D : IniValue<Vector3D>
        {
            public IniValueVector3D (string section, string key, Vector3D defaultValue = default(Vector3D), string comment = null) : base(section, key, defaultValue, comment) { }
            protected override void Set (MyIni ini)
            {
                ini.Set(key, $"{value.X} {value.Y} {value.Z}");
            }
            protected override bool TryGetValue (MyIniValue storage, out Vector3D value)
            {
                value = new Vector3D();
                string temp;
                if (!storage.TryGetString(out temp))
                    return false;
                string [] args = temp.Split(new [] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length != 3)
                    return false;
                if (!double.TryParse(args [0], out value.X))
                    return false;
                if (!double.TryParse(args [1], out value.Y))
                    return false;
                return double.TryParse(args [2], out value.Z);
            }
        }

        public class IniValueEnum<T> : IniValue<T> where T : struct, IComparable
        {
            public IniValueEnum (string section, string key, T defaultValue = default(T), string comment = null) : base(section, key, defaultValue, comment) { }
            protected override void Set (MyIni ini)
            {
                ini.Set(key, value.ToString());
            }

            protected override bool TryGetValue (MyIniValue storage, out T value)
            {
                value = default(T);
                string temp;
                if (!storage.TryGetString(out temp))
                    return false;
                return Enum.TryParse<T>(temp, out value);
            }
        }

        public class IniValueInt : IniValue<int>
        {
            public IniValueInt (string section, string key, int defaultValue = 0, string comment = null) : base(section, key, defaultValue, comment) { }
            protected override void Set (MyIni ini)
            {
                ini.Set(key, value);
            }
            protected override bool TryGetValue (MyIniValue storage, out int value)
            {
                return storage.TryGetInt32(out value);
            }
        }

        public class IniValueDouble : IniValue<double>
        {
            public IniValueDouble (string section, string key, double defaultValue = 0, string comment = null) : base(section, key, defaultValue, comment) { }

            protected override void Set (MyIni ini)
            {
                ini.Set(key, value);
            }

            protected override bool TryGetValue (MyIniValue storage, out double value)
            {
                return storage.TryGetDouble(out value);
            }
        }

        public class IniMissingException : IniParseException
        {
            public IniMissingException (string id)
                : base("Value for '" + id + "' is missing or invalid")
            {
            }

            public IniMissingException (string id, Exception innerException)
                : base("Value for '" + id + "' is missing or invalid", innerException)
            {
            }
        }

        public class IniParseException : Exception
        {
            private const string error = "Failed to parse CustomData settings.\n";
            public IniParseException () : base(error)
            {
            }

            public IniParseException (string message) : base(error + message)
            {
            }

            public IniParseException (string message, Exception innerException) : base(error + message, innerException)
            {
            }
        }
    }
}
