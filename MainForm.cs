using System.Diagnostics;
using Microsoft.Win32;

namespace Corrector;

internal sealed class MainForm : Form
{
    readonly Engine engine=new();
    readonly CancellationTokenSource lifetime=new();
    CancellationTokenSource? operation;
    readonly NotifyIcon tray;
    readonly TrayMenu menu = new();
    readonly NotificationCard notification;
    readonly Label status=new();
    readonly Label stateTitle=new();
    readonly ProgressBar activity=new();
    readonly Label badge=new();
    readonly Button start=new();
    readonly Button cancel=new();
    readonly CheckBox preview=new();
    readonly CheckBox autostart=new();
    readonly ComboBox hotkey=new();
    readonly RichTextBox original=new(), corrected=new();
    readonly Button test=new(),copy=new();
    readonly bool background;
    bool exiting,busy,initializing,checking;
    string? lastOriginal,lastResult;
    string hotkeyName="Ctrl + Alt + Space";
    readonly Stopwatch timer=new();
    readonly System.Windows.Forms.Timer ticker=new() {Interval=1000};
    static readonly Color Ink=Color.FromArgb(31,42,54), Accent=Color.FromArgb(32,112,95),Muted=Color.FromArgb(100,113,126);

    public MainForm(bool background)
    {
        this.background=background;
        notification=new NotificationCard(Open);
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
        Text="Corrector — русский корректор";ClientSize=new Size(960,740);MinimumSize=new Size(900,700);
        StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(246,248,247);ForeColor=Ink;
        Icon=CorrectorIcon.Load(SystemInformation.IconSize);
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(28),ColumnCount=1,RowCount=7};
        layout.RowStyles.Add(new(SizeType.Absolute,84));layout.RowStyles.Add(new(SizeType.Absolute,142));layout.RowStyles.Add(new(SizeType.Absolute,72));layout.RowStyles.Add(new(SizeType.Absolute,48));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,64));layout.RowStyles.Add(new(SizeType.Absolute,64));
        var header=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};header.ColumnStyles.Add(new(SizeType.Absolute,155));header.ColumnStyles.Add(new(SizeType.Percent,100));header.RowStyles.Add(new(SizeType.Absolute,42));header.RowStyles.Add(new(SizeType.Percent,100));
        var logo=new Label {Text="Corrector",Font=new Font("Segoe UI",20,FontStyle.Bold),Dock=DockStyle.Fill,ForeColor=Accent};header.Controls.Add(logo,0,0);header.SetRowSpan(logo,2);
        header.Controls.Add(new Label {Text="Корректор текста",Font=new Font("Segoe UI",16,FontStyle.Bold),Dock=DockStyle.Fill},1,0);
        header.Controls.Add(new Label {Text="Исправления на твоём компьютере. Текст никуда не отправляется.",Dock=DockStyle.Fill,ForeColor=Muted},1,1);
        layout.Controls.Add(header,0,0);
        var statePanel=new TableLayoutPanel {Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(12),ColumnCount=2};statePanel.ColumnStyles.Add(new(SizeType.Percent,100));statePanel.ColumnStyles.Add(new(SizeType.Absolute,145));
        var stateContent=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};stateContent.RowStyles.Add(new(SizeType.Absolute,38));stateContent.RowStyles.Add(new(SizeType.Percent,100));stateContent.RowStyles.Add(new(SizeType.Absolute,12));
        stateTitle.Text="Подготовка модели";stateTitle.Font=new Font("Segoe UI",15,FontStyle.Bold);stateTitle.ForeColor=Accent;stateTitle.Dock=DockStyle.Fill;
        status.Text="Подготовка…";status.Dock=DockStyle.Fill;status.AutoEllipsis=true;
        activity.Dock=DockStyle.Fill;activity.Style=ProgressBarStyle.Marquee;activity.MarqueeAnimationSpeed=30;activity.Visible=false;
        stateContent.Controls.Add(stateTitle,0,0);stateContent.Controls.Add(status,0,1);stateContent.Controls.Add(activity,0,2);
        start.Text="Повторить запуск";start.Dock=DockStyle.Fill;start.Visible=false;start.Click+=async(_,_)=>await InitializeModel();
        statePanel.Controls.Add(stateContent);statePanel.Controls.Add(start);layout.Controls.Add(statePanel,0,1);
        var settings=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(0,12,0,0)};
        settings.Controls.Add(new Label {Text="Исправить выделение:",AutoSize=true,Margin=new Padding(0,5,10,0)});
        hotkey.DropDownStyle=ComboBoxStyle.DropDownList;hotkey.Width=190;hotkey.Items.AddRange(["Ctrl + Alt + Space","Ctrl + Alt + F9","Ctrl + Shift + F9"]);hotkey.SelectedIndex=0;hotkey.SelectedIndexChanged+=(_,_)=>{if(IsHandleCreated)RegisterKeys();};settings.Controls.Add(hotkey);
        preview.Text="Всегда показывать результат";preview.AutoSize=true;preview.Margin=new Padding(20,5,0,0);settings.Controls.Add(preview);layout.Controls.Add(settings,0,2);
        var intro=new Label {Dock=DockStyle.Fill,Text="Выдели текст и нажми горячую клавишу. Или проверь текст здесь:",ForeColor=Muted,Padding=new Padding(0,8,0,0)};layout.Controls.Add(intro,0,3);
        var editors=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};editors.ColumnStyles.Add(new(SizeType.Percent,50));editors.ColumnStyles.Add(new(SizeType.Percent,50));editors.RowStyles.Add(new(SizeType.Absolute,25));editors.RowStyles.Add(new(SizeType.Percent,100));
        editors.Controls.Add(new Label {Text="ИСХОДНЫЙ ТЕКСТ",ForeColor=Muted,AutoSize=true},0,0);editors.Controls.Add(new Label {Text="РЕЗУЛЬТАТ",ForeColor=Muted,AutoSize=true},1,0);
        original.AccessibleName="Исходный текст";corrected.AccessibleName="Исправленный текст";
        foreach(var box in new[]{original,corrected}){box.Dock=DockStyle.Fill;box.BorderStyle=BorderStyle.None;box.Font=new Font("Segoe UI",12);box.BackColor=Color.White;box.ForeColor=Ink;box.DetectUrls=false;box.Margin=new Padding(0,0,10,0);box.AcceptsTab=true;}
        corrected.ReadOnly=true;original.Text="Привет я незнаю получиться ли у нас встретится завтра если будет дождь мы останемся дома";
        editors.Controls.Add(original,0,1);editors.Controls.Add(corrected,1,1);layout.Controls.Add(editors,0,4);
        var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(0,10,0,0)};
        test.Text="Исправить";test.Width=130;test.Height=32;test.BackColor=Accent;test.ForeColor=Color.White;test.FlatStyle=FlatStyle.Flat;test.Enabled=false;test.Click+=async(_,_)=>await CorrectEditor();
        copy.Text="Копировать";copy.Width=190;copy.Height=32;copy.Enabled=false;copy.Click+=(_,_)=>{try{Clipboard.SetText(corrected.Text);SetStatus("Результат скопирован. Вставь его в нужное поле.");}catch(Exception ex){Error(ex);}};
        cancel.Text="Отменить";cancel.Width=110;cancel.Height=32;cancel.Enabled=false;cancel.Click+=(_,_)=>operation?.Cancel();
        actions.Controls.AddRange([test,copy,cancel]);layout.Controls.Add(actions,0,5);
        var footer=new FlowLayoutPanel {Dock=DockStyle.Fill};autostart.Text="Запускать при входе в Windows";autostart.AutoSize=true;autostart.CheckedChanged+=(_,_)=>{if(!initializing)SetAutostart();};footer.Controls.Add(autostart);
        badge.Text="Gemma 4 · 12B · UD-Q4_K_XL · до 6000 символов";badge.AutoSize=true;badge.ForeColor=Muted;badge.Margin=new Padding(20,4,0,0);footer.Controls.Add(badge);layout.Controls.Add(footer,0,6);Controls.Add(layout);
        menu.Items.Add("Настройки…",null,(_,_)=>{
            var dialog = new SettingsWindow(SettingsStore.Load(), SettingsStore.Autostart);
            new System.Windows.Interop.WindowInteropHelper(dialog).Owner = Handle;
            if (dialog.ShowDialog() == true)
            {
                initializing = true;
                try { autostart.Checked = SettingsStore.Autostart; }
                finally { initializing = false; }
            }
        });
        menu.Items.Add("Открыть корректор",null,(_,_)=>Open());menu.Items.Add("Исправить: горячая клавиша",null,(_,_)=>Notify("Выдели текст в нужной программе и нажми "+hotkeyName));menu.Items.Add("Вернуть исходник: Ctrl + Alt + Backspace",null,(_,_)=>Notify("Выдели последнее исправление и нажми Ctrl + Alt + Backspace."));menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Выгрузить модель из памяти",null,(_,_)=>{if(busy){Notify("Сначала дождись завершения или отмени исправление.");return;}engine.Stop();test.Enabled=false;start.Visible=true;Outcome("Модель выгружена","Горячая клавиша снова запустит её.");});menu.Items.Add("Выход",null,(_,_)=>Quit());
        tray=new NotifyIcon {Icon=CorrectorIcon.ForTray(),Visible=true,Text="Corrector — подготовка модели",ContextMenuStrip=menu};tray.DoubleClick+=(_,_)=>Open();
        ticker.Tick+=(_,_)=>{if(checking){stateTitle.Text=$"Проверяю текст · {timer.Elapsed:mm\\:ss}";Text=$"Проверка {timer.Elapsed:mm\\:ss} — Corrector";}};ticker.Start();
        LoadSettings();preview.CheckedChanged+=(_,_)=>SaveSettings();
        Shown+=async(_,_)=>{if(background)Hide();RegisterKeys();await InitializeModel();};
        FormClosing+=(_,e)=>{if(!exiting){e.Cancel=true;Hide();Notify("Corrector работает в фоне. Исправить: "+hotkeyName);}};
    }
    void RegisterKeys()
    {
        Native.UnregisterHotKey(Handle,1);Native.UnregisterHotKey(Handle,2);
        uint mod=hotkey.SelectedIndex==2?6u:3u,key=hotkey.SelectedIndex==0?0x20u:0x78u;
        hotkeyName=hotkey.Text;
        if(!Native.RegisterHotKey(Handle,1,mod|0x4000,key))MessageBox.Show(this,"Эта горячая клавиша занята другой программой. Выбери другое сочетание.","Corrector");
        if(!Native.RegisterHotKey(Handle,2,3|0x4000,0x08))Notify("Не удалось назначить отмену Ctrl + Alt + Backspace.");
        SaveSettings();
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x0312){if(m.WParam.ToInt32()==1)_=CorrectSelection();else if(m.WParam.ToInt32()==2)_=UndoSelection();}
        base.WndProc(ref m);
    }
    void SetStatus(string text){status.Text=text;tray.Text="Corrector — "+(checking?"проверяю текст":initializing?"загружаю модель":engine.Ready?"готово":"модель не загружена");}
    void Outcome(string title,string detail,bool error=false){checking=false;stateTitle.Text=title;stateTitle.ForeColor=error?Color.Firebrick:Accent;SetStatus(detail);}
    void ShowActivity(string title){stateTitle.Text=title;stateTitle.ForeColor=Accent;activity.Visible=true;activity.MarqueeAnimationSpeed=30;test.Text="Проверяю…";original.ReadOnly=true;}
    void Notify(string text){if(!exiting)notification.Present("Corrector",text);}
    void Error(Exception ex){Outcome("Не удалось выполнить проверку",ex.Message,true);Notify(ex.Message);}
    void Open(){Show();WindowState=FormWindowState.Normal;Activate();}
    async Task InitializeModel()
    {
        if(busy)return;busy=true;initializing=true;start.Visible=false;test.Enabled=false;
        ShowActivity("Подготовка модели");test.Text="Загрузка…";
        operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);cancel.Enabled=true;
        try{await engine.Start(new Progress<string>(SetStatus),operation.Token);Outcome("Готово к проверке","Вставь текст слева и нажми «Исправить» или используй "+hotkeyName);Notify("Готово. Выдели русский текст и нажми "+hotkeyName);}
        catch(OperationCanceledException){Outcome("Загрузка отменена","Загруженная часть сохранена. Можно повторить запуск.");start.Visible=true;}
        catch(Exception ex){Error(ex);start.Visible=true;}
        finally{initializing=false;Finish();}
    }
    async Task<string> Run(string text)
    {
        if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("Вставь или выдели текст для проверки.");
        if(text.Length>Correction.MaxChars)throw new ArgumentException("Для одной проверки выдели не больше 6000 символов.");
        operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);cancel.Enabled=true;test.Enabled=false;copy.Enabled=false;timer.Restart();
        corrected.Clear();ShowActivity("Проверяю текст · 00:00");
        if(!engine.Ready){initializing=true;stateTitle.Text="Подготовка модели";try{await engine.Start(new Progress<string>(SetStatus),operation.Token);}finally{initializing=false;}}
        checking=true;timer.Restart();
        SetStatus("Орфография, грамматика и пунктуация. Дождись результата справа.\nБольшой текст может занять несколько минут. Проверку можно отменить.");
        return await engine.Correct(text,operation.Token);
    }
    void Finish(){busy=false;checking=false;timer.Stop();activity.Visible=false;activity.MarqueeAnimationSpeed=0;Text="Corrector — русский корректор";test.Text="Исправить";original.ReadOnly=false;cancel.Enabled=false;test.Enabled=engine.Ready;copy.Enabled=corrected.TextLength>0;operation?.Dispose();operation=null;SetStatus(status.Text);SaveSettings();}
    void Cancelled(){engine.Stop();start.Visible=true;Outcome("Проверка отменена","Исходный текст сохранён. Следующий запуск снова загрузит модель.");}
    async Task CorrectEditor()
    {
        if(busy)return;busy=true;string source=original.Text;
        try{corrected.Text=await Run(source);Outcome(corrected.Text==source?"Готово — ошибок не найдено":"Готово — текст исправлен",$"Время: {timer.Elapsed.TotalSeconds:F1} с. Результат справа; нажми «Копировать», чтобы забрать его.");}
        catch(OperationCanceledException){Cancelled();}catch(Exception ex){Error(ex);}finally{Finish();}
    }
    async Task CorrectSelection()
    {
        if(busy){Notify("Уже выполняется операция. Дождись завершения или нажми «Отменить».");return;}
        busy=true;
        try
        {
            var selection=await SelectedText.Capture();
            if(selection.Text.Length>Correction.MaxChars)throw new ArgumentException("Выдели не больше 6000 символов.");
            Notify("Проверяю выделенный текст. Дождись завершения; не меняй окно и выделение для автоматической замены.");
            var result=await Run(selection.Text);
            if(result==selection.Text){corrected.Text=result;Outcome("Готово — ошибок не найдено",$"Проверено за {timer.Elapsed.TotalSeconds:F1} с. Текст оставлен без изменений.");Notify("Ошибок не найдено.");return;}
            bool review=preview.Checked||Correction.NeedsReview(selection.Text,result);
            if(!review && await SelectedText.Replace(selection,result))
            {
                lastOriginal=selection.Text;lastResult=result;
                Outcome("Готово — текст заменён",$"Исправлено за {timer.Elapsed.TotalSeconds:F1} с.");
                Notify("Текст исправлен. Вернуть исходник: выдели результат и нажми Ctrl + Alt + Backspace.");
            }
            else
            {
                Outcome("Готово — нужен просмотр",review?"Результат справа. Исходный текст не заменён.":"Окно или выделение изменилось. Результат справа, исходник не заменён.");
                // Never steal focus while the user is typing elsewhere.
                if(Native.GetForegroundWindow()==selection.Window && Native.Focus==selection.Focus && Native.InputTime==selection.InputTime)Open();else Notify("Результат готов. Открой Corrector в трее, чтобы просмотреть и скопировать его.");
            }
            if(selection.Window!=Handle)original.Text=selection.Text;
            corrected.Text=result;
        }
        catch(OperationCanceledException){Cancelled();}catch(Exception ex){Error(ex);}finally{Finish();}
    }
    async Task UndoSelection()
    {
        if(busy)return;
        if(lastOriginal==null||lastResult==null){Notify("Пока нечего отменять. Можно использовать Ctrl + Z в редакторе.");return;}
        busy=true;
        try
        {
            var selection=await SelectedText.Capture();
            if(selection.Text!=lastResult){Notify("Для отмены выдели последнее исправление целиком.");return;}
            if(await SelectedText.Replace(selection,lastOriginal)){lastOriginal=null;lastResult=null;Notify("Исходный текст восстановлен.");}
        }
        catch(Exception ex){Error(ex);}finally{busy=false;}
    }
    void LoadSettings()
    {
        initializing=true;
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(SettingsStore.PreferencesKey);
            hotkey.SelectedIndex=Math.Clamp((int?)key?.GetValue("Hotkey")??0,0,2);preview.Checked=(int?)key?.GetValue("Preview")==1;
            autostart.Checked=SettingsStore.Autostart;
        }
        finally{initializing=false;}
    }
    void SaveSettings(){if(initializing)return;using var key=Registry.CurrentUser.CreateSubKey(SettingsStore.PreferencesKey);key.SetValue("Hotkey",hotkey.SelectedIndex);key.SetValue("Preview",preview.Checked?1:0);}
    void SetAutostart()
    {
        try{SettingsStore.SetAutostart(autostart.Checked);}
        catch(Exception ex){Error(ex);}
    }
    void Quit(){SaveSettings();exiting=true;lifetime.Cancel();operation?.Cancel();ticker.Stop();Native.UnregisterHotKey(Handle,1);Native.UnregisterHotKey(Handle,2);tray.Visible=false;engine.Dispose();tray.Icon?.Dispose();tray.Dispose();menu.Dispose();notification.Dispose();var windowIcon=Icon;Close();windowIcon?.Dispose();}
}
