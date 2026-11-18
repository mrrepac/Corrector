using System.Diagnostics;

namespace Corrector;

internal sealed class TrayApp : ApplicationContext
{
    readonly Engine engine=new();
    readonly Control dispatcher=new();
    readonly NotifyIcon tray;
    readonly DoubleCutHook hook;
    readonly CancellationTokenSource lifetime=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly Stopwatch watch=new();
    readonly ToolStripMenuItem status=new("Загрузка модели…"),cancel=new("Отменить проверку"),copyOriginal=new("Скопировать исходник"),copyResult=new("Скопировать исправленный текст");
    CancellationTokenSource? operation;
    CorrectionMode correctionMode = CorrectionMode.Auto;
    string? original,result;
    bool busy,loading,closing;
    string partStatus="";
    public TrayApp()
    {
        _=dispatcher.Handle;
        using var bitmap=new Bitmap(64,64);using(var graphics=Graphics.FromImage(bitmap)){graphics.Clear(Color.FromArgb(32,112,95));using var font=new Font("Segoe UI",24,FontStyle.Bold);graphics.DrawString("C",font,Brushes.White,12,10);}
        var handle=bitmap.GetHicon();using var icon=Icon.FromHandle(handle);
        tray=new NotifyIcon{Icon=(Icon)icon.Clone(),Visible=true,Text="Corrector — загрузка модели"};DestroyIcon(handle);
        var menu=new ContextMenuStrip();status.Enabled=false;cancel.Enabled=false;copyOriginal.Enabled=false;copyResult.Enabled=false;
        menu.Items.Add(status);menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Как пользоваться",null,(_,_)=>Notify("Поставь курсор в черновик и дважды нажми Ctrl+X. Для проверки части текста сначала выдели её. Исходник останется в буфере."));
        var modeMenu = new ToolStripMenuItem("Режим корректуры");
        foreach (var (mode, label) in new[] { (CorrectionMode.Auto, "Авто"), (CorrectionMode.Verse, "Стихи"), (CorrectionMode.Prose, "Проза") })
        {
            var item = new ToolStripMenuItem(label) { Checked = mode == correctionMode };
            item.Click += (_, _) =>
            {
                correctionMode = mode;
                foreach (ToolStripMenuItem option in modeMenu.DropDownItems) option.Checked = option == item;
            };
            modeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(modeMenu);
        menu.Items.Add(cancel);menu.Items.Add(copyOriginal);menu.Items.Add(copyResult);
        cancel.Click+=(_,_)=>operation?.Cancel();copyOriginal.Click+=(_,_)=>Copy(original,"Исходник в буфере. Вставь его через Ctrl+V.");copyResult.Click+=(_,_)=>Copy(result,"Исправленный текст в буфере. Вставь его через Ctrl+V.");
        menu.Items.Add("Загрузить модель",null,async(_,_)=>await Prepare());
        menu.Items.Add("Выгрузить модель",null,(_,_)=>{if(busy||loading)return;engine.Stop();Status("Модель выгружена");});
        menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Выход",null,(_,_)=>ExitThread());tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>Notify(status.Text??"Corrector работает в фоне");tray.BalloonTipClicked+=(_,_)=>{};
        hook=new DoubleCutHook();hook.Detected+=gesture=>dispatcher.BeginInvoke((Action)(async()=>await CorrectCut(gesture)));
        timer.Tick+=(_,_)=>{if(busy)ShowProgress();};timer.Start();
        dispatcher.BeginInvoke((Action)(async()=>await Prepare()));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr handle);
    void Status(string text){status.Text=text;string tip="Corrector — "+text;tray.Text=tip[..Math.Min(63,tip.Length)];}
    void Notify(string text){if(!closing)tray.ShowBalloonTip(4500,"Corrector",text,ToolTipIcon.Info);}
    void ShowProgress()=>Status($"Проверяю текст{partStatus} · {watch.Elapsed:mm\\:ss}");
    void Copy(string? text,string message){if(text==null)return;try{ClipboardText.Write(text);Notify(message);}catch(Exception ex){Notify(ex.Message);}}
    async Task Prepare()
    {
        if(busy||loading||closing)return;loading=true;Status("Загрузка модели…");
        try{await engine.Start(new Progress<string>(s=>{if(loading&&!closing)Status(s.Replace('\n',' '));}),lifetime.Token);Status("Готово · Ctrl+X дважды");Notify("Готово. Поставь курсор в черновик или выдели фрагмент и дважды нажми Ctrl+X.");}
        catch(OperationCanceledException){}catch(Exception ex){Status("Не удалось загрузить модель");Notify(ex.Message);}
        finally{loading=false;}
    }
    async Task CorrectCut(CutGesture gesture)
    {
        if(closing)return;
        bool occupied=busy||loading,ownsOperation=!occupied;
        if(ownsOperation){busy=true;partStatus="";watch.Restart();}
        try
        {
            await Native.ReleaseModifiers();
            if(Native.GetForegroundWindow()!=gesture.Window||Native.Focus!=gesture.Focus)throw new InvalidOperationException("Поле изменилось. Исходник остался в буфере.");
            for(int i=0;i<20 && Native.GetClipboardSequenceNumber()==gesture.ClipboardBefore;i++)await Task.Delay(25);
            uint sequence=Native.GetClipboardSequenceNumber();
            Selection target;
            if(sequence==gesture.ClipboardBefore)
            {
                if(occupied){Notify("Corrector занят. Повтори команду после завершения.");return;}
                target=await SelectedText.CaptureDraft(gesture);
            }
            else
            {
                if(!Native.ClipboardFrom(gesture.Window))throw new InvalidOperationException("Буфер изменился в другом приложении. Повтори команду.");
                string cut=ClipboardText.Read()??throw new InvalidOperationException("В буфере нет текста.");
                target=await SelectedText.RestoreCut(gesture,cut);
            }
            string text=target.Text;
            sequence=Native.GetClipboardSequenceNumber();
            if(occupied){Notify("Исходный текст возвращён на место. Ещё идёт загрузка или предыдущая проверка — повтори команду после её завершения.");return;}
            if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("В поле нет текста для проверки.");
            original=text;result=null;copyOriginal.Enabled=true;copyResult.Enabled=false;
            operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);cancel.Enabled=true;
            Notify("Проверяю текст — он остаётся на месте. Не меняй выделение до завершения. Исходник также сохранён в буфере.");
            if(!engine.Ready)await engine.Start(new Progress<string>(s=>{if(!closing)Status(s.Replace('\n',' '));}),operation.Token);
            result=await engine.CorrectDraft(text,operation.Token,correctionMode,(index,count)=>
            {
                partStatus=count>1 ? $" · часть {index}/{count}" : "";
                ShowProgress();
            });
            copyResult.Enabled=true;
            if(gesture.IsCurrent && Native.GetClipboardSequenceNumber()==sequence && await SelectedText.Replace(target,result,clipboardOriginal:text,gesture:gesture))
            {
                Status($"Готово за {watch.Elapsed.TotalSeconds:F0} с");
                Notify("Текст исправлен. Исходник остался в буфере. Чтобы вернуть его, выдели исправленный текст и нажми Ctrl+V.");
            }
            else
            {
                Status("Готово · вставка отменена");
                Notify("Поле, ввод или буфер изменились — ничего не вставлено. Результат можно скопировать через значок C → «Скопировать исправленный текст». Там же доступен исходник.");
            }
        }
        catch(OperationCanceledException){if(ownsOperation){engine.Stop();Status("Проверка отменена");}Notify("Проверка отменена. Исходный текст остаётся на месте и доступен в буфере.");}
        catch(Exception ex){if(ownsOperation)Status("Проверка не выполнена");Notify(ex.Message);}
        finally{if(ownsOperation){busy=false;watch.Stop();cancel.Enabled=false;operation?.Dispose();operation=null;}}
    }
    protected override void ExitThreadCore()
    {
        closing=true;lifetime.Cancel();operation?.Cancel();hook.Dispose();timer.Stop();timer.Dispose();engine.Dispose();tray.Visible=false;tray.Icon?.Dispose();tray.Dispose();dispatcher.Dispose();base.ExitThreadCore();
    }
}
