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

    public readonly string Title = "Black Smoke Jump";

    [Separator("Jump effect")]

    [Checkbox(description: "Replace the vanilla jump drive effect with the black smoke cloud")]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Slider(200f, 3000f, 100f, SliderAttribute.SliderType.Integer, description: "Smoke billboards per cloud, lower it on a slow GPU")]
    public int PuffBudget
    {
        get => puffBudget;
        set => SetField(ref puffBudget, value);
    }

    [Slider(0.3f, 1.5f, 0.05f, SliderAttribute.SliderType.Float, description: "Opacity of the smoke")]
    public float Darkness
    {
        get => darkness;
        set => SetField(ref darkness, value);
    }

    [Slider(0.5f, 4f, 0.1f, SliderAttribute.SliderType.Float, description: "Size of the clouds: bigger clouds can be seen from farther away")]
    public float CloudScale
    {
        get => cloudScale;
        set => SetField(ref cloudScale, value);
    }

    [Checkbox(description: "Lightning inside the cloud")]
    public bool Lightning
    {
        get => lightning;
        set => SetField(ref lightning, value);
    }

    [Color(description: "Color of the lightning bolts and sparks")]
    public Color LightningColor
    {
        get => lightningColor;
        set => SetField(ref lightningColor, value);
    }

    [Color(description: "Color of the flashes lighting up the smoke and of its glowing regions")]
    public Color FlashColor
    {
        get => flashColor;
        set => SetField(ref flashColor, value);
    }

    [Checkbox(description: "Give your third person camera a smoke gate (a camera block on the hull, fog) while your ship jumps, so nothing outside the smoke shows")]
    public bool CameraOverlay
    {
        get => cameraOverlay;
        set => SetField(ref cameraOverlay, value);
    }

    [Checkbox(description: "Hide a ship jumping in near you until its smoke cloud has formed")]
    public bool HideArrivingGrids
    {
        get => hideArrivingGrids;
        set => SetField(ref hideArrivingGrids, value);
    }

    [Slider(1f, 50f, 1f, SliderAttribute.SliderType.Float, description: "Clouds farther than this (km) are not drawn")]
    public float MaxDistanceKm
    {
        get => maxDistanceKm;
        set => SetField(ref maxDistanceKm, value);
    }

    [Separator("Sound")]

    [Slider(0f, 1f, 0.05f, SliderAttribute.SliderType.Float, description: "The game's music while a jump is heard, as a share of its volume: lower it to hear the gate better")]
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
