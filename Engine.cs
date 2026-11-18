using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Corrector;

internal sealed class Engine : IDisposable
{
    Process? process;
    readonly HttpClient client=new() { Timeout=TimeSpan.FromMinutes(20) };
    readonly SemaphoreSlim gate=new(1,1);
    Native.Job? job;
    string endpoint="";
    readonly string secret=Guid.NewGuid().ToString("N");
    public bool Ready => process is { HasExited:false } && endpoint!="";
    public async Task Start(IProgress<string> progress,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(Ready)return;
            string model=await Assets.Ensure(progress,ct);
            string exe=Directory.GetFiles(Path.Combine(Paths.Assets,"runtime"),"llama-server.exe",SearchOption.AllDirectories).FirstOrDefault()??throw new FileNotFoundException("Не найден встроенный движок.");
            Directory.CreateDirectory(Paths.State);
            foreach(int layers in new[]{99,0})
            {
                progress.Report(layers>0?"Загрузка модели на видеокарту…":"Загрузка модели на процессоре…");
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                var info=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=Path.GetDirectoryName(exe)! };
                foreach(var arg in new[]{"-m",model,"--host","127.0.0.1","--port",port.ToString(),"--api-key",secret,"-c","12288","-ngl",layers.ToString(),"--parallel","1","--batch-size","512","--ubatch-size","128","--jinja","--reasoning-format","deepseek","--reasoning","off","--chat-template-kwargs","{\"enable_thinking\":false}","--no-webui"})info.ArgumentList.Add(arg);
                // Do not record prompts or corrected text. Keep only the bounded startup diagnostic log.
                var logPath=Path.Combine(Paths.State,"engine-startup.log");
                File.WriteAllText(logPath,DateTimeOffset.Now+"\n");
                object logGate=new();bool loading=true;
                process=new Process { StartInfo=info };
                void Log(object sender,DataReceivedEventArgs e) { if(e.Data==null)return;lock(logGate){if(loading && new FileInfo(logPath).Length<500_000)File.AppendAllText(logPath,e.Data+"\n");} }
                process.ErrorDataReceived+=Log;process.OutputDataReceived+=Log;
                process.Start();job=new Native.Job();job.Add(process);
                process.BeginErrorReadLine();process.BeginOutputReadLine();
                string address=$"http://127.0.0.1:{port}";
                try
                {
                    using var health=new HttpClient { Timeout=TimeSpan.FromSeconds(3) };
                    health.DefaultRequestHeaders.Authorization=new("Bearer",secret);
                    var watch=Stopwatch.StartNew();
                    while(watch.Elapsed<TimeSpan.FromMinutes(8))
                    {
                        ct.ThrowIfCancellationRequested();
                        if(process.HasExited)throw new IOException("Движок завершился при загрузке.");
                        try { using var response=await health.GetAsync(address+"/health",ct);if(response.IsSuccessStatusCode){lock(logGate)loading=false;Assets.RemoveRuntimeArchives();endpoint=address;client.DefaultRequestHeaders.Authorization=new("Bearer",secret);progress.Report(layers>0?"Модель готова · GPU":"Модель готова · CPU");return;} }
                        catch(HttpRequestException) {} catch(TaskCanceledException) when(!ct.IsCancellationRequested) {}
                        await Task.Delay(1000,ct);
                    }
                    throw new TimeoutException("Модель не успела загрузиться.");
                }
                catch(OperationCanceledException) { Stop();throw; }
                catch(Exception ex) { Stop();if(layers==0)throw new IOException("Не удалось запустить модель. Подробности: "+logPath,ex); }
            }
            throw new IOException("Не удалось запустить модель. Подробности: "+Path.Combine(Paths.State,"engine-startup.log"));
        }
        finally { gate.Release(); }
    }
    public async Task<(string Text, bool NeedsReview)> CorrectDraft(string text,CancellationToken ct,CorrectionMode mode,Action<int,int> showPart,AppSettings settings)
    {
        var parts = Correction.SplitDraft(text);
        var result = new System.Text.StringBuilder(text.Length);
        bool review = false;
        var draftMode = Correction.UseVerse(text, mode) ? CorrectionMode.Verse : CorrectionMode.Prose;
        for (int i=0; i<parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            showPart(i+1, parts.Count);
            string part = parts[i];
            if(string.IsNullOrWhiteSpace(part)){result.Append(part);continue;}
            string corrected=await Correct(part,ct,draftMode,settings);
            string? formatted=Correction.KeepDraftLayout(part,corrected);
            if(formatted!=null)
            {
                review |= Correction.NeedsReview(part, formatted);
                result.Append(formatted);
                continue;
            }
            // A lexical edit and a changed layout can make the original breaks ambiguous.
            // In that case correct each original line separately and copy its separators verbatim.
            foreach(string line in System.Text.RegularExpressions.Regex.Split(part,@"(\r\n|\r|\n)"))
            {
                ct.ThrowIfCancellationRequested();
                if(string.IsNullOrWhiteSpace(line)){result.Append(line);continue;}
                string singleLine=line==part ? corrected : await Correct(line,ct,draftMode,settings);
                string replacement = System.Text.RegularExpressions.Regex.Replace(singleLine,@"\s*[\r\n]+\s*"," ");
                review |= Correction.NeedsReview(line, replacement);
                result.Append(replacement);
            }
        }
        ct.ThrowIfCancellationRequested();
        return (result.ToString(), review);
    }
    public async Task<string> Translate(string text, CancellationToken ct, Action<int,int> showPart, AppSettings settings)
    {
        string instruction = settings.Prompt(PromptKind.Translation);
        var parts = Correction.SplitDraft(text);
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            showPart(i + 1, parts.Count);
            string part = parts[i];
            result.Append(string.IsNullOrWhiteSpace(part) ? part :
                await Generate(part, part, instruction, 0, ct));
        }
        return result.ToString();
    }

    public Task<string> Correct(string text,CancellationToken ct,CorrectionMode mode=CorrectionMode.Auto, AppSettings? settings=null)
    {
        settings ??= SettingsStore.Load();
        bool verse = Correction.UseVerse(text, mode);
        return Generate(text, verse ? text.ReplaceLineEndings("\n") : text,
            settings.Prompt(verse ? PromptKind.Verse : PromptKind.Prose), verse ? 0 : .2, ct);
    }

    async Task<string> Generate(string text,string input,string instruction,double temperature,CancellationToken ct)
    {
        if(!Ready)throw new InvalidOperationException("Модель ещё не готова.");
        if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("Выдели текст для исправления.");
        if(text.Length>Correction.MaxChars)throw new ArgumentException($"Выдели не больше {Correction.MaxChars} символов за один раз.");
        var body=new { messages=new[]{new { role="system",content=instruction },new { role="user",content=input }},temperature,top_p=.8,top_k=20,repeat_penalty=1.0,max_tokens=8192,stream=false,chat_template_kwargs=new { enable_thinking=false } };
        using var response=await client.PostAsJsonAsync(endpoint+"/v1/chat/completions",body,ct);
        if(!response.IsSuccessStatusCode)throw new IOException($"Ошибка движка: {(int)response.StatusCode}. Исходный текст сохранён.");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var choice=doc.RootElement.GetProperty("choices")[0];
        if(choice.GetProperty("finish_reason").GetString()!="stop")throw new InvalidDataException("Ответ модели оборвался. Выдели меньший фрагмент; исходный текст сохранён.");
        return Correction.Normalize(text,choice.GetProperty("message").GetProperty("content").GetString()??"");
    }
    public void Stop()
    {
        endpoint="";
        if(process!=null){try{if(!process.HasExited)process.Kill(true);process.WaitForExit(5000);}catch(InvalidOperationException){}process.Dispose();process=null;}
        job?.Dispose();job=null;
    }
    public void Dispose(){Stop();client.Dispose();}
}

