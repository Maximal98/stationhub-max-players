using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Mono.Unix;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using UnitystationLauncher.Constants;
using UnitystationLauncher.Exceptions;
using UnitystationLauncher.Infrastructure;
using UnitystationLauncher.Models;
using UnitystationLauncher.Models.Api;
using UnitystationLauncher.Models.Enums;
using UnitystationLauncher.Services.Interface;
using Path = System.IO.Path;

namespace UnitystationLauncher.Services;

public class TTSService : ITTSService
{
    private static string _nameConfig = @"CodeScanList.json";

    private readonly HttpClient _httpClient;

    private readonly IPreferencesService _preferencesService;
    private readonly IEnvironmentService _environmentService;

    private static Process? process;

    public TTSService(HttpClient httpClient, IPreferencesService preferencesService,
        IEnvironmentService environmentService)
    {
        _httpClient = httpClient;
        _preferencesService = preferencesService;
        _environmentService = environmentService;
    }
    //returns true when updates are available, false when none are
    public async Task<bool> CheckUpdates() {
        VersionModel? localVersion = null;
        string TTSPath = _preferencesService.GetPreferences().TTSPath;

        try
        {
            var VersionFile = System.IO.Path.Combine(TTSPath, "version.txt");
            if (System.IO.File.Exists(VersionFile))
            {
                // Read the JSON file content
                string jsonContent = System.IO.File.ReadAllText(VersionFile);

                // Deserialize the JSON content into an object
                localVersion = JsonSerializer.Deserialize<VersionModel>(jsonContent);
            }
        } 
        catch (FileNotFoundException) {
            return true; //true because no version is always older than a version
        }
        catch (Exception ex)
        {
            Log.Error($"Exception while reading TTS Version on disk: {ex.Message}");
        }

        string jsonData;
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(ApiUrls.TTSVersionFile);
            if (!response.IsSuccessStatusCode)
            {
                Log.Error("Unable to download TTS Version" + response);
                return false;
            }

            jsonData = await response.Content.ReadAsStringAsync();
        }
        catch (Exception e)
        {
            Log.Error("Unable to download TTS Version" + e);
            return false;
        }

        VersionModel? remoteVersion = JsonSerializer.Deserialize<VersionModel>(jsonData, options: new()
        {
            IgnoreReadOnlyProperties = true,
            PropertyNameCaseInsensitive = true
        });

