using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.Core;

namespace Kazrich.App;

internal sealed record ModelAsset(string Id, string File, string Url, string Sha256, long Size);

internal sealed class ManagedModel : IDisposable
{
    internal const string RuntimeVersion = "b10687";
    private const string RuntimeUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10687/llama-b10687-bin-win-cpu-x64.zip";
    private const string RuntimeHash = "7671db956d077e7f055c7b6b0cf48e58726785212497468d0fc3b40c6aa9adae";
    private const long RuntimeSize = 18131074;
    private static readonly ModelAsset CudaRuntime = new("cuda", "llama-b10687-cuda.zip",
        "https://github.com/ggml-org/llama.cpp/releases/download/b10687/llama-b10687-bin-win-cuda-13.3-x64.zip",
        "b55e69159e6683fae8ee5cace68c6fd64bfc146c5f6d00eeefa778704d3fdc6c", 146519181);
    private static readonly ModelAsset CudaLibraries = new("cudart", "cudart-b10687.zip",
        "https://github.com/ggml-org/llama.cpp/releases/download/b10687/cudart-llama-bin-win-cuda-13.3-x64.zip",
        "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e", 390970417);
    internal static readonly ModelAsset Small = new("qwen3.5:0.8b", "Qwen3.5-0.8B-Q8_0.gguf",
        "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/6ab461498e2023f6e3c1baea90a8f0fe38ab64d0/Qwen3.5-0.8B-Q8_0.gguf",
        "0ad885ffd4bb022fc4f0d33a3308fa108ef8613159d3b3a67e23abca056b7a6c", 811843840);
    internal static readonly ModelAsset Medium = new("qwen3.5:2b", "Qwen3.5-2B-Q4_K_M.gguf",
        "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf",
        "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223", 1280835840);
    private readonly string root;
    private Process? process;
    private nint job;
    private string? runningModel;
    private int runningThreads;
    private bool runtimeCpuOnly = true;
    private string? endpoint;
    private volatile bool disposed;
    private volatile bool ready;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private string RuntimeDirectoryFor(bool cpuOnly) => Path.Combine(root, "runtime", "llama-" + RuntimeVersion + (cpuOnly ? "-cpu" : "-cuda"));
    private string RuntimeDirectory => RuntimeDirectoryFor(runtimeCpuOnly);
    private string DownloadDirectory => Path.Combine(root, "downloads");
    internal string ModelDirectory => Path.Combine(root, "models");
    internal ManagedModel(string? root = null) => this.root = root ?? SettingsFile.DirectoryPath;
    internal static ModelAsset Asset(string model) => model == Medium.Id ? Medium : model == Small.Id ? Small : throw new ArgumentException("Неизвестная модель.");
    private string ModelPath(ModelAsset asset) => Path.Combine(ModelDirectory, asset.File);
    private static string? FindServer(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    private string? ServerPath => FindServer(RuntimeDirectory);
    internal bool IsInstalled(string model, bool cpuOnly = true)
    {
        var asset = Asset(model);
        var directory = RuntimeDirectoryFor(cpuOnly);
        return FindServer(directory) != null && File.Exists(Path.Combine(directory, "verified.sha256")) &&
            File.Exists(ModelPath(asset)) && new FileInfo(ModelPath(asset)).Length == asset.Size &&
            File.Exists(ModelPath(asset) + ".verified");
    }
    private int runningContextTokens;
    internal bool IsRunning(Settings settings)
    {
        var active = process;
        try
        {
            return ready && active is { HasExited: false } && endpoint != null &&
                runningModel == settings.Model && runningThreads == settings.CpuThreads && runtimeCpuOnly == settings.CpuOnly &&
                runningContextTokens == settings.RequiredContextTokens();
        }
        catch (InvalidOperationException) { return false; } // A concurrent Stop may have disposed this process.
    }
    internal Settings ConnectionSettings(Settings settings)
    {
        var address = endpoint;
        if (address == null || !IsRunning(settings)) throw new InvalidOperationException("Модель ещё не запущена.");
        return settings with { Backend = "OpenAI", Endpoint = address };
    }

    internal async Task PrepareAndStartAsync(Settings settings, bool install, IProgress<string> progress, CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunning(settings)) return;
            Stop();
            runtimeCpuOnly = settings.CpuOnly;
            if (!IsInstalled(settings.Model, settings.CpuOnly))
            {
                if (!install) throw new InvalidOperationException("Нажмите «Скачать и включить», чтобы подготовить локальную модель.");
                await InstallAsync(Asset(settings.Model), progress, token);
            }
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            progress.Report("Загружаю модель в память…");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var address = "http://127.0.0.1:" + port;
            endpoint = address;
            var info = new ProcessStartInfo(ServerPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(ServerPath!)!
            };
            foreach (var arg in new[] { "-m", ModelPath(Asset(settings.Model)), "--host", "127.0.0.1", "--port", port.ToString(),
                "-t", settings.CpuThreads.ToString(), "-tb", settings.CpuThreads.ToString(), "-ngl", settings.CpuOnly ? "0" : "99", "-c", settings.RequiredContextTokens().ToString(),
                "-np", "1", "--reasoning", "off", "--prio", "-1", "--alias", settings.Model, "--no-webui", "-lv", "4" }) info.ArgumentList.Add(arg);
            var starting = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить локальный движок.");
            process = starting;
            try
            {
                job = ProcessJob.Attach(starting);
                var gpuOffloaded = 0;
                starting.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && System.Text.RegularExpressions.Regex.IsMatch(e.Data, @"offloaded [1-9]\d*/\d+ layers to GPU"))
                        Interlocked.Exchange(ref gpuOffloaded, 1);
                };
                starting.BeginOutputReadLine(); starting.BeginErrorReadLine(); // Drain streams; do not log user words.
                runningModel = settings.Model; runningThreads = settings.CpuThreads;
                runningContextTokens = settings.RequiredContextTokens();
                using var healthClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                var deadline = Stopwatch.StartNew();
                void CheckStartingProcess()
                {
                    if (disposed || !ReferenceEquals(process, starting)) throw new OperationCanceledException("Запуск локальной модели остановлен.");
                    if (starting.HasExited) throw new InvalidOperationException("Локальный движок завершился при запуске. Проверьте свободную память.");
                }
                while (deadline.Elapsed < TimeSpan.FromSeconds(90))
                {
                    token.ThrowIfCancellationRequested();
                    CheckStartingProcess();
                    try
                    {
                        using var response = await healthClient.GetAsync(address + "/health", token);
                        if (response.IsSuccessStatusCode)
                        {
                            CheckStartingProcess();
                            if (!settings.CpuOnly && Volatile.Read(ref gpuOffloaded) == 0)
                                throw new InvalidOperationException("Движок не подтвердил загрузку слоёв на NVIDIA GPU. Проверьте CUDA-драйвер или включите «Только CPU».");
                            ready = true;
                            progress.Report(settings.CpuOnly ? "Локальная модель готова • CPU, " + settings.CpuThreads + " потока" : "Локальная модель готова • NVIDIA GPU (CUDA)");
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && !disposed && ReferenceEquals(process, starting)) { }
                    await Task.Delay(200, token);
                }
                throw new TimeoutException("Модель не загрузилась за 90 секунд.");
            }
            catch { Stop(); throw; }
        }
        finally { lifecycle.Release(); }
    }

    private async Task InstallAsync(ModelAsset asset, IProgress<string> progress, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(DownloadDirectory);
        Directory.CreateDirectory(ModelDirectory);
        if (ServerPath == null || !File.Exists(Path.Combine(RuntimeDirectory, "verified.sha256")))
        {
            if (!runtimeCpuOnly)
            {
                Directory.CreateDirectory(RuntimeDirectory);
                foreach (var package in new[] { CudaRuntime, CudaLibraries })
                {
                    var cudaArchive = Path.Combine(DownloadDirectory, package.File);
                    await DownloadVerifiedAsync(package.Url, cudaArchive, package.Sha256, package.Size, "CUDA", progress, token);
                    await Task.Run(() => ZipFile.ExtractToDirectory(cudaArchive, RuntimeDirectory, true), token);
                }
                if (ServerPath == null) throw new InvalidDataException("В CUDA-архиве не найден llama-server.exe.");
                await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory, "verified.sha256"), CudaRuntime.Sha256 + "\n" + CudaLibraries.Sha256, token);
            }
            else
            {
            var archive = Path.Combine(DownloadDirectory, "llama-" + RuntimeVersion + "-cpu.zip");
            await DownloadVerifiedAsync(RuntimeUrl, archive, RuntimeHash, RuntimeSize, "Движок", progress, token);
            progress.Report("Распаковываю локальный движок…");
            Directory.CreateDirectory(RuntimeDirectory);
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, RuntimeDirectory, true), token);
            if (ServerPath == null) throw new InvalidDataException("В архиве движка не найден llama-server.exe.");
            await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory, "verified.sha256"), RuntimeHash, token);
            }
        }
        await DownloadVerifiedAsync(asset.Url, ModelPath(asset), asset.Sha256, asset.Size, "Модель", progress, token);
        await File.WriteAllTextAsync(ModelPath(asset) + ".verified", asset.Sha256, token);
    }

    private static async Task DownloadVerifiedAsync(string url, string path, string expectedHash, long expectedSize,
        string label, IProgress<string> progress, CancellationToken token)
    {
        if (File.Exists(path))
        {
            progress.Report(label + ": проверяю скачанный файл…");
            if (new FileInfo(path).Length == expectedSize && await HasHashAsync(path, expectedHash, token)) return;
        }
        var partial = path + ".part";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > expectedSize) { File.Delete(partial); offset = 0; }
        if (offset != expectedSize)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RichType/0.8.33");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("Скачивание должно идти по HTTPS.");
            if (response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
            else if (response.Content.Headers.ContentRange?.From != offset) throw new InvalidDataException("Сервер вернул неверный диапазон файла.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                if (offset + read > expectedSize) throw new InvalidDataException("Размер загрузки не совпадает с ожидаемым.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                offset += read;
                if (watch.ElapsedMilliseconds > 150)
                {
                    progress.Report($"{label}: {offset * 100 / expectedSize}% • {offset / 1_000_000} / {expectedSize / 1_000_000} МБ");
                    watch.Restart();
                }
            }
        }
        progress.Report(label + ": проверяю SHA-256…");
        if (!File.Exists(partial) || new FileInfo(partial).Length != expectedSize || !await HasHashAsync(partial, expectedHash, token))
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw new InvalidDataException("Проверка скачанного файла не прошла. Повторите загрузку.");
        }
        File.Move(partial, path, true);
    }

    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal void Stop()
    {
        ready = false;
        var activeJob = Interlocked.Exchange(ref job, 0);
        var active = Interlocked.Exchange(ref process, null);
        if (activeJob != 0) ProcessJob.Close(activeJob);
        if (active != null)
        {
            try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            active.Dispose();
        }
        endpoint = null; runningModel = null;
    }
    public void Dispose() { disposed = true; Stop(); }
}

internal static class ProcessJob
{
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Io
    { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public Limits Basic; public Io Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    internal static nint Attach(Process process)
    {
        var handle = CreateJobObject(0, null);
        var limits = new ExtendedLimits { Basic = new Limits { Flags = 0x2000 } };
        if (handle == 0 || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(handle, process.Handle))
        {
            if (handle != 0) CloseHandle(handle);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось привязать движок к приложению.");
        }
        return handle;
    }
    internal static void Close(nint handle) => CloseHandle(handle);
}
