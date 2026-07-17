using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace BeamForge.App;

public partial class MainWindow : Window
{
    private readonly SimulatedDmxOutput _simulator = new();
    private readonly AudioCaptureService _audio = new();
    private readonly SafetyController _safety;
    private readonly AutoShowEngine _autoShow;
    private FixtureProfile _profile = FixtureProfile.CreateSimulator();
    private ShowProject _project;
    private LaserAnimation? _animation;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _uiReady;

    public ObservableCollection<MixerChannel> MixerChannels { get; } = [];
    public ObservableCollection<ChannelDefinition> ProfileChannels { get; } = [];
    public Array ChannelRoles { get; } = Enum.GetValues<ChannelRole>();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _project = ShowProject.CreateDefault(_profile);
        _safety = new SafetyController(_simulator, _profile);
        _autoShow = new AutoShowEngine(_safety);
        _safety.StateChanged += (_, _) => Dispatcher.Invoke(UpdateSafetyState);
        _audio.FeaturesAvailable += Audio_FeaturesAvailable;
        _audio.SilenceElapsed += async (_, _) => await Dispatcher.InvokeAsync(async () => await EmergencyBlackoutAsync("Audio silence timeout."));
        _audio.CaptureFailed += async (_, exception) => await Dispatcher.InvokeAsync(async () => await EmergencyBlackoutAsync($"Audio failed: {exception.Message}"));
        LoadProfileIntoEditor();
        RebuildMixer();
        _uiReady = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AudioSourceCombo.ItemsSource = Enum.GetValues<AudioSourceMode>();
        AudioSourceCombo.SelectedItem = _project.AudioSource;
        EvidenceCombo.ItemsSource = Enum.GetValues<EvidenceStatus>();
        EvidenceCombo.SelectedItem = _profile.Evidence;
        ScenesList.ItemsSource = _project.Scenes;
        CuesList.ItemsSource = _project.Cues;
        RefreshAudioDevices();
        UpdateSafetyState();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        ApplySafetyFields();
        await _safety.ConnectAsync();
        SetStatus("Simulator connected in blackout.");
    });

    private async void Arm_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        ApplySafetyFields();
        await _safety.ArmAsync();
        SetStatus("Simulator armed. This does not enable hardware output.");
    });

    private async void Blackout_Click(object sender, RoutedEventArgs e) => await EmergencyBlackoutAsync("Blackout requested.");

    private async void MixerSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || !_safety.IsArmed || sender is not Slider slider || slider.Tag is not int channel) return;
        try { await _safety.SetChannelAsync(channel, (byte)Math.Round(e.NewValue)); }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        string name = SceneNameText.Text.Trim();
        if (name.Length == 0) { ShowError("Scene name is required."); return; }
        byte[] universe = _safety.CurrentUniverse;
        Scene scene = new()
        {
            Name = name,
            Values = _profile.Channels.ToDictionary(channel => channel.Number, channel => universe[_safety.StartAddress + channel.Number - 2])
        };
        _project.Scenes.RemoveAll(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
        _project.Scenes.Add(scene);
        ScenesList.Items.Refresh();
        SetStatus($"Captured scene '{name}'.");
    }

    private async void ApplyScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not Scene scene) { ShowError("Select a scene first."); return; }
        await RunUiAction(async () =>
        {
            await _safety.ApplySceneAsync(scene);
            SyncMixerFromUniverse();
            SetStatus($"Applied scene '{scene.Name}'.");
        });
    }

    private void AudioSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshAudioDevices();
    }

    private void RefreshAudioDevices()
    {
        try
        {
            AudioSourceMode mode = AudioSourceCombo.SelectedItem is AudioSourceMode selected ? selected : AudioSourceMode.Microphone;
            AudioDeviceCombo.ItemsSource = AudioCaptureService.GetDevices(mode);
            AudioDeviceCombo.SelectedIndex = AudioDeviceCombo.Items.Count > 0 ? 0 : -1;
        }
        catch (Exception exception) { SetStatus($"Audio devices unavailable: {exception.Message}"); }
    }

    private void StartAudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySafetyFields();
            AudioSourceMode mode = (AudioSourceMode)(AudioSourceCombo.SelectedItem ?? AudioSourceMode.Microphone);
            string? deviceId = (AudioDeviceCombo.SelectedItem as AudioDevice)?.Id;
            _audio.SilenceTimeoutSeconds = _project.SilenceTimeoutSeconds;
            _audio.Start(mode, deviceId);
            _project.AudioSource = mode;
            SetStatus($"Audio analysis started: {mode}.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        _audio.Stop();
        await EmergencyBlackoutAsync("Audio stopped.");
    }

    private async void Audio_FeaturesAvailable(object? sender, AudioFeatures features)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            EnergyMeter.Value = features.Energy * 100;
            LowMeter.Value = features.Low * 100;
            MidMeter.Value = features.Mid * 100;
            HighMeter.Value = features.High * 100;
            BeatText.Text = features.Beat ? "Beat: HIT" : "Beat: -";
            if (!AutoModeCheck.IsChecked.GetValueOrDefault() || !_safety.IsArmed) return;
            try
            {
                await _autoShow.ApplyAsync(features, _project.AutoMappings);
                SyncMixerFromUniverse();
            }
            catch (InvalidOperationException) { }
        });
    }

    private void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        int number = ProfileChannels.Count == 0 ? 1 : ProfileChannels.Max(channel => channel.Number) + 1;
        ProfileChannels.Add(new ChannelDefinition { Number = number, Name = $"Channel {number}" });
        ChannelCountText.Text = Math.Max(number, ParseInt(ChannelCountText.Text, number)).ToString(CultureInfo.InvariantCulture);
    }

    private void RemoveChannel_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is ChannelDefinition channel) ProfileChannels.Remove(channel);
    }

    private async void ApplyProfile_Click(object sender, RoutedEventArgs e) => await RunUiAction(() =>
    {
        if (_safety.IsConnected) throw new InvalidOperationException("Disconnect before changing the fixture profile.");
        _profile = ReadProfileEditor();
        IReadOnlyList<string> errors = _profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        _safety.SetProfile(_profile);
        _project = ShowProject.CreateDefault(_profile);
        ScenesList.ItemsSource = _project.Scenes;
        CuesList.ItemsSource = _project.Cues;
        RebuildMixer();
        ProfileStatusText.Text = _profile.SimulationOnly ? "Simulation profile applied." : "Profile remains blocked from hardware until a transport exists.";
        SetStatus($"Applied profile {_profile.Manufacturer} {_profile.Model}.");
        return Task.CompletedTask;
    });

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "BeamForge fixture profile (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (_safety.IsConnected) throw new InvalidOperationException("Disconnect before importing a profile.");
            _profile = JsonStore.LoadFixture(dialog.FileName);
            _safety.SetProfile(_profile);
            _project = ShowProject.CreateDefault(_profile);
            LoadProfileIntoEditor();
            RebuildMixer();
            SetStatus($"Imported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "BeamForge fixture profile (*.json)|*.json", FileName = "fixture-profile.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FixtureProfile profile = ReadProfileEditor();
            JsonStore.SaveFixture(dialog.FileName, profile);
            SetStatus($"Saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void ImportArtwork_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "Supported images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            TraceOptions options = ReadTraceOptions();
            SetStatus("Tracing artwork...");
            _animation = await Task.Run(() => LaserImageTracer.Trace(dialog.FileName, options));
            PreviewApprovedCheck.IsChecked = false;
            FrameSlider.Maximum = Math.Max(0, _animation.Frames.Count - 1);
            FrameSlider.Value = 0;
            DrawPreview();
            ArtworkStatusText.Text = $"{Path.GetFileName(dialog.FileName)}: {_animation.Frames.Count} frame(s), {_animation.TotalPoints} total points. ILDA timing is projector-controlled.";
            SetStatus("Artwork traced. Review the ordered path before export.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void PreviewApproval_Changed(object sender, RoutedEventArgs e) => ExportIldaButton.IsEnabled = _animation is not null && PreviewApprovedCheck.IsChecked == true;

    private void ExportIlda_Click(object sender, RoutedEventArgs e)
    {
        if (_animation is null || PreviewApprovedCheck.IsChecked != true) return;
        SaveFileDialog dialog = new() { Filter = "ILDA frame file (*.ild)|*.ild", FileName = "beamforge.ild", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            IldaFile.ExportFormat5(dialog.FileName, _animation.Frames);
            SetStatus($"Exported standard ILDA format 5 to {dialog.FileName}. LM-002 compatibility remains unverified.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void InspectIlda_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "ILDA frame file (*.ild)|*.ild|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using FileStream stream = File.OpenRead(dialog.FileName);
            IReadOnlyList<IldaSectionInfo> sections = IldaFile.Inspect(stream);
            string summary = sections.Count == 0 ? "No frame sections found." : string.Join(Environment.NewLine, sections.Select(section => $"Frame {section.FrameNumber}: format {section.FormatCode}, {section.RecordCount} records, {section.Name}"));
            MessageBox.Show(this, summary, "ILDA inspection", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void FrameSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiReady) DrawPreview();
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

    private void DrawPreview()
    {
        PreviewCanvas.Children.Clear();
        if (_animation is null || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;
        int frameIndex = Math.Clamp((int)Math.Round(FrameSlider.Value), 0, _animation.Frames.Count - 1);
        LaserFrame frame = _animation.Frames[frameIndex];
        PointCollection segment = [];
        void Flush()
        {
            if (segment.Count > 1) PreviewCanvas.Children.Add(new System.Windows.Shapes.Polyline { Points = segment, Stroke = Brushes.Lime, StrokeThickness = 1 });
            segment = [];
        }
        foreach (LaserPoint point in frame.Points)
        {
            if (point.Blank) { Flush(); continue; }
            segment.Add(new System.Windows.Point((point.X + 1) * PreviewCanvas.ActualWidth / 2, (1 - point.Y) * PreviewCanvas.ActualHeight / 2));
        }
        Flush();
        FrameStatusText.Text = $"{frameIndex + 1}/{_animation.Frames.Count} - {frame.Points.Count} points";
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "BeamForge show (*.beamforge.json)|*.beamforge.json", FileName = "show.beamforge.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ApplyProjectFields();
            JsonStore.SaveProject(dialog.FileName, _project, _profile);
            SetStatus($"Saved project {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "BeamForge show (*.beamforge.json)|*.beamforge.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _project = JsonStore.LoadProject(dialog.FileName, _profile);
            LoadProjectFields();
            ScenesList.ItemsSource = _project.Scenes;
            CuesList.ItemsSource = _project.Cues;
            SetStatus($"Loaded project {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void AddCue_Click(object sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not Scene scene) { ShowError("Select a scene on the Live Control tab first."); return; }
        _project.Cues.Add(scene.Name);
        CuesList.Items.Refresh();
    }

    private void RemoveCue_Click(object sender, RoutedEventArgs e)
    {
        if (CuesList.SelectedIndex >= 0) _project.Cues.RemoveAt(CuesList.SelectedIndex);
        CuesList.Items.Refresh();
    }

    private void MoveCueUp_Click(object sender, RoutedEventArgs e) => MoveCue(-1);
    private void MoveCueDown_Click(object sender, RoutedEventArgs e) => MoveCue(1);

    private void MoveCue(int offset)
    {
        int from = CuesList.SelectedIndex;
        int to = from + offset;
        if (from < 0 || to < 0 || to >= _project.Cues.Count) return;
        (_project.Cues[from], _project.Cues[to]) = (_project.Cues[to], _project.Cues[from]);
        CuesList.Items.Refresh();
        CuesList.SelectedIndex = to;
    }

    private FixtureProfile ReadProfileEditor()
    {
        int count = ParseInt(ChannelCountText.Text, 0);
        List<ChannelDefinition> channels = ProfileChannels.Select(CloneChannel).ToList();
        return new FixtureProfile
        {
            Id = Slug($"{ManufacturerText.Text}-{ModelText.Text}"),
            Manufacturer = ManufacturerText.Text.Trim(),
            Model = ModelText.Text.Trim(),
            ChannelCount = count,
            Evidence = EvidenceCombo.SelectedItem is EvidenceStatus evidence ? evidence : EvidenceStatus.Provisional,
            SimulationOnly = SimulationOnlyCheck.IsChecked == true,
            EvidenceNote = "Edited in BeamForge; verification evidence must be recorded separately.",
            Channels = channels,
            BlackoutValues = channels.ToDictionary(channel => channel.Number, channel => channel.SafeValue)
        };
    }

    private void LoadProfileIntoEditor()
    {
        ManufacturerText.Text = _profile.Manufacturer;
        ModelText.Text = _profile.Model;
        ChannelCountText.Text = _profile.ChannelCount.ToString(CultureInfo.InvariantCulture);
        EvidenceCombo.SelectedItem = _profile.Evidence;
        SimulationOnlyCheck.IsChecked = _profile.SimulationOnly;
        ProfileChannels.Clear();
        foreach (ChannelDefinition channel in _profile.Channels) ProfileChannels.Add(CloneChannel(channel));
        ProfileStatusText.Text = _profile.EvidenceNote;
    }

    private static ChannelDefinition CloneChannel(ChannelDefinition channel) => new()
    {
        Number = channel.Number,
        Name = channel.Name,
        Role = channel.Role,
        SafeValue = channel.SafeValue,
        Automatable = channel.Automatable,
        AutomationMin = channel.AutomationMin,
        AutomationMax = channel.AutomationMax,
        Hazardous = channel.Hazardous,
        Ranges = channel.Ranges.Select(range => new ChannelRange { Min = range.Min, Max = range.Max, Label = range.Label }).ToList()
    };

    private void RebuildMixer()
    {
        _uiReady = false;
        MixerChannels.Clear();
        foreach (ChannelDefinition channel in _profile.Channels.OrderBy(channel => channel.Number)) MixerChannels.Add(new MixerChannel(channel.Number, channel.Name));
        _uiReady = true;
    }

    private void SyncMixerFromUniverse()
    {
        byte[] universe = _safety.CurrentUniverse;
        _uiReady = false;
        foreach (MixerChannel channel in MixerChannels) channel.Value = universe[_safety.StartAddress + channel.Number - 2];
        _uiReady = true;
    }

    private void ApplySafetyFields()
    {
        ApplyProjectFields();
        _safety.SetConfiguration(_project.StartAddress, _project.IntensityCeilingPercent);
    }

    private void ApplyProjectFields()
    {
        _project.Name = ProjectNameText.Text.Trim();
        _project.StartAddress = ParseInt(StartAddressText.Text, 1);
        _project.IntensityCeilingPercent = ParseInt(IntensityCeilingText.Text, 25);
        _project.SilenceTimeoutSeconds = ParseDouble(SilenceTimeoutText.Text, 3);
        IReadOnlyList<string> errors = _project.Validate(_profile);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private void LoadProjectFields()
    {
        ProjectNameText.Text = _project.Name;
        StartAddressText.Text = _project.StartAddress.ToString(CultureInfo.InvariantCulture);
        IntensityCeilingText.Text = _project.IntensityCeilingPercent.ToString(CultureInfo.InvariantCulture);
        SilenceTimeoutText.Text = _project.SilenceTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        AudioSourceCombo.SelectedItem = _project.AudioSource;
    }

    private TraceOptions ReadTraceOptions() => new()
    {
        PhotoEdges = PhotoEdgesCheck.IsChecked == true,
        Threshold = ParseInt(ThresholdText.Text, 128),
        EdgeLow = ParseInt(EdgeLowText.Text, 60),
        EdgeHigh = ParseInt(EdgeHighText.Text, 150),
        PointBudget = ParseInt(PointBudgetText.Text, 600),
        BlankingDwell = ParseInt(BlankingDwellText.Text, 4),
        CornerDwell = ParseInt(CornerDwellText.Text, 0),
        Scale = ParseDouble(ScaleText.Text, 0.9)
    };

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        await EmergencyBlackoutAsync("Emergency blackout (Escape).");
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _audio.Stop();
        try { await _safety.DisposeAsync(); }
        finally { _shutdownComplete = true; Close(); }
    }

    public async Task EmergencyBlackoutAsync(string reason = "Emergency blackout.")
    {
        try { await _safety.BlackoutAsync(); }
        catch { }
        SyncMixerFromUniverse();
        SetStatus(reason);
    }

    private void UpdateSafetyState()
    {
        ConnectionStateText.Text = !_safety.IsConnected ? "Disconnected" : _safety.IsArmed ? "ARMED (SIMULATOR)" : "Connected / blackout";
        ConnectionStateText.Foreground = _safety.IsArmed ? Brushes.Orange : Brushes.White;
        ConnectButton.IsEnabled = !_safety.IsConnected;
        ArmButton.IsEnabled = _safety.IsConnected && !_safety.IsArmed;
        ProfileGrid.IsReadOnly = _safety.IsConnected;
    }

    private async Task RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { UpdateSafetyState(); }
    }

    private void ShowError(string message)
    {
        SetStatus(message);
        MessageBox.Show(this, message, "BeamForge", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetStatus(string message) => StatusText.Text = message;
    private static int ParseInt(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static double ParseDouble(string text, double fallback) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    private static string Slug(string value) => string.Join('-', value.Trim().ToLowerInvariant().Split(Path.GetInvalidFileNameChars().Append(' ').ToArray(), StringSplitOptions.RemoveEmptyEntries));
}

public sealed class MixerChannel(int number, string name) : INotifyPropertyChanged
{
    private byte _value;
    public int Number { get; } = number;
    public string Name { get; } = name;
    public byte Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
