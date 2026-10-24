using System.Linq;
using System.Reflection;
using ClientPlugin.Effects;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif
    
namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "BlackSmoke";
    public static Plugin Instance { get; private set; }
    private SettingsGenerator settingsGenerator;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Instance = this;
        Instance.settingsGenerator = new SettingsGenerator();

        // Class by class, so one patch that fails does not leave the rest out, and the game's log lists what is patched
        var harmony = new Harmony(Name);
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (System.Exception e)
            {
                Log.Info($"Patch of {type.Name} failed: {e.GetBaseException().Message}");
            }
        }

        Log.Info($"Patched: {string.Join(", ", harmony.GetPatchedMethods().Select(m => $"{m.DeclaringType?.Name}.{m.Name}"))}");
    }

    /// <summary>Called by Pulsar with the plugin's asset folder (the AssetFolder asset of BlackSmoke.xml)</summary>
    // ReSharper disable once UnusedMember.Global
    public void LoadAssets(string folder)
    {
        JumpSounds.AssetFolder = folder;
        Log.Info($"Assets: {folder}");
    }

    public void Dispose()
    {
        // Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        Instance = null;
    }

    public void Update()
    {
        try
        {
            JumpEffects.Update();
        }
        catch (System.Exception e)
        {
            Log.Error(e);
        }
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        Instance.settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(Instance.settingsGenerator.Dialog);
    }

}