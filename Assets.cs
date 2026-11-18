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
        if (!File.Exists(marker))
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
                    await using var input=await response.Content.ReadAsStreamAsync(ct);
                    await using(var output=new FileStream(part,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,1024*1024,true))
                    {
                        byte[] buffer=new byte[1024*1024]; int n; var last=DateTime.MinValue;
                        while((n=await input.ReadAsync(buffer,ct))>0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0,n),ct);offset+=n;
                            if((DateTime.UtcNow-last).TotalMilliseconds>500)
                            {
                                progress.Report($"Загрузка {asset.File}\n{offset/1e9:F2} / {asset.Size/1e9:F2} ГБ · {100.0*offset/asset.Size:F0}%");last=DateTime.UtcNow;
                            }
                        }
                    }
                }
                await Verify(part,asset,progress,ct);
                File.Move(part,file,true);
                File.WriteAllText(marker,$"{asset.Sha256}:{File.GetLastWriteTimeUtc(file).Ticks}");
                return file;
            }
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
