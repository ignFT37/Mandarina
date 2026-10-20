// Mandarina: what the two transcription panels share: the file's name, what the tool is doing and which model runs where. The
// model follows the device: the large one on a dedicated graphics card, the small one on the processor (WhisperModels.For). The
// device is found by starting a model for a moment.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using Filee.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Filee.App.Mandarina.Tools;

public abstract partial class TranscribePanelBase : ToolPanel
{
    private readonly CancellationTokenSource _loading = new();

    /// <summary>Where the model runs, once found (null until then, and when it could not be found).</summary>
    private WhisperDevice? _device;

    /// <summary>What the tool is doing while it works: "Transcribing «clip.mp4»…".</summary>
    [ObservableProperty] private string _phase = "";

    /// <summary>Which model runs where ("Using the large model on the graphics card: NVIDIA GeForce RTX 4060"), once it is known.</summary>
    [ObservableProperty] private string _deviceText = "";

    /// <summary>A graphics card with memory of its own can run the model: the choice to use it means something.</summary>
    [ObservableProperty] private bool _hasGpu;

    protected TranscribePanelBase(PanelContext context) : base(context)
    {
        _ = LoadDeviceAsync();
    }

    /// <summary>The file's name, or "3 files".</summary>
    public string FileName => Files.Count == 1 ? Path.GetFileName(Files[0]) : Context.Format("panel.transcribe.n_files", Files.Count);

    /// <summary>A speech model is installed, whichever (without one nothing can run).</summary>
    public bool HasModel => WhisperModel.Installed().Any;

    /// <summary>What is missing, in words, or null.</summary>
    public string? MissingText => HasModel ? null : Context.Format("engine.reason.needs_package", "Whisper");

    /// <summary>The run will use the dedicated graphics card when there is one (SUBTITLES lets the user turn that off).</summary>
    protected virtual bool WantsGpu => true;

    private async Task LoadDeviceAsync()
    {
        try
        {
            if (WhisperEngine.Locate(Context.Media, Context.Log) is not { } engine)
                return;
            DeviceText = Context["panel.transcribe.device_checking"];
            ShowDeviceFor(await engine.DeviceAsync(_loading.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget, and only a courtesy: the line just stays empty; the tool itself reports what is wrong.
            DeviceText = "";
            Context.Log.LogWarning(ex, "Could not find where Whisper runs");
        }
    }

    /// <summary>The device is known (tests give one instead of starting a model): remembers it and says which model runs on it.</summary>
    internal void ShowDeviceFor(WhisperDevice device)
    {
        _device = device;
        HasGpu = device.Gpu is not null;
        ShowDevice();
    }

    /// <summary>Says which model runs where; again when an option changes the device (the graphics card switched off).</summary>
    protected void ShowDevice()
    {
        if (_device is { } device)
            DeviceText = DeviceLine(Context.Loc, WhisperModel.Installed(), device, WantsGpu);
    }

    /// <summary>
    /// "Using the large model on the graphics card: NVIDIA GeForce RTX 4060", "Using the small model on the processor"…: the model
    /// <paramref name="models"/> gives for the device a run would use (the card only when <paramref name="wantsGpu"/>) and that
    /// device. Empty when no model is installed. On the processor with only the large model, it says the small one is faster there.
    /// </summary>
    internal static string DeviceLine(ILocalizer loc, WhisperModels models, WhisperDevice device, bool wantsGpu)
    {
        var onCard = wantsGpu && device.Gpu is not null;
        if (models.For(onCard) is not { } model)
            return "";
        var key = (model.Size, onCard) switch
        {
            (WhisperSize.Large, true) => "panel.transcribe.device_large_gpu",
            (WhisperSize.Small, true) => "panel.transcribe.device_small_gpu",
            (WhisperSize.Large, false) => "panel.transcribe.device_large_cpu",
            _ => "panel.transcribe.device_small_cpu",
        };
        return loc.Format(key, device.Name);
    }

    public override void Dispose()
    {
        _loading.Cancel();
        base.Dispose();
    }
}
