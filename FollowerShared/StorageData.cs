using System.Linq;
using System.Text;
using System;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        public class StorageData
        {
            private readonly Program prg;
            private readonly StringBuilder sb = new StringBuilder();

            public StorageData(Program prg)
            {
                this.prg = prg;
            }

            private Vector3D offset = new Vector3D();
            public Vector3D Offset
            {
                get
                {
                    return offset;
                }
                set
                {
                    offset = value;
                    if (AutoSave)
                        Save();
                }
            }

            private bool isDisabled = false;
            public bool IsDisabled
            {
                get
                {
                    return isDisabled;
                }
                set
                {
                    if (isDisabled != value)
                    {
                        isDisabled = value;
                        if (AutoSave)
                            Save();
                    }
                }
            }

            private string currentConfig = "default";
            public string CurrentConfig
            {
                get
                {
                    return currentConfig;
                }
                set
                {
                    currentConfig = value;
                    if (AutoSave)
                        Save();
                }
            }

            public bool AutoSave { get; set; } = true;

            public void Save()
            {
                // isDisabled;currentConfig;x;y;z
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
                prg.Storage = sb.ToString();
                sb.Clear();
                AutoSave = true;
            }

            public void Load()
            {
                string storage = prg.Storage;
                Settings settings = prg.settings;

                if (string.IsNullOrWhiteSpace(prg.Storage))
                {
                    Save();
                    if (!settings.configs.Value.ContainsKey(currentConfig))
                        currentConfig = settings.configs.Value.First().Key;
                    return;
                }

                try
                {
                    // Parse Storage values
                    string[] args = prg.Storage.Split(';');
                    bool loadedIsDisabled = args[0] == "1";
                    string loadedCurrentConfig = args[1];
                    Vector3D loadedOffset = new Vector3D(
                        double.Parse(args[2]),
                        double.Parse(args[3]),
                        double.Parse(args[4])
                        );

                    if (settings.configs.Value.ContainsKey(loadedCurrentConfig))
                        currentConfig = loadedCurrentConfig;
                    else
                        currentConfig = settings.configs.Value.First().Key;
                    offset = loadedOffset;
                    isDisabled = loadedIsDisabled;
                }
                catch (Exception)
                {
                    Save();
                }
            }

        }
    }
}