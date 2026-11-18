using System.Diagnostics;

namespace Corrector;

internal sealed class TrayApp : ApplicationContext
{
    readonly Engine engine=new();
    readonly Control dispatcher=new();
    readonly NotifyIcon tray;
    readonly TrayMenu menu = new();
    readonly NotificationCard notification = new();
    readonly ToolStripMenuItem checkUpdates = new("Проверить обновления");
    readonly ToolStripMenuItem openRelease = new() { Visible = false };
    Version? availableVersion;
    readonly DoubleCutHook hook;
    readonly CancellationTokenSource lifetime=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly Stopwatch watch=new();
    readonly ToolStripMenuItem status=new("Загрузка модели…"),cancel=new("Отменить проверку"),copyOriginal=new("Скопировать исходник"),copyResult=new("Скопировать исправленный текст");
    CancellationTokenSource? operation;
    AppSettings settings = SettingsStore.Load();
    CorrectionMode correctionMode = CorrectionMode.Auto;
    string? original,result;
    bool busy,loading,closing;
    string partStatus="";
    TranslationPopup? popup;
    SettingsWindow? settingsWindow;
    bool translating;
    public TrayApp()
    {
        _=dispatcher.Handle;
        tray=new NotifyIcon{Icon=CorrectorIcon.ForTray(),Visible=true,Text="Corrector — загрузка модели"};
        menu.StatusItem=status;status.Enabled=false;cancel.Enabled=false;copyOriginal.Enabled=false;copyResult.Enabled=false;
        menu.Items.Add(status);menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings()).Tag="\uE713";
        checkUpdates.Tag = "\uE72C";
        checkUpdates.Click += async (_, _) => await CheckUpdates(manual: true);
        openRelease.Tag = "\uE8A7";
        openRelease.Click += (_, _) => {
            try { Process.Start(new ProcessStartInfo($"{ReleaseUpdates.ReleasesUrl}v{availableVersion}") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Notify("Не удалось открыть GitHub в браузере.", "Обновления Corrector", NoticeKind.Warning); }
        };
        menu.Items.Add(checkUpdates);
        menu.Items.Add(openRelease);
        menu.Items.Add("Как пользоваться",null,(_,_)=>Notify($"Дважды нажми Ctrl+X для корректуры. Для вычисления допиши = после примера. Для перевода выдели {settings.TranslationSource} текст и нажми ту же комбинацию.")).Tag="\uE897";
        var modeMenu = new ToolStripMenuItem("Режим корректуры") { Tag="\uE713" };
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
        cancel.Tag="\uE711";copyOriginal.Tag="\uE8C8";copyResult.Tag="\uE8F3";
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(cancel);menu.Items.Add(copyOriginal);menu.Items.Add(copyResult);
        cancel.Click+=(_,_)=>operation?.Cancel();copyOriginal.Click+=(_,_)=>Copy(original,"Исходник в буфере. Вставь его через Ctrl+V.");copyResult.Click+=(_,_)=>Copy(result,"Текст в буфере. Вставь его через Ctrl+V.");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Загрузить модель",null,async(_,_)=>await Prepare()).Tag="\uE896";
        menu.Items.Add("Выгрузить модель",null,(_,_)=>{if(busy||loading)return;engine.Stop();Status("Модель выгружена");}).Tag="\uE898";
        menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Выход",null,(_,_)=>ExitThread()).Tag="\uE7E8";tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>Notify(status.Text??"Corrector работает в фоне");
        hook=new DoubleCutHook();hook.Detected+=gesture=>dispatcher.BeginInvoke((Action)(async()=>await CorrectCut(gesture)));
        timer.Tick+=(_,_)=>{if(busy)ShowProgress();};timer.Start();
        dispatcher.BeginInvoke((Action)(async()=>await Prepare()));
        dispatcher.BeginInvoke((Action)(async()=>await CheckUpdates(manual: false)));
    }
    async Task CheckUpdates(bool manual)
    {
        if (closing || !checkUpdates.Enabled) return;
        checkUpdates.Enabled = false;
        checkUpdates.Text = "Проверяю обновления…";
        try
        {
            var latest = await ReleaseUpdates.Latest(lifetime.Token);
            if (closing) return;
            availableVersion = latest > ReleaseUpdates.Installed ? latest : null;
            openRelease.Visible = availableVersion != null;
            if (availableVersion != null)
            {
                openRelease.Text = $"Открыть релиз {latest} на GitHub";
                Notify("Установщик доступен через меню значка C.", $"Доступен Corrector {latest}", NoticeKind.Success);
            }
            else if (manual)
                Notify($"Установлена актуальная версия {ReleaseUpdates.Installed.ToString(3)}.", "Обновления Corrector", NoticeKind.Success);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (manual && !closing) Notify("Не удалось проверить версию на GitHub. Попробуй позже.", "Обновления Corrector", NoticeKind.Warning);
        }
        finally
        {
            if (!closing) { checkUpdates.Text = "Проверить обновления"; checkUpdates.Enabled = true; }
        }
    }
    void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        try
        {
            settingsWindow = new SettingsWindow(settings, SettingsStore.Autostart);
            if (settingsWindow.ShowDialog() == true) settings = settingsWindow.Saved;
        }
        catch (Exception ex) { Notify(ex.Message, "Настройки Corrector", NoticeKind.Warning); }
        finally { settingsWindow = null; }
    }

    void Status(string text){status.Text=text;string tip="Corrector — "+text;tray.Text=tip[..Math.Min(63,tip.Length)];}
    void Notify(string text, string title = "Corrector", NoticeKind kind = NoticeKind.Info)
    {
        if (!closing) notification.Present(title, text, kind);
    }
    void ShowProgress()=>Status($"{(translating ? "Перевожу" : "Проверяю текст")}{partStatus} · {watch.Elapsed:mm\\:ss}");
    void Copy(string? text,string message){if(text==null)return;try{ClipboardText.Write(text);Notify(message);}catch(Exception ex){Notify(ex.Message, "Ошибка Corrector", NoticeKind.Warning);}}
    async Task Prepare()
    {
        if(busy||loading||closing)return;loading=true;Status("Загрузка модели…");
        try{await engine.Start(new Progress<string>(s=>{if(loading&&!closing)Status(s.Replace('\n',' '));}),lifetime.Token);Status("Готово · Ctrl+X дважды");Notify("Дважды нажми Ctrl+X для проверки текста.", "Corrector готов", NoticeKind.Success);}
        catch(OperationCanceledException){}catch(Exception ex){Status("Не удалось загрузить модель");Notify(ex.Message, "Ошибка Corrector", NoticeKind.Warning);}
        finally{loading=false;}
    }
    static bool IsMax(IntPtr window)
    {
        using var process = Process.GetProcessById((int)Native.ProcessOf(window));
        return process.ProcessName.Equals("max", StringComparison.OrdinalIgnoreCase);
    }

    async Task CorrectCut(CutGesture gesture)
    {
        if(closing)return;
        var preferences = settings;
        bool occupied=busy||loading,ownsOperation=!occupied;
        if(ownsOperation){busy=true;partStatus="";watch.Restart();}
        try
        {
            await Native.ReleaseModifiers();
            if(Native.GetForegroundWindow()!=gesture.Window||Native.Focus!=gesture.Focus)throw new InvalidOperationException("Поле изменилось. Исходник остался в буфере.");
            for(int i=0;i<20 && Native.GetClipboardSequenceNumber()==gesture.ClipboardBefore;i++)await Task.Delay(25);
            uint sequence=Native.GetClipboardSequenceNumber();
            string? cut = null;
            string? visible = await Task.Run(() => DraftField.VisibleSelection(gesture));
            if (!gesture.IsCurrent || sequence != Native.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Поле, ввод или буфер изменились.");
            if (sequence != gesture.ClipboardBefore)
            {
                if (!gesture.IsCurrent || !Native.ClipboardFrom(gesture.Window))
                    throw new InvalidOperationException("Поле, ввод или буфер изменились. Повтори команду.");
                cut = ClipboardText.Read() ?? throw new InvalidOperationException("В буфере нет текста.");
                if (visible == null || !DraftField.SameText(cut,visible))
                {
                    Native.CtrlKey(0x5A);
                    await Task.Delay(80);
                    visible=null;
                }
                else cut=null; // A read-only view can copy its selection on Ctrl+X.
            }
            if (occupied) { Notify("Corrector занят. Повтори команду после завершения."); return; }
            if (!gesture.IsCurrent || sequence != Native.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Поле, ввод или буфер изменились.");
            KeyboardSelection? keyboardField=null;
            DraftField? field=null;
            string text;
            bool explicitSelection;
            if (visible != null && Calculator.Expression(visible) == null && preferences.ShouldTranslate(visible,true))
            {
                text=visible;
                explicitSelection=true;
            }
            else if (cut!=null && ObsidianSelection.IsObsidian(gesture.Window) && Clipboard.GetData(DataFormats.Html) is string html && html.Contains("<!-- obsidian -->",StringComparison.Ordinal))
            {
                var obsidian=new ObsidianSelection(gesture,sequence);
                try { await obsidian.Prepare(cut); }
                catch { obsidian.RestoreClipboard(cut); throw; }
                keyboardField=obsidian;
                text=obsidian.Text;
                explicitSelection=obsidian.ExplicitSelection;
                sequence=obsidian.Sequence;
            }
            else
            {
                try { field = await Task.Run(() => DraftField.Select(gesture, cut, sequence)); }
                catch (NoDraftFieldException) when (IsMax(gesture.Window))
                {
                    var max = new MaxSelection(gesture, sequence);
                    var saved = cut == null ? SelectedText.Snapshot() : null;
                    try { await max.Prepare(cut); }
                    catch
                    {
                        if (cut != null) max.RestoreClipboard(cut);
                        else if (max.Sequence != sequence && max.Sequence == Native.GetClipboardSequenceNumber())
                        {
                            if (saved == null) Clipboard.Clear();
                            else Clipboard.SetDataObject(saved, true);
                        }
                        throw;
                    }
                    keyboardField = max;
                }
                catch (NoDraftFieldException) when (cut==null && (string.IsNullOrEmpty(visible) || preferences.ShouldTranslate(visible,true)))
                {
                    // Some browser/PDF viewers only expose their selection through Copy.
                }
                if (keyboardField != null)
                {
                    text = keyboardField.Text;
                    explicitSelection = keyboardField.ExplicitSelection;
                    sequence = keyboardField.Sequence;
                }
                else
                {
                    if (!gesture.IsCurrent || sequence != Native.GetClipboardSequenceNumber())
                        throw new InvalidOperationException("Поле, ввод или буфер изменились.");
                    var target = await SelectedText.Capture(gesture, keepCopy:field!=null);
                    text = target.Text;
                    explicitSelection=field?.ExplicitSelection ?? true;
                    if (field==null && !preferences.ShouldTranslate(text,true))
                        throw new InvalidOperationException($"Для перевода выдели {preferences.TranslationSource} текст.");
                    if (field!=null && !DraftField.SameText(field.Text, text))
                        throw new InvalidOperationException("Редактор выделил другой текст. Проверка остановлена.");
                    sequence=Native.GetClipboardSequenceNumber();
                }
            }
            original=text;result=null;copyOriginal.Enabled=true;copyResult.Enabled=false;
            operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);cancel.Enabled=true;
            if ((keyboardField != null || field != null) && Calculator.Expression(text) is string expression)
            {
                string answer;
                try { answer = Calculator.Evaluate(expression); }
                catch (InvalidOperationException ex)
                {
                    if (!explicitSelection && gesture.IsCurrent && sequence == Native.GetClipboardSequenceNumber())
                        Native.Chord(0x27);
                    Status("Проверь выражение");
                    Notify(ex.Message, "Калькулятор", NoticeKind.Warning);
                    return;
                }
                result = answer;
                copyResult.Text = "Скопировать ответ";
                copyResult.Enabled = true;
                bool appended = keyboardField != null
                    ? await keyboardField.AppendAnswer(answer, operation.Token)
                    : await SelectedText.AppendAnswer(gesture, field!, answer, sequence, operation.Token);
                Status(appended ? "Вычислено" : "Вставка не подтверждена");
                if (!appended) Notify("Ответ доступен в меню Corrector.", "Проверь текст в поле", NoticeKind.Warning);
                return;
            }
            translating=preferences.ShouldTranslate(text,explicitSelection);
            cancel.Text=translating ? "Отменить перевод" : "Отменить проверку";
            copyResult.Text=translating ? "Скопировать перевод" : "Скопировать исправленный текст";
            if (translating)
            {
                popup?.Close();
                popup=new TranslationPopup(preferences.TranslationTitle);
                var pending=operation;
                popup.Closed+=(_,_)=>{popup=null; if (operation==pending) pending.Cancel();};
                popup.Show();
            }
            else Notify("Сохрани выделение до конца проверки.", "Проверяю текст", NoticeKind.Working);
            if(!engine.Ready)await engine.Start(new Progress<string>(s=>{if(!closing)Status(s.Replace('\n',' '));}),operation.Token);
            void Progress(int index,int count)
            {
                partStatus=count>1 ? $" · часть {index}/{count}" : "";
                ShowProgress();
                if (translating && count>1) popup?.Display($"Перевожу · часть {index}/{count}");
            }
            if (translating)
            {
                result=await engine.Translate(text,operation.Token,Progress,preferences);
                operation.Token.ThrowIfCancellationRequested();
                popup?.Display(result,true);
                copyResult.Enabled=true;
                Status("Перевод готов");
                return;
            }
            result=await engine.CorrectDraft(text,operation.Token,correctionMode,Progress,preferences);
            copyResult.Enabled=true;
            if(keyboardField!=null ? await keyboardField.ReplaceVerified(result,operation.Token) :
                await SelectedText.ReplaceVerified(gesture, field!, text, result, sequence, operation.Token))
            {
                Status($"Готово за {watch.Elapsed.TotalSeconds:F0} с");
                bool entered = keyboardField?.EnterAfterPaste ?? field!.EnterAfterPaste;
                Notify(entered ? "Enter нажат. Исходник в буфере." : "Исходник в буфере.",
                    "Текст проверен", NoticeKind.Success);
            }
            else
            {
                Status("Вставка не подтверждена");
                Notify("Обе версии текста доступны в меню Corrector.", "Проверь текст в поле", NoticeKind.Warning);
            }
        }
        catch(OperationCanceledException){if(ownsOperation){if(!translating)engine.Stop();Status(translating?"Перевод отменён":"Проверка отменена");if(translating)popup?.Close();} }
        catch(Exception ex){if(ownsOperation){Status(translating?"Перевод не выполнен":"Проверка не выполнена");if(translating)popup?.Display(ex.Message);}Notify(ex.Message, "Ошибка Corrector", NoticeKind.Warning);}
        finally{if(ownsOperation){busy=false;translating=false;watch.Stop();cancel.Enabled=false;operation?.Dispose();operation=null;}}
    }
    protected override void ExitThreadCore()
    {
        closing=true;settingsWindow?.Close();lifetime.Cancel();operation?.Cancel();popup?.Close();hook.Dispose();timer.Stop();timer.Dispose();engine.Dispose();tray.Visible=false;tray.Icon?.Dispose();tray.Dispose();menu.Dispose();notification.Dispose();dispatcher.Dispose();base.ExitThreadCore();
    }
}
