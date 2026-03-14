using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TerraVision;

public class TerraVision : Mod
{
    public static TerraVision instance;
    public static LibVLC LibVLCInstance { get; private set; }
    private static bool _coreInitialized = false;

    public override void Load()
    {
        instance = this;

        if (!Main.dedServ)
            SetupLibVLC();
    }

    public override void Unload()
    {
        LibVLCInstance?.Dispose();
        LibVLCInstance = null;
        _coreInitialized = false;

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TerrariaVideos", Name);
        if (System.IO.Directory.Exists(tempDir))
        {
            try
            {
                System.IO.Directory.Delete(tempDir, true);
            }
            catch { /* Ignore cleanup errors */ }
        }

        instance = null;
    }

    private static void SetupLibVLC()
    {
        if (_coreInitialized)
            return;

        string vlcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                      "My Games", "Terraria", "tModLoader", "VideoPlayerLibs");

        try
        {
            LibVLCSharp.Shared.Core.Initialize(vlcPath);
            _coreInitialized = true;

            string[] args = new string[]
            {
                    "--no-quiet", // Keep LibVLC logs for now, helpful for debugging
                    "--network-caching=1000",
                    "--file-caching=1000",
                    "--avcodec-fast",
                    "--aout=directsound,waveout,mmdevice",
            };

            LibVLCInstance = new LibVLC(args);

            instance.Logger.Info("LibVLC initialized successfully!");
            instance.Logger.Info($"LibVLC version: {LibVLCInstance.Version}");

            LogAudioCapabilities();
        }
        catch (Exception ex)
        {
            instance.Logger.Error($"Failed to initialize LibVLC: {ex.Message}");
            instance.Logger.Error($"Stack trace: {ex.StackTrace}");
        }
    }

    private static void LogAudioCapabilities()
    {
        try
        {
            var audioOutputs = LibVLCInstance.AudioOutputs;
            instance.Logger.Info($"Available audio outputs: {audioOutputs.Length}");

            foreach (var output in audioOutputs)
            {
                instance.Logger.Info($"  - {output.Name}");
                instance.Logger.Info($"    Description: {output.Description}");

            }
        }
        catch (Exception ex)
        {
            instance.Logger.Warn($"Could not log audio capabilities: {ex.Message}");
        }
    }
}
