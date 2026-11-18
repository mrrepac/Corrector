using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Corrector;

internal sealed record Asset(string File, string Url, long Size, string Sha256);
internal sealed class AssetLock
{
    public Asset Model { get; set; } = null!;
    public Asset[] Runtime { get; set; } = [];
}

internal static class Assets
{
    public static AssetLock ReadLock() => JsonSerializer.Deserialize<AssetLock>(File.ReadAllText(Path.Combine(Paths.Root,"assets.lock.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive=true })!;
    public static async Task<string> Ensure(IProgress<string> progress, CancellationToken ct)
    {
        var manifest = ReadLock();
        var runtime = Path.Combine(Paths.Assets, "runtime");
        var marker = Path.Combine(runtime, "verified-b12499.txt");
        string[] required = ["llama-server.exe", "llama-server-impl.dll", "llama.dll", "ggml.dll", "ggml-base.dll",
            "ggml-cuda.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll"];
        if (!File.Exists(marker) || required.Any(name => !File.Exists(Path.Combine(runtime, name))))
        {
            Directory.CreateDirectory(runtime);
            foreach(var asset in manifest.Runtime)
            {
                var archive = await Download(asset, Path.Combine(Paths.Assets,"downloads"), progress, ct);
                progress.Report("Распаковка движка…");
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, runtime, true), ct);
            }
            File.WriteAllText(marker,"b12499");
        }
        return await Download(manifest.Model, Path.Combine(Paths.Assets,"models"), progress, ct);
    }
    // Called only after the extracted engine successfully loads the model.
    internal static void RemoveRuntimeArchives()
    {
        string directory = Path.Combine(Paths.Assets, "downloads");
        if (!Directory.Exists(directory)) return;
        foreach (var asset in ReadLock().Runtime)
        {
            var path = Path.Combine(directory, Path.GetFileName(asset.File));
            try { File.Delete(path); File.Delete(path + ".verified"); }
            catch (IOException) { } // Another instance may be preparing its runtime.
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task CopyBounded(Stream input, Stream output, long remaining, CancellationToken token, Action<long>? report = null)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (read > remaining - total) throw new InvalidDataException("Сервер прислал больше данных, чем ожидалось.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
            report?.Invoke(total);
        }
        if (total != remaining) throw new EndOfStreamException("Загрузка прервалась до конца файла.");
    }
    static async Task<string> Download(Asset asset, string folder, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,asset.File), part=file+".part", marker=file+".verified";
        if (File.Exists(file) && new FileInfo(file).Length==asset.Size)
        {
            string stamp=$"{asset.Sha256}:{File.GetLastWriteTimeUtc(file).Ticks}";
            if (File.Exists(marker) && File.ReadAllText(marker)==stamp) return file;
            await Verify(file,asset,progress,ct);
            File.WriteAllText(marker,stamp); return file;
        }
        using var client=new HttpClient { Timeout=Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Corrector/1.0");
        for (int attempt=0; ;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long offset=File.Exists(part)?new FileInfo(part).Length:0;
                if(offset>asset.Size) { File.Delete(part);offset=0; }
                if(offset<asset.Size)
                {
                    long requiredBytes=asset.Size-offset+1024L*1024*1024;
                    if(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < requiredBytes)
                        throw new IOException($"Для загрузки {asset.File} требуется {requiredBytes/1e9:F1} ГБ свободного места.");
                    using var request=new HttpRequestMessage(HttpMethod.Get, asset.Url);
                    if(offset>0)request.Headers.Range=new RangeHeaderValue(offset,null);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromHours(4));
                    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                    response.EnsureSuccessStatusCode();
                    if(response.StatusCode!=HttpStatusCode.PartialContent)offset=0;
                    else if(response.Content.Headers.ContentRange?.From!=offset)throw new IOException("Некорректный ответ при продолжении загрузки.");
                    long remaining = asset.Size - offset;
                    if (response.Content.Headers.ContentLength is long size && size > remaining)
                        throw new InvalidDataException("Размер ответа превышает ожидаемый.");
                    await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);
                    await using(var output=new FileStream(part,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,1024*1024,true))
                    {
                        var last=DateTime.MinValue;
                        await CopyBounded(input, output, remaining, timeout.Token, received =>
                        {
                            if((DateTime.UtcNow-last).TotalMilliseconds>500)
                            {
                                long written = offset + received;
                                progress.Report($"Загрузка {asset.File}\n{written/1e9:F2} / {asset.Size/1e9:F2} ГБ · {100.0*written/asset.Size:F0}%");last=DateTime.UtcNow;
                            }
                        });
                    }
                }
                await Verify(part,asset,progress,ct);
                File.Move(part,file,true);
                File.WriteAllText(marker,$"{asset.Sha256}:{File.GetLastWriteTimeUtc(file).Ticks}");
                return file;
            }
            catch(OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Истекло время загрузки. Повтори загрузку, чтобы продолжить."); }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) when(attempt<3)
            {
                progress.Report($"Загрузка прервана: {ex.Message}\nПовтор через 3 секунды…");
                await Task.Delay(3000,ct);
            }
        }
    }
    static async Task Verify(string path, Asset asset,IProgress<string> progress,CancellationToken ct)
    {
        progress.Report("Проверка целостности: "+asset.File);
        if(new FileInfo(path).Length!=asset.Size)throw new IOException("Файл загружен не полностью.");
        await using var stream=File.OpenRead(path);
        string hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
        if(!hash.Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))
        {
            stream.Close(); File.Delete(path);
            throw new IOException("Контрольная сумма не совпала. Файл будет загружен заново.");
        }
    }
}
