using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VRageMath;


namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;
    private int puffBudget = 2200;
    private float darkness = 1f;
    private float cloudScale = 1f;
    private bool lightning = true;
    private Color lightningColor = new Color(197, 122, 250);
    private Color flashColor = new Color(200, 150, 240);
    private bool cameraOverlay = true;
    private bool hideArrivingGrids = true;
    private float maxDistanceKm = 15f;
    private float musicDuringJump = 0.2f;

    #endregion

    #region User interface

    public readonly string Title = Plugin.Title;

    [Separator("Jump effect")]

    [Checkbox(label: "Enabled", description: "Replaces the vanilla jump drive effect with the black smoke cloud.")]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Slider(200f, 3000f, 100f, SliderAttribute.SliderType.Integer, label: "Smoke billboards", description: "How many smoke billboards make up one cloud.\nLower it if the effect is slow on your GPU.")]
    public int PuffBudget
    {
        get => puffBudget;
        set => SetField(ref puffBudget, value);
    }

    [Slider(0.3f, 1.5f, 0.05f, SliderAttribute.SliderType.Float, label: "Smoke opacity", description: "How thick and dark the smoke looks.")]
    public float Darkness
    {
        get => darkness;
        set => SetField(ref darkness, value);
    }

    [Slider(0.5f, 4f, 0.1f, SliderAttribute.SliderType.Float, label: "Cloud size", description: "Sets the size of the clouds.\nBigger clouds can be seen from farther away.")]
    public float CloudScale
    {
        get => cloudScale;
        set => SetField(ref cloudScale, value);
    }

    [Checkbox(label: "Lightning", description: "Shows lightning inside the cloud.")]
    public bool Lightning
    {
        get => lightning;
        set => SetField(ref lightning, value);
    }

    [Color(label: "Lightning color", description: "Sets the color of the lightning bolts and sparks.")]
    public Color LightningColor
    {
        get => lightningColor;
        set => SetField(ref lightningColor, value);
    }

    [Color(label: "Flash color", description: "Sets the color of the flashes that light up the smoke.\nThe glowing parts of the smoke take this color too.")]
    public Color FlashColor
    {
        get => flashColor;
        set => SetField(ref flashColor, value);
    }

    [Checkbox(label: "Camera smoke", description: "Wraps your third person camera in a smoke gate while your ship jumps.\nA camera block on the hull sees fog instead.\nNothing outside the smoke shows through either of them.")]
    public bool CameraOverlay
    {
        get => cameraOverlay;
        set => SetField(ref cameraOverlay, value);
    }

    [Checkbox(label: "Hide arriving ships", description: "Hides a ship jumping in near you until its smoke cloud has formed.")]
    public bool HideArrivingGrids
    {
        get => hideArrivingGrids;
        set => SetField(ref hideArrivingGrids, value);
    }

    [Slider(1f, 50f, 1f, SliderAttribute.SliderType.Float, label: "Draw distance (km)", description: "Clouds farther away than this are not drawn.")]
    public float MaxDistanceKm
    {
        get => maxDistanceKm;
        set => SetField(ref maxDistanceKm, value);
    }

    [Separator("Sound")]

    [Slider(0f, 1f, 0.05f, SliderAttribute.SliderType.Float, label: "Music during jumps", description: "How loud the game's music plays while a jump can be heard, as a share of its normal volume.\nLower it to hear the gate better.")]
    public float MusicDuringJump
    {
        get => musicDuringJump;
        set => SetField(ref musicDuringJump, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
