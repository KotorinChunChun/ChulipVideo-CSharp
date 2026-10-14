using System.Diagnostics;
using ChulipVideo.Core;
using ChulipVideo.Media;
using ChulipVideo.Export;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.UI;

public sealed class MainForm : BrandedForm
{
    private readonly SettingsStore store;
    private AppSettings settings;
    private readonly VideoCanvas canvas = new() { Dock=DockStyle.Fill };
    private readonly Timeline timeline = new() { Dock=DockStyle.Fill, Margin=Padding.Empty };
    private readonly Label info = new() { Dock=DockStyle.Fill, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft, Text="動画を選択、またはここへドロップしてください。" };
    private readonly Label status = new() { Dock=DockStyle.Fill, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft, Text="準備完了" };
    private readonly NumericUpDown start=Number("開始秒",0,864000,3), position=Number("現在秒",0,864000,3), end=Number("終了秒",0,864000,3);
    private readonly NumericUpDown cropX=Number("X",-16384,32768), cropY=Number("Y",-16384,32768), cropW=Number("幅",1,16384), cropH=Number("高さ",1,16384);
    private readonly NumericUpDown speed=Number("再生速度",-9.9m,9.9m,1);
    private readonly CheckBox range=new(){Text="区間",AutoSize=true},loop=new(){Text="ループ",AutoSize=true},pingpong=new(){Text="往復",AutoSize=true};
    private readonly CheckBox outside=new(){Text="枠外を許可",AutoSize=true},moveLock=new(){Text="移動固定",AutoSize=true},sizeLock=new(){Text="サイズ固定",AutoSize=true};
    private readonly CheckBox pointer=new(){Text="マウス演出",AutoSize=true},keys=new(){Text="キー字幕",AutoSize=true},burn=new(){Text="動画に演出を焼込",AutoSize=true};
    private readonly CheckBox stable=new(){Text="静止フレームのみ",AutoSize=true},duplicates=new(){Text="同一画像を除外",AutoSize=true};
    private readonly ComboBox presets=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=190,AccessibleName="解像度プリセット"};
    private readonly Button play=new(){Text="▶ 再生",AutoSize=true,AccessibleName="再生・停止"};
    private readonly Button cancel=new(){Text="書出し中断",AutoSize=true,Enabled=false};
    private readonly ProgressBar progress=new(){Width=150,Height=22};
    private readonly List<Control> busyControls=[];
    private readonly ToolTip tips=new();
    private readonly System.Windows.Forms.Timer timer=new(){Interval=30};
    private readonly Stopwatch clock=new();
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource mediaLifetime=new();
    private CancellationTokenSource? exportCancel;
    private FfmpegService? media;
    private MediaInfo? current;
    private PreviewReader? preview;
    private EventLog events=EventLog.Load("");
    private Bitmap? rawPreview;
    private readonly List<CropRect> history=[];
    private int historyIndex=-1, seekVersion;
    private bool syncing,rendering,playing,loading,exporting,closing,shutdownComplete;
    private double currentTime,rangeStart,rangeEnd,playOrigin,previewTime;
    private Task? exportTask;

    public MainForm(SettingsStore store,AppSettings initialSettings,string? video)
    {
        this.store=store; settings=initialSettings;
        Text=WindowTitle.Create(); MinimumSize=new Size(800,650); Size=new Size(settings.WindowWidth,settings.WindowHeight);
        StartPosition=FormStartPosition.CenterScreen; AllowDrop=true; KeyPreview=true;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(8)};
        foreach(var row in new[]{40F,28F,-1F,40F,40F,175F,76F,26F})layout.RowStyles.Add(row<0?new RowStyle(SizeType.Percent,100):new RowStyle(SizeType.Absolute,row));
        var toolbar=Flow();
        AddButton(toolbar,"動画を選択",()=>ChooseVideo(),"Ctrl+O",true);
        AddButton(toolbar,"再読込",()=>RunUiAsync(()=>LoadAsync(current?.Path)),"動画・設定・TSVを読み直します",true);
        AddButton(toolbar,"設定を保存",()=>SaveVideo(),"動画別設定を保存",true);
        AddButton(toolbar,"フォルダー",()=>OpenFolder(),null,true);
        AddButton(toolbar,"設定",()=>OpenSettings(),null,true);
        AddButton(toolbar,"ヘルプ",()=>ShowHelp());
        layout.Controls.Add(toolbar,0,0);layout.Controls.Add(info,0,1);layout.Controls.Add(canvas,0,2);layout.Controls.Add(timeline,0,3);
        var playback=Flow();play.Click+=(_,_)=>TogglePlay(); playback.Controls.Add(play);busyControls.Add(play);
        AddButton(playback,"|◀",()=>Seek(0),"Ctrl+Home",true); AddButton(playback,"−1秒",()=>Seek(currentTime-1),"Ctrl+←",true);
        AddButton(playback,"+1秒",()=>Seek(currentTime+1),"Ctrl+→",true); AddButton(playback,"▶|",()=>Seek(current?.LastFrameTime??0),"Ctrl+End",true);
        AddLabel(playback,"速度");playback.Controls.Add(speed);playback.Controls.AddRange([range,loop,pingpong]);
        layout.Controls.Add(playback,0,4);
        var tabs=new TabControl{Dock=DockStyle.Fill};var edit=new TabPage("範囲・クロップ");var exportOptions=new TabPage("画像抽出・演出");
        tabs.TabPages.AddRange([edit,exportOptions]);
        var editLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};
        for(int i=0;i<4;i++)editLayout.RowStyles.Add(new RowStyle(SizeType.Percent,25));
        var times=Flow();AddLabel(times,"開始");times.Controls.Add(start);AddButton(times,"現在→開始",()=>SetRange(currentTime,rangeEnd));AddLabel(times,"現在");times.Controls.Add(position);AddLabel(times,"終了");times.Controls.Add(end);AddButton(times,"現在→終了",()=>SetRange(rangeStart,currentTime));
        editLayout.Controls.Add(times,0,0);
        var crop=Flow();foreach(var pair in new[]{("X",cropX),("Y",cropY),("幅",cropW),("高さ",cropH)}){AddLabel(crop,pair.Item1);crop.Controls.Add(pair.Item2);}crop.Controls.Add(outside);
        editLayout.Controls.Add(crop,0,1);
        var presetRow=Flow();presetRow.Controls.Add(presets);AddButton(presetRow,"適用",()=>ApplyPreset());AddButton(presetRow,"登録",()=>SavePreset());AddButton(presetRow,"削除",()=>DeletePreset());presetRow.Controls.Add(moveLock);presetRow.Controls.Add(sizeLock);
        editLayout.Controls.Add(presetRow,0,2);
        var view=Flow();AddButton(view,"元に戻す",()=>Undo(),"Ctrl+Z");AddButton(view,"やり直し",()=>Redo(),"Ctrl+Y");AddButton(view,"全画面を選択",()=>SetCrop(current is null?new(0,0,1,1):CropRect.Full(current)));AddButton(view,"表示リセット",()=>canvas.ResetView(),"中ボタンをダブルクリック");AddLabel(view,"左:選択/移動  枠:サイズ  中:パン  ホイール:拡大");
        editLayout.Controls.Add(view,0,3);edit.Controls.Add(editLayout);
        var optionRows=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(8)};
        var option1=Flow();option1.Height=32;option1.Width=700;option1.Controls.AddRange([stable,duplicates]);optionRows.Controls.Add(option1);
        var option2=Flow();option2.Height=32;option2.Width=700;option2.Controls.AddRange([pointer,keys,burn]);optionRows.Controls.Add(option2);
        optionRows.Controls.Add(new Label{AutoSize=true,Text="静止抽出は前後フレームの完全一致を判定します。圧縮率・字幕出力・演出の外観は「設定」で変更。"});
        exportOptions.Controls.Add(optionRows);layout.Controls.Add(tabs,0,5);busyControls.Add(tabs);busyControls.Add(timeline);busyControls.Add(canvas);
        var output=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};output.RowStyles.Add(new(SizeType.Percent,50));output.RowStyles.Add(new(SizeType.Percent,50));
        var outputButtons=Flow();AddButton(outputButtons,"画像をコピー",()=>RunUiAsync(CopyAsync),"Ctrl+C",true);AddButton(outputButtons,"画像を保存",()=>RunUiAsync(SaveImageAsync),"Ctrl+S",true);AddButton(outputButtons,"PNG連番",()=>BeginExport(ExportKind.PngSequence),null,true);AddButton(outputButtons,"MP4保存",()=>BeginExport(ExportKind.Mp4),null,true);AddButton(outputButtons,"GIF保存",()=>BeginExport(ExportKind.Gif),null,true);
        output.Controls.Add(outputButtons,0,0);var progressRow=Flow();progressRow.Controls.Add(progress);progressRow.Controls.Add(cancel);AddLabel(progressRow,"出力範囲: 開始を含み終了を含まない / 音声なし");output.Controls.Add(progressRow,0,1);layout.Controls.Add(output,0,6);layout.Controls.Add(status,0,7);Controls.Add(layout);
        cancel.Click+=(_,_)=>exportCancel?.Cancel();
        timeline.Seek+=Seek;timeline.StartChanged+=v=>SetRange(v,rangeEnd);timeline.EndChanged+=v=>SetRange(rangeStart,v);
        start.ValueChanged+=(_,_)=>{if(!syncing)SetRange((double)start.Value,rangeEnd);};end.ValueChanged+=(_,_)=>{if(!syncing)SetRange(rangeStart,(double)end.Value);};position.ValueChanged+=(_,_)=>{if(!syncing)Seek((double)position.Value);};
        foreach(var n in new[]{cropX,cropY,cropW,cropH})n.ValueChanged+=(_,_)=>{if(!syncing)SetCrop(new((int)cropX.Value,(int)cropY.Value,(int)cropW.Value,(int)cropH.Value));};
        canvas.CropChanged+=(_,_)=>{SyncCrop();Redraw();};canvas.CropCommitted+=(_,_)=>RememberCrop();canvas.ViewChanged+=(_,_)=>UpdateInfo();
        outside.CheckedChanged+=(_,_)=>{canvas.AllowOutside=outside.Checked;if(!syncing)SetCrop(canvas.Crop);};
        moveLock.CheckedChanged+=(_,_)=>{canvas.MoveLocked=moveLock.Checked;cropX.Enabled=cropY.Enabled=!moveLock.Checked;};
        sizeLock.CheckedChanged+=(_,_)=>{canvas.SizeLocked=sizeLock.Checked;cropW.Enabled=cropH.Enabled=presets.Enabled=!sizeLock.Checked;};
        canvas.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.XButton1)Seek(currentTime-1);if(e.Button==MouseButtons.XButton2)Seek(currentTime+1);};
        speed.ValueChanged+=(_,_)=>{settings.Speed=(double)speed.Value;ResetClock();};
        range.CheckedChanged+=(_,_)=>{settings.RangeOnly=range.Checked;StopPlaying();};loop.CheckedChanged+=(_,_)=>settings.Loop=loop.Checked;pingpong.CheckedChanged+=(_,_)=>settings.PingPong=pingpong.Checked;
        pointer.CheckedChanged+=(_,_)=>{if(syncing)return;settings.Overlay.ShowPointer=pointer.Checked;settings.Overlay.ShowClicks=pointer.Checked;Redraw();};keys.CheckedChanged+=(_,_)=>{if(syncing)return;settings.Overlay.ShowKeys=keys.Checked;Redraw();};burn.CheckedChanged+=(_,_)=>settings.BurnOverlay=burn.Checked;
        stable.CheckedChanged+=(_,_)=>settings.StableOnly=stable.Checked;duplicates.CheckedChanged+=(_,_)=>settings.SkipDuplicates=duplicates.Checked;
        DragEnter+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=(_,e)=>{if(e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length>0&&!exporting&&!loading)RunUiAsync(()=>LoadAsync(paths[0]));};
        timer.Tick+=(_,_)=>PlaybackTick(); timer.Start();
        ApplySettings();
        Shown+=(_,_)=>{if(store.Warning is not null)Status(store.Warning);var path=video??settings.LastVideoPath;if(File.Exists(path))RunUiAsync(()=>LoadAsync(path));};
    }

    private static NumericUpDown Number(string label,decimal min,decimal max,int decimals=0) => new(){Minimum=min,Maximum=max,DecimalPlaces=decimals,Increment=decimals>0?0.1m:1,Width=decimals>0?90:76,AccessibleName=label,ThousandsSeparator=false};
    private static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,Padding=new Padding(0,2,0,0)};
    private static void AddLabel(Control parent,string text)=>parent.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(3,7,3,0)});
    private void AddButton(Control parent,string text,Action action,string? tip=null,bool busy=false)
    {var b=new Button{Text=text,AutoSize=true,Margin=new Padding(2,1,2,1),Padding=new Padding(2,0,2,0)};b.Click+=(_,_)=>action();parent.Controls.Add(b);if(tip is not null)tips.SetToolTip(b,tip);if(busy)busyControls.Add(b);}
    private async void RunUiAsync(Func<Task> action)
    {try{await action();}catch(OperationCanceledException){if(!closing)Status("中断しました。");}catch(Exception ex){if(!closing)ShowError(ex);}}
    private void Status(string message){if(!IsDisposed)status.Text=message;}
    private void ShowError(Exception ex){Status(ex.Message);MessageBox.Show(this,ex.Message,WindowTitle.Create("エラー"),MessageBoxButtons.OK,MessageBoxIcon.Error);}
    private void ChooseVideo()
    {using var dialog=new OpenFileDialog{Filter="動画|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.gif|すべてのファイル|*.*",CheckFileExists=true};if(dialog.ShowDialog(this)==DialogResult.OK)RunUiAsync(()=>LoadAsync(dialog.FileName));}
    private FfmpegService GetMedia()
    {var path=FfmpegService.FindExecutable(settings.FfmpegPath);if(path is null)throw new FileNotFoundException("FFmpegが見つかりません。「設定」でffmpeg.exeを選択してください。");return new(path);}

    private async Task LoadAsync(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)||loading||exporting)return;
        StopPlaying();loading=true;SetBusy(true);Status("動画を読み込み中…");
        try
        {
            var nextMedia=GetMedia();var nextInfo=await nextMedia.ProbeAsync(path,lifetime.Token);
            var nextEvents=EventLog.Load(Path.ChangeExtension(path,".tsv"));
            var videoSettings=SettingsStore.LoadVideo(nextInfo,out var warning);
            mediaLifetime.Cancel();if(preview is not null)await preview.DisposeAsync();mediaLifetime.Dispose();mediaLifetime=new();
            current=nextInfo;media=nextMedia;events=nextEvents;preview=new PreviewReader(media,current);
            rawPreview?.Dispose();rawPreview=null;canvas.Frame=null;
            settings.LastVideoPath=Path.GetFullPath(path);
            syncing=true;canvas.VideoSize=new(current.Width,current.Height);outside.Checked=videoSettings.AllowOutside;canvas.AllowOutside=outside.Checked;
            canvas.Crop=videoSettings.Crop;rangeStart=videoSettings.Start;rangeEnd=videoSettings.End;currentTime=videoSettings.Position;syncing=false;
            history.Clear();historyIndex=-1;RememberCrop();SyncTimes();SyncCrop();canvas.ResetView();RequestFrame();
            Status(warning??current.TimingWarning??$"読み込み完了 / 入力ログ {events.Count} 件");TrySaveApp();
        }
        finally {loading=false;SetBusy(false);}
    }
    private void UpdateInfo()
    {if(current is not null)info.Text=$"{Path.GetFileName(current.Path)}  |  {current.Width}×{current.Height}  {current.Fps:0.###} fps  {current.Duration:0.###}秒  |  表示 {canvas.Zoom:0.##}倍";}
    private void Seek(double time)
    {if(current is null||loading||exporting)return;currentTime=Math.Clamp(time,0,current.LastFrameTime);ResetClock();SyncTimes();RequestFrame();}
    private void SetRange(double from,double to)
    {
        if(current is null)return;StopPlaying();
        from=Math.Clamp(from,0,current.LastFrameTime);to=Math.Clamp(to,Math.Min(current.Duration,from+1/current.Fps),current.Duration);
        bool changedStart=Math.Abs(from-rangeStart)>1e-9;
        bool changedEnd=Math.Abs(to-rangeEnd)>1e-9;
        rangeStart=from;rangeEnd=to;SyncTimes();
        if(changedStart||changedEnd)Seek(changedStart?from:to);
    }
    private void SyncTimes()
    {
        syncing=true;start.Value=(decimal)rangeStart;position.Value=(decimal)currentTime;end.Value=(decimal)rangeEnd;syncing=false;
        timeline.Duration=current?.Duration??0;timeline.Start=rangeStart;timeline.End=rangeEnd;timeline.Position=currentTime;timeline.Invalidate();UpdateInfo();
    }
    private void RequestFrame(){seekVersion++;if(!rendering)RunUiAsync(RenderLoopAsync);}
    private async Task RenderLoopAsync()
    {
        if(preview is null||current is null||closing)return;rendering=true;
        try
        {
            while(!closing)
            {
                var version=seekVersion;var reader=preview;var token=mediaLifetime.Token;var time=currentTime;
                Bitmap frame;
                try{frame=await reader.ReadAsync(time,token);}catch(OperationCanceledException){if(reader!=preview)continue;throw;}
                if(closing||reader!=preview||version!=seekVersion){frame.Dispose();if(closing)break;continue;}
                rawPreview?.Dispose();rawPreview=frame;previewTime=FrameTime(time,current);Redraw();
                if(version==seekVersion)break;
            }
        }
        catch{StopPlaying();throw;}
        finally{rendering=false;}
    }
    private void Redraw()
    {if(rawPreview is null||current is null||closing)return;canvas.Frame=OverlayRenderer.RenderPreview(rawPreview,current,previewTime,events,settings.Overlay,canvas.Crop);}
    private void TogglePlay()
    {
        if(current is null||exporting||loading)return;
        if(playing){StopPlaying();return;}
        if(speed.Value==0){speed.Value=1;}
        var low=settings.RangeOnly?rangeStart:0;var high=settings.RangeOnly?rangeEnd:current.Duration;
        if(currentTime<low||currentTime>=high-1/current.Fps)currentTime=settings.Speed<0?Math.Max(low,high-1/current.Fps):low;
        playing=true;play.Text="Ⅱ 停止";ResetClock();
    }
    private void StopPlaying(){playing=false;clock.Stop();play.Text="▶ 再生";}
    private void ResetClock(){playOrigin=currentTime;clock.Restart();}
    private void PlaybackTick()
    {
        if(!playing||current is null||loading||exporting||closing)return;
        var low=settings.RangeOnly?rangeStart:0;
        var high=settings.RangeOnly&&rangeEnd<current.Duration-1e-7?Math.Min(current.LastFrameTime,rangeEnd-1/current.Fps):current.LastFrameTime;high=Math.Max(low,high);
        var target=playOrigin+clock.Elapsed.TotalSeconds*settings.Speed;
        if(target>high||target<low)
        {
            if(!settings.Loop){currentTime=Math.Clamp(target,low,high);StopPlaying();}
            else if(settings.PingPong){currentTime=Math.Clamp(target,low,high);speed.Value=-speed.Value;ResetClock();}
            else{currentTime=settings.Speed<0?high:low;ResetClock();}
        }
        else currentTime=target;
        SyncTimes();if(!rendering||!playing)RequestFrame();
    }
    private void SetCrop(CropRect crop)
    {if(current is null)return;canvas.Crop=crop.Clamp(current.Width,current.Height,outside.Checked);SyncCrop();RememberCrop();Redraw();}
    private void SyncCrop()
    {syncing=true;var c=canvas.Crop;cropX.Value=Math.Clamp(c.X,(int)cropX.Minimum,(int)cropX.Maximum);cropY.Value=Math.Clamp(c.Y,(int)cropY.Minimum,(int)cropY.Maximum);cropW.Value=Math.Clamp(c.Width,1,16384);cropH.Value=Math.Clamp(c.Height,1,16384);syncing=false;}
    private void RememberCrop()
    {if(current is null)return;var crop=canvas.Crop;if(historyIndex>=0&&history[historyIndex]==crop)return;if(historyIndex+1<history.Count)history.RemoveRange(historyIndex+1,history.Count-historyIndex-1);history.Add(crop);historyIndex=history.Count-1;}
    private void Undo(){if(historyIndex>0){canvas.Crop=history[--historyIndex];SyncCrop();Redraw();}}
    private void Redo(){if(historyIndex+1<history.Count){canvas.Crop=history[++historyIndex];SyncCrop();Redraw();}}
    private void ApplyPreset(){if(!sizeLock.Checked&&presets.SelectedItem is ResolutionPreset preset)SetCrop(canvas.Crop with{Width=preset.Width,Height=preset.Height});}
    private void SavePreset()
    {
        if(current is null)return;
        using var dialog=new BrandedForm{Text=WindowTitle.Create("プリセット登録"),ClientSize=new Size(360,110),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var text=new TextBox{Text=$"{canvas.Crop.Width}×{canvas.Crop.Height}",Location=new Point(12,12),Width=332};var ok=new Button{Text="登録",DialogResult=DialogResult.OK,Location=new Point(180,60)};var no=new Button{Text="キャンセル",DialogResult=DialogResult.Cancel,Location=new Point(265,60)};dialog.Controls.AddRange([text,ok,no]);dialog.AcceptButton=ok;dialog.CancelButton=no;
        if(dialog.ShowDialog(this)!=DialogResult.OK||string.IsNullOrWhiteSpace(text.Text))return;
        settings.Presets.Add(new(text.Text.Trim(),canvas.Crop.Width,canvas.Crop.Height));RefreshPresets();TrySaveApp();
    }
    private void DeletePreset(){if(presets.SelectedItem is ResolutionPreset p){settings.Presets.Remove(p);RefreshPresets();TrySaveApp();}}
    private void RefreshPresets(){presets.Items.Clear();presets.Items.AddRange(settings.Presets.Cast<object>().ToArray());if(presets.Items.Count>0)presets.SelectedIndex=0;}
    private void SaveVideo()
    {
        if(current is null)return;
        try{SettingsStore.SaveVideo(current,new(){Crop=canvas.Crop,Start=rangeStart,End=rangeEnd,Position=currentTime,AllowOutside=outside.Checked});TrySaveApp();Status("動画設定を保存しました: "+SettingsStore.VideoPath(current.Path));}catch(Exception ex){ShowError(ex);}
    }
    private void TrySaveApp(){try{store.Save(settings);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Status("設定を保存できません: "+ex.Message);}}
    private void OpenSettings()
    {StopPlaying();using var form=new SettingsForm(settings);if(form.ShowDialog(this)==DialogResult.OK){settings=form.Settings;ApplySettings();TrySaveApp();Redraw();}}
    private void ApplySettings()
    {
        syncing=true;
        speed.Value=(decimal)settings.Speed;range.Checked=settings.RangeOnly;loop.Checked=settings.Loop;pingpong.Checked=settings.PingPong;
        pointer.Checked=settings.Overlay.ShowPointer||settings.Overlay.ShowClicks;keys.Checked=settings.Overlay.ShowKeys;burn.Checked=settings.BurnOverlay;stable.Checked=settings.StableOnly;duplicates.Checked=settings.SkipDuplicates;
        canvas.CropColor=settings.CropColor;canvas.CanvasColor=settings.CanvasColor;RefreshPresets();
        syncing=false;
    }
    private void OpenFolder()
    {if(current is not null)Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,Arguments=$"/select,\"{current.Path}\""});}
    private async Task<Bitmap> CurrentImageAsync()
    {
        if(current is null||media is null)throw new InvalidOperationException("先に動画を選択してください。");StopPlaying();
        var source=current;var crop=canvas.Crop;var log=events;var options=settings.Overlay with{};
        var time=FrameTime(currentTime,source);
        using var frame=await media.ReadFrameAsync(source,time,lifetime.Token);
        return OverlayRenderer.Render(frame,crop,time,log,options);
    }
    private static double FrameTime(double time,MediaInfo info)=>info.FrameTime(time);
    private async Task CopyAsync(){using var image=await CurrentImageAsync();Clipboard.SetImage(image);Status($"{image.Width}×{image.Height} の画像をコピーしました。");}
    private async Task SaveImageAsync()
    {
        if(current is null)return;StopPlaying();
        using var dialog=new SaveFileDialog{Filter="PNG画像|*.png",FileName=$"{Path.GetFileNameWithoutExtension(current.Path)}_{currentTime:0.000}.png",InitialDirectory=Path.GetDirectoryName(current.Path),OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(File.Exists(dialog.FileName)){MessageBox.Show(this,"既存ファイルを保護するため、新しい名前を指定してください。",WindowTitle.Create("保存先"));return;}
        using var image=await CurrentImageAsync();ExportService.SavePng(image,dialog.FileName,settings.PngCompression);Status("画像を保存しました: "+dialog.FileName);
    }
    private void BeginExport(ExportKind kind)
    {
        if(current is null||media is null||exporting||loading)return;StopPlaying();string outputPath;
        if(kind==ExportKind.PngSequence){using var dialog=new FolderBrowserDialog{Description="PNG連番を保存する親フォルダー",UseDescriptionForTitle=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;outputPath=Path.Combine(dialog.SelectedPath,$"{Path.GetFileNameWithoutExtension(current.Path)}_crops_{DateTime.Now:yyyyMMdd_HHmmss_fff}");}
        else{var ext=kind==ExportKind.Mp4?"mp4":"gif";using var dialog=new SaveFileDialog{Filter=$"{ext.ToUpperInvariant()}|*.{ext}",FileName=$"{Path.GetFileNameWithoutExtension(current.Path)}_clip_{DateTime.Now:HHmmss}.{ext}",OverwritePrompt=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;if(File.Exists(dialog.FileName)){MessageBox.Show(this,"既存ファイルを保護するため、新しい名前を指定してください。",WindowTitle.Create("保存先"));return;}outputPath=dialog.FileName;}
        var snapshot=System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        var request=new ExportRequest(current,canvas.Crop,rangeStart,rangeEnd,outputPath,kind,snapshot);
        exportCancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);exporting=true;SetBusy(true);cancel.Enabled=true;progress.Value=0;
        exportTask=ExportAsync(request,media,events,exportCancel.Token);
    }
    private async Task ExportAsync(ExportRequest request,FfmpegService service,EventLog log,CancellationToken token)
    {
        try
        {
            var report=new Progress<ExportProgress>(p=>{if(closing)return;progress.Value=p.Total>0?(int)Math.Clamp(p.Completed*100L/p.Total,0,100):0;Status(p.Message+ $" ({p.Completed}/{p.Total})");});
            var result=await Task.Run(()=>new ExportService(service).ExportAsync(request,log,report,token),token);
            progress.Value=100;Status($"保存完了: {result.FrameCount} フレーム / {result.OutputPath}");
        }
        catch(OperationCanceledException){Status("書出しを中断しました。");}
        catch(Exception ex){if(!closing)ShowError(ex);}
        finally{exporting=false;exportCancel?.Dispose();exportCancel=null;if(!closing){cancel.Enabled=false;SetBusy(false);}}
    }
    private void SetBusy(bool busy){foreach(var c in busyControls)c.Enabled=!busy;speed.Enabled=range.Enabled=loop.Enabled=pingpong.Enabled=!busy;}
    private void ShowHelp()=>MessageBox.Show(this,"動画を選択し、開始・終了とクロップを指定して保存します。\n\nSpace: 再生/停止　←/→: 0.1秒　Ctrl+←/→: 1秒\nHome/End: 区間端　Ctrl+Home/End: 動画端\nCtrl+C: 画像コピー　Ctrl+S: PNG保存　Ctrl+Z/Y: 戻す/進む\nAlt+矢印: 1px移動　Shift+矢印: 中心固定で伸縮（Ctrl併用10px）\n左ドラッグ: 選択/移動　枠ドラッグ: サイズ変更\nShift: 比率/移動軸を固定　Ctrl: 中心固定　Alt: 10px刻み\nホイール: ズーム　中ドラッグ: パン　中ダブルクリック: リセット\n\n動画と同名のTSVがあればマウス・キー操作を表示します。\n動画別設定は「設定を保存」で保存します。Python版.settings.jsonも読み込めます。\nMP4/GIFは音声なし。「動画に演出を焼込」は設定で変更できます。",WindowTitle.Create("使い方"));

    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(exporting||loading)return base.ProcessCmdKey(ref msg,keyData);
        var key=keyData&Keys.KeyCode;var ctrl=(keyData&Keys.Control)!=0;var alt=(keyData&Keys.Alt)!=0;var shift=(keyData&Keys.Shift)!=0;
        if(ctrl&&key==Keys.O){ChooseVideo();return true;}
        if(ActiveControl is TextBoxBase or NumericUpDown || ContainsFocusedNumeric(this))return base.ProcessCmdKey(ref msg,keyData);
        if(ctrl&&key==Keys.C){RunUiAsync(CopyAsync);return true;}if(ctrl&&key==Keys.S){RunUiAsync(SaveImageAsync);return true;}if(ctrl&&key==Keys.Z){Undo();return true;}if(ctrl&&key==Keys.Y){Redo();return true;}
        if(key==Keys.Space){TogglePlay();return true;}if(key==Keys.Home){Seek(ctrl?0:rangeStart);return true;}if(key==Keys.End){Seek(ctrl?(current?.LastFrameTime??0):rangeEnd);return true;}
        if(key is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            int delta=ctrl?10:1;int dx=key==Keys.Left?-delta:key==Keys.Right?delta:0;int dy=key==Keys.Up?-delta:key==Keys.Down?delta:0;
            if(alt){if(!moveLock.Checked)SetCrop(canvas.Crop with{X=canvas.Crop.X+dx,Y=canvas.Crop.Y+dy});return true;}
            if(shift){if(!sizeLock.Checked)SetCrop(canvas.Crop with{X=canvas.Crop.X-dx,Y=canvas.Crop.Y-dy,Width=canvas.Crop.Width+2*dx,Height=canvas.Crop.Height+2*dy});return true;}
            if(key is Keys.Left or Keys.Right){Seek(currentTime+(key==Keys.Left?-1:1)*(ctrl?1:0.1));return true;}
        }
        return base.ProcessCmdKey(ref msg,keyData);
    }
    private static bool ContainsFocusedNumeric(Control root)=>root.Controls.Cast<Control>().Any(c=>c is NumericUpDown&&c.ContainsFocus||ContainsFocusedNumeric(c));
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.XButton1)Seek(currentTime-1);if(e.Button==MouseButtons.XButton2)Seek(currentTime+1);}
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if(shutdownComplete){base.OnFormClosing(e);return;}
        e.Cancel=true;if(closing)return;closing=true;StopPlaying();timer.Stop();lifetime.Cancel();mediaLifetime.Cancel();exportCancel?.Cancel();
        try{if(exportTask is not null)await exportTask;if(preview is not null)await preview.DisposeAsync();}
        catch(Exception ex){Debug.WriteLine(ex);}
        finally
        {
            if(WindowState==FormWindowState.Normal){settings.WindowWidth=Width;settings.WindowHeight=Height;}TrySaveApp();
            rawPreview?.Dispose();rawPreview=null;timer.Dispose();tips.Dispose();shutdownComplete=true;Close();
        }
    }
}
