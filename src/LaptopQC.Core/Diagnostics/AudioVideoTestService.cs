#if WINDOWS
using LaptopQC.Core.Abstractions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Service for testing Microphone, Speakers (Stereo), Camera, and 3.5mm Audio Jack
/// </summary>
public class AudioVideoTestService : IAudioVideoTestService
{
    private readonly SpeechSynthesizer _synthesizer;
    private string _tempRecordingPath;
    private WasapiOut? _jackPlayer;

    // P/Invoke for WinMM (MCI) to record audio
    [DllImport("winmm.dll", EntryPoint = "mciSendString", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr hwndCallback);

    public AudioVideoTestService()
    {
        // Initialize speech synthesizer for speaker testing
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
            }
            catch (Exception)
            {
                // Handle case where speech is not available
            }
        }
    }

    /// <summary>
    /// Test stereo speakers by speaking text on a specific channel
    /// </summary>
    /// <param name="isLeft">True for Left channel, False for Right channel</param>
    public void TestSpeaker(bool isLeft)
    {
        if (_synthesizer == null) return;
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.SpeakAsync(isLeft ? "Testing Left Speaker" : "Testing Right Speaker");
    }

    /// <summary>
    /// Start recording microphone input to a temp file
    /// </summary>
    public void StartOneShotMicTest()
    {
        _tempRecordingPath = Path.Combine(Path.GetTempPath(), "mic_test.wav");
        
        // Close any previous
        mciSendString("close recsound", null, 0, IntPtr.Zero);
        
        // Open
        mciSendString("open new type waveaudio alias recsound", null, 0, IntPtr.Zero);
        
        // Record
        mciSendString("record recsound", null, 0, IntPtr.Zero);
    }

    /// <summary>
    /// Stop recording and save to file
    /// </summary>
    public void StopMicTest()
    {
        mciSendString($"save recsound \"{_tempRecordingPath}\"", null, 0, IntPtr.Zero);
        mciSendString("close recsound", null, 0, IntPtr.Zero);
    }

    /// <summary>
    /// Play back the recorded file (runs on background thread to avoid blocking UI)
    /// </summary>
    public void PlaybackMicRecording()
    {
        if (File.Exists(_tempRecordingPath))
        {
            var path = _tempRecordingPath;
            Task.Run(() =>
            {
                mciSendString("close mysound", null, 0, IntPtr.Zero);
                mciSendString($"open \"{path}\" type waveaudio alias mysound", null, 0, IntPtr.Zero);
                mciSendString("play mysound wait", null, 0, IntPtr.Zero);
                mciSendString("close mysound", null, 0, IntPtr.Zero);
            });
        }
    }

    // ─── 3.5mm Audio Jack Detection & Playback ───────────────────────────

    /// <summary>
    /// Check if a headphone is connected via the 3.5mm analog jack.
    /// Excludes USB and Bluetooth audio devices — only detects analog endpoints.
    /// Returns (isConnected, deviceFriendlyName).
    /// </summary>
    public (bool IsConnected, string DeviceName) GetHeadphoneStatus()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    // ── Exclude USB and Bluetooth devices ──
                    // The device ID contains the bus type, e.g.:
                    //   {0.0.0.00000000}.{guid}  for HDAUDIO (analog)
                    //   SWD\MMDEVAPI\{...}\{...}  with the endpoint path
                    // Check the endpoint ID path for bus indicators
                    var deviceId = device.ID?.ToLowerInvariant() ?? "";
                    
                    // Also check PKEY_Device_EnumeratorName ({a45c254e-df1c-4efd-8020-67d146a850e0}, 24)
                    bool isAnalog = false;
                    try
                    {
                        var props = device.Properties;
                        
                        // PKEY_Device_EnumeratorName tells us the bus
                        var enumeratorNameKey = new PropertyKey(
                            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
                        if (props.Contains(enumeratorNameKey))
                        {
                            var busName = props[enumeratorNameKey].Value?.ToString()?.ToLowerInvariant() ?? "";
                            // HDAUDIO = analog (Realtek, etc.), PCI = onboard
                            // USB = USB audio device, BTHENUM = Bluetooth
                            if (busName.Contains("hdaudio") || busName.Contains("pci"))
                                isAnalog = true;
                            else if (busName.Contains("usb") || busName.Contains("bth"))
                                continue; // Skip USB and Bluetooth devices
                        }
                    }
                    catch { /* Best-effort bus detection */ }

                    // If we couldn't determine the bus, try heuristics on the device ID
                    if (!isAnalog)
                    {
                        if (deviceId.Contains("usb") || deviceId.Contains("bth") || deviceId.Contains("bluetooth"))
                            continue; // Skip USB and Bluetooth
                    }

                    var name = device.FriendlyName?.ToLowerInvariant() ?? "";

                    // Check if this device looks like a headphone / 3.5mm jack endpoint
                    bool isHeadphone = name.Contains("headphone") ||
                                       name.Contains("headset") ||
                                       name.Contains("3.5") ||
                                       name.Contains("line out") ||
                                       name.Contains("jack");

                    // Also check via endpoint form factor if available
                    if (!isHeadphone)
                    {
                        try
                        {
                            var props = device.Properties;
                            var formFactorKey = new PropertyKey(
                                new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
                            if (props.Contains(formFactorKey))
                            {
                                var formFactor = (EndpointFormFactor)(uint)props[formFactorKey].Value;
                                isHeadphone = formFactor == EndpointFormFactor.Headphones ||
                                              formFactor == EndpointFormFactor.Headset;
                            }
                        }
                        catch { /* Form factor check is best-effort */ }
                    }

                    if (isHeadphone)
                    {
                        return (true, device.FriendlyName ?? "Headphones");
                    }
                }
                catch { /* Skip problematic devices */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Headphone detection error: {ex.Message}");
        }

        return (false, "");
    }

    /// <summary>
    /// Speak a test sentence directly to the headphone endpoint.
    /// Uses SpeechSynthesizer to generate a WAV, then plays it through the headphone device.
    /// Returns true if playback started successfully.
    /// </summary>
    public bool PlayTestSoundToHeadphones()
    {
        try
        {
            // Stop any previous playback
            StopJackPlayback();

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            MMDevice? headphoneDevice = null;
            foreach (var device in devices)
            {
                try
                {
                    // ── Exclude USB and Bluetooth devices ──
                    var deviceId = device.ID?.ToLowerInvariant() ?? "";
                    bool isAnalog = false;
                    try
                    {
                        var props = device.Properties;
                        var enumeratorNameKey = new PropertyKey(
                            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
                        if (props.Contains(enumeratorNameKey))
                        {
                            var busName = props[enumeratorNameKey].Value?.ToString()?.ToLowerInvariant() ?? "";
                            if (busName.Contains("hdaudio") || busName.Contains("pci"))
                                isAnalog = true;
                            else if (busName.Contains("usb") || busName.Contains("bth"))
                                continue;
                        }
                    }
                    catch { }

                    if (!isAnalog)
                    {
                        if (deviceId.Contains("usb") || deviceId.Contains("bth") || deviceId.Contains("bluetooth"))
                            continue;
                    }

                    var name = device.FriendlyName?.ToLowerInvariant() ?? "";
                    bool isHeadphone = name.Contains("headphone") ||
                                       name.Contains("headset") ||
                                       name.Contains("3.5") ||
                                       name.Contains("line out") ||
                                       name.Contains("jack");

                    if (!isHeadphone)
                    {
                        try
                        {
                            var props = device.Properties;
                            var formFactorKey = new PropertyKey(
                                new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
                            if (props.Contains(formFactorKey))
                            {
                                var formFactor = (EndpointFormFactor)(uint)props[formFactorKey].Value;
                                isHeadphone = formFactor == EndpointFormFactor.Headphones ||
                                              formFactor == EndpointFormFactor.Headset;
                            }
                        }
                        catch { }
                    }

                    if (isHeadphone)
                    {
                        headphoneDevice = device;
                        break;
                    }
                }
                catch { }
            }

            if (headphoneDevice == null)
                return false;

            // Generate speech WAV to a temp file, then play through headphone endpoint
            var tempWav = Path.Combine(Path.GetTempPath(), "jack_test_speech.wav");
            using (var tts = new SpeechSynthesizer())
            {
                tts.SetOutputToWaveFile(tempWav);
                tts.Speak("Testing headphone jack. If you can hear this, the audio jack is working.");
            }

            // Play the WAV file through the specific headphone device
            var audioFile = new AudioFileReader(tempWav);
            _jackPlayer = new WasapiOut(headphoneDevice, AudioClientShareMode.Shared, false, 200);
            _jackPlayer.Init(audioFile);
            _jackPlayer.Play();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Headphone playback error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop any active jack test playback.
    /// </summary>
    public void StopJackPlayback()
    {
        try
        {
            if (_jackPlayer != null)
            {
                _jackPlayer.Stop();
                _jackPlayer.Dispose();
                _jackPlayer = null;
            }
        }
        catch { }
    }

    /// <summary>
    /// Launch Windows Camera App
    /// </summary>
    public void LaunchCameraApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "microsoft.windows.camera:",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch camera: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
        StopJackPlayback();
        // Cleanup temp file
        try
        {
            if (File.Exists(_tempRecordingPath))
                File.Delete(_tempRecordingPath);
        }
        catch { }
    }

    // ─── Enum for endpoint form factors ──────────────────────────────────
    private enum EndpointFormFactor
    {
        RemoteNetworkDevice = 0,
        Speakers = 1,
        LineLevel = 2,
        Headphones = 3,
        Microphone = 4,
        Headset = 5,
        Handset = 6,
        UnknownDigitalPassthrough = 7,
        SPDIF = 8,
        DigitalAudioDisplayDevice = 9,
        UnknownFormFactor = 10
    }
}
#endif