        return localVersion != null || localVersion != remoteVersion;
    }
    public async Task DownloadLatest(Download Download)
    {
        string TTSPath = _preferencesService.GetPreferences().TTSPath;
        try
        {
            Download.Active = true;
            Download.DownloadState = DownloadState.InProgress;
            StopTTS();
            // await Task.Delay(2 * 1000); //to give it some grace period to shutdown

            if (System.IO.Directory.Exists(TTSPath))
            {
                foreach (var file in System.IO.Directory.GetFiles(TTSPath))
                {
                    System.IO.File.Delete(file);
                }

                foreach (var directory in System.IO.Directory.GetDirectories(TTSPath))
                {
                    System.IO.Directory.Delete(directory, true);
                }
            }

            var zip = _environmentService.GetCurrentEnvironment() switch
            {
                CurrentEnvironment.WindowsStandalone => "win.zip",
                //CurrentEnvironment.MacOsStandalone => "mac.zip",
                CurrentEnvironment.LinuxStandalone or CurrentEnvironment.LinuxFlatpak => "lnx.tar.xz",
                _ => null
            };



            HttpResponseMessage request = await _httpClient.GetAsync(ApiUrls.TTSFiles + "/" + zip,
                HttpCompletionOption.ResponseHeadersRead);

            using Stream responseStream = await request.Content.ReadAsStreamAsync();
            Log.Information("Download connection established");
            await using ProgressStream progressStream = new(responseStream);
            using IDisposable logProgressDisposable = InstallationService.LogProgress(progressStream, Download);

            Download.Size = request.Content.Headers.ContentLength ??
                            throw new ContentLengthNullException(ApiUrls.TTSFiles + "/" + zip);

            using IDisposable progressDisposable = progressStream.Progress.Subscribe(p => { Download.Downloaded = p; });

            await Task.Run(() => Extract(progressStream));

            Download.Active = false;
            Download.DownloadState = DownloadState.InProgress;
            StartTTS();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }


    private void Extract(Stream progressStream)
    {

        switch (_environmentService.GetCurrentEnvironment())
        {
            case CurrentEnvironment.WindowsStandalone:
                {
                    ZipArchive archive = new(progressStream);
                    archive.ExtractToDirectory("tts", true);
                    break;
                }
            case CurrentEnvironment.LinuxStandalone or CurrentEnvironment.LinuxFlatpak:
                {
                    using var decompressedStream = DecompressXz(progressStream); // Decompress XZ stream to get .tar
                    ExtractTar(decompressedStream, "tts");
                    break;
                }
            default:
                throw new Exception("Unsupported OS: " + _environmentService.GetCurrentEnvironment().ToString());
        }
    }

    private static Stream DecompressXz(Stream compressedStream)
    {
        var decompressedStream = new MemoryStream();
        using (var xzStream = new SharpCompress.Compressors.Xz.XZStream(compressedStream))
        {
            xzStream.CopyTo(decompressedStream);
        }

        decompressedStream.Seek(0, SeekOrigin.Begin);
        return decompressedStream;
    }


    private void ExtractTar(Stream tarStream, string destinationPath)
    {
        using var archive = TarArchive.Open(tarStream);
        foreach (var entry in archive.Entries)
        {
            if (!entry.IsDirectory)
            {
                entry.WriteToDirectory(destinationPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
        }
    }

    private (string?, string?) FindExecutable()
    {
        string installationBasePath = _preferencesService.GetPreferences().InstallationPath;
        if (string.IsNullOrWhiteSpace(installationBasePath) || !Directory.Exists(installationBasePath))
        {
            return (null, null);
        }

        return _environmentService.GetCurrentEnvironment() switch
        {
            CurrentEnvironment.WindowsStandalone
                => (Path.Combine(installationBasePath, "tts", "python-3.10.11.amd64", "python.exe"),
                    Path.Combine(installationBasePath, "tts", "scripts")),
            CurrentEnvironment.MacOsStandalone
                => throw new NotImplementedException("tts Mac Support not implemented"),
            CurrentEnvironment.LinuxStandalone or CurrentEnvironment.LinuxFlatpak
                => (Path.Combine(installationBasePath, "tts", "bin", "python"),
                    Path.Combine(installationBasePath, "tts", "bin")),
            _ => (null, null)
        };
    }

    private void EnsureExecutableFlagOnUnixSystems(string executablePath)
    {
        if (_environmentService.GetCurrentEnvironment() != CurrentEnvironment.WindowsStandalone)
        {
            UnixFileInfo fileInfo = new(executablePath);
            fileInfo.FileAccessPermissions |= FileAccessPermissions.UserReadWriteExecute;
        }
    }

    public void StartTTS()
    {
        try
        {
            if (process != null && process.HasExited == false)
                return;
        }
        catch (Exception e)
        {
            Log.Error("Error while Querying TTS Process: " + e.ToString());
        }


        string TTSPath = _preferencesService.GetPreferences().TTSPath;
        if (System.IO.Directory.Exists(TTSPath) == false)
            return; //Not installed

        (string?, string?) executable = FindExecutable();
        if (string.IsNullOrWhiteSpace(executable.Item1))
        {
            Log.Warning($"Couldn't find TTS executable. Installation Path: {executable.Item1 ?? "null"}");
            return;
        }

        EnsureExecutableFlagOnUnixSystems(executable.Item1);

        string arguments = "TTS_local_Server.py";
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            WorkingDirectory = executable.Item2,
            FileName = executable.Item1,
            ArgumentList = { arguments },
            UseShellExecute = false, // Don't use the shell
            CreateNoWindow = true, // Run without creating a window
        };

        if (startInfo == null)
        {
            const string failureReason = "Unhandled platform.";
            Log.Warning(failureReason + $" Platform: {Enum.GetName(_environmentService.GetCurrentEnvironment())}");
            return;
        }

        // Start the process
        process = new Process();

        process.StartInfo = startInfo;
        process.EnableRaisingEvents = true;

        // Handle process exit to clean up if needed
        process.Exited += (sender, e) => { Log.Information($"Subprocess with PID {process.Id} exited."); };

        // Ensure subprocess ends when the main application exits
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            if (process != null)
            {
                if (process.HasExited == false)
                    process.Kill();
            }

        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"Error starting TTS process: {ex.Message}");
        }
    }


    public void StopTTS()
    {
        if (process != null)
        {
            try
            {
                if (process.HasExited == false)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception while stopping TTS: " + e.ToString());
            }

        }
    }
}