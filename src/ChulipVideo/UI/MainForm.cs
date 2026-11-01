using System.Diagnostics;
using ChulipVideo.Core;
using ChulipVideo.Media;
using ChulipVideo.Export;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.UI;

public sealed partial class MainForm : BrandedForm
{
    private readonly SettingsStore store;
    private AppSettings settings;
    private readonly VideoCanvas canvas = new() { Dock=DockStyle.Fill };
    private readonly Timeline timeline = new() { Dock=DockStyle.Fill, Margin=Padding.Empty };
    private readonly TextBox sourcePath = new() { Dock=DockStyle.Fill, ReadOnly=true, AccessibleName="選択中の動画ファイル", PlaceholderText="動画を選択、またはドロップ", Margin=new Padding(4,7,4,3) };
    private readonly Label info = new() { Dock=DockStyle.Fill, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft, Text="動画未選択", Margin=new Padding(3) };
    private readonly Label status = new() { Dock=DockStyle.Fill, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft, Text="準備完了" };
    private readonly TimecodeUpDown start=Timecode("開始時刻"), position=Timecode("現在時刻"), end=Timecode("終了時刻");
    private readonly NumericUpDown cropX=Number("X",-16384,32768), cropY=Number("Y",-16384,32768), cropW=Number("幅",1,16384), cropH=Number("高さ",1,16384);
    private readonly NumericUpDown speed=Number("再生速度",-9.9m,9.9m,1);
    private readonly CheckBox range=new(){Text="区間",AutoSize=true},loop=new(){Text="ループ",AutoSize=true},pingpong=new(){Text="往復",AutoSize=true};
    private readonly CheckBox outside=new(){Text="枠外許可",AutoSize=true},moveLock=new(){Text="座標固定",AutoSize=true},sizeLock=new(){Text="寸法固定",AutoSize=true};
    private readonly CheckBox pointer=new(){Text="マウスポインタ",AutoSize=true},keys=new(){Text="キー入力字幕",AutoSize=true},burn=new(){Text="動画へ焼込み",AutoSize=true};
    private readonly CheckBox stable=new(){Text="静止フレーム",AutoSize=true},duplicates=new(){Text="重複を除外",AutoSize=true};
    private readonly ComboBox presets=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,AccessibleName="解像度プリセット"};
    private readonly ComboBox presetRatio=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,AccessibleName="プリセット比率の絞り込み"};
    private readonly NumericUpDown pngCompression=Number("PNG圧縮",0,9);
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
    // プレビュー描画は終了処理で安全に取り消せるため、再生中でも停止を受け付ける。
    internal string? ManagementStopBlockReason => exporting || loading || closing ? "読込・書き出し・終了処理中のため停止できません。" : null;
    private double currentTime,rangeStart,rangeEnd,playOrigin,previewTime;
    private Task? exportTask;

    public MainForm(SettingsStore store,AppSettings initialSettings,string? video)
    {
        this.store=store; settings=initialSettings;
        Text=WindowTitle.Create(); MinimumSize=new Size(800,650); Size=new Size(settings.WindowWidth,settings.WindowHeight);
        StartPosition=FormStartPosition.CenterScreen; AllowDrop=true; KeyPreview=true;
        BuildLayout();
        cancel.Click+=(_,_)=>exportCancel?.Cancel();
        timeline.Seek+=Seek;timeline.StartChanged+=v=>SetRange(v,rangeEnd);timeline.EndChanged+=v=>SetRange(rangeStart,v);
        start.ValueChanged+=(_,_)=>{if(!syncing)SetRange((double)start.Value,rangeEnd);};end.ValueChanged+=(_,_)=>{if(!syncing)SetRange(rangeStart,(double)end.Value);};position.ValueChanged+=(_,_)=>{if(!syncing)Seek((double)position.Value);};
        foreach(var editor in new[]{start,position,end})editor.Enter+=(_,_)=>StopPlaying();
        foreach(var n in new[]{cropX,cropY,cropW,cropH})n.ValueChanged+=(_,_)=>{if(!syncing)SetCrop(new((int)cropX.Value,(int)cropY.Value,(int)cropW.Value,(int)cropH.Value));};
        canvas.CropChanged+=(_,_)=>{SyncCrop();Redraw();SyncEffects();};canvas.CropCommitted+=(_,_)=>{RememberCrop();SyncIdle();};canvas.ViewChanged+=(_,_)=>UpdateInfo();
        outside.CheckedChanged+=(_,_)=>{canvas.AllowOutside=outside.Checked;if(!syncing)SetCrop(canvas.Crop);};
        moveLock.CheckedChanged+=(_,_)=>{canvas.MoveLocked=moveLock.Checked;cropX.Enabled=cropY.Enabled=!moveLock.Checked;};
        sizeLock.CheckedChanged+=(_,_)=>{canvas.SizeLocked=sizeLock.Checked;cropW.Enabled=cropH.Enabled=presets.Enabled=!sizeLock.Checked;};
        canvas.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.XButton1)Seek(currentTime-1);if(e.Button==MouseButtons.XButton2)Seek(currentTime+1);};
        speed.ValueChanged+=(_,_)=>{if(syncing)return;settings.Speed=(double)speed.Value;ResetClock();};
        range.CheckedChanged+=(_,_)=>{if(syncing)return;settings.RangeOnly=range.Checked;StopPlaying();};loop.CheckedChanged+=(_,_)=>{if(!syncing)settings.Loop=loop.Checked;};pingpong.CheckedChanged+=(_,_)=>{if(!syncing)settings.PingPong=pingpong.Checked;};
        pointer.CheckedChanged+=(_,_)=>{if(syncing)return;settings.Overlay.ShowPointer=pointer.Checked;settings.Overlay.ShowClicks=pointer.Checked;Redraw();};keys.CheckedChanged+=(_,_)=>{if(syncing)return;settings.Overlay.ShowKeys=keys.Checked;Redraw();};burn.CheckedChanged+=(_,_)=>{if(!syncing)settings.BurnOverlay=burn.Checked;};
        stable.CheckedChanged+=(_,_)=>{if(!syncing)settings.StableOnly=stable.Checked;};duplicates.CheckedChanged+=(_,_)=>{if(!syncing)settings.SkipDuplicates=duplicates.Checked;};
        pngCompression.ValueChanged+=(_,_)=>{if(!syncing)settings.PngCompression=(int)pngCompression.Value;};
        presetRatio.SelectedIndexChanged+=(_,_)=>RefreshPresets();
        presets.SelectionChangeCommitted+=(_,_)=>ApplyPreset();
        DragEnter+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        DragDrop+=(_,e)=>{if(e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length>0&&!exporting&&!loading)RunUiAsync(()=>LoadAsync(paths[0]));};
        timer.Tick+=(_,_)=>PlaybackTick(); timer.Start();
        InitializeMasks();InitializeEffects();ApplySettings();
        Shown+=(_,_)=>{if(store.Warning is not null)Status(store.Warning);var path=video??settings.LastVideoPath;if(File.Exists(path))RunUiAsync(()=>LoadAsync(path));};
    }

    private static NumericUpDown Number(string label,decimal min,decimal max,int decimals=0) => new(){Minimum=min,Maximum=max,DecimalPlaces=decimals,Increment=decimals>0?0.1m:1,Width=decimals>0?90:76,AccessibleName=label,ThousandsSeparator=false};
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
            sourcePath.Text=settings.LastVideoPath;tips.SetToolTip(sourcePath,settings.LastVideoPath);
            syncing=true;canvas.VideoSize=new(current.Width,current.Height);outside.Checked=videoSettings.AllowOutside;canvas.AllowOutside=outside.Checked;
            masks=videoSettings.Masks;clips=videoSettings.Clips;markers=videoSettings.Markers;canvas.SelectedMask=-1;canvas.Crop=videoSettings.Crop;rangeStart=videoSettings.Start;rangeEnd=videoSettings.End;currentTime=videoSettings.Position;syncing=false;
            history.Clear();historyIndex=-1;RememberCrop();SyncTimes();SyncCrop();canvas.ResetView();RequestFrame();SyncIdle();SyncClips();
            Status(warning??current.TimingWarning??$"読み込み完了 / 入力ログ {events.Count} 件");TrySaveApp();
        }
        finally {loading=false;SetBusy(false);}
    }
    private void UpdateInfo()
    {if(current is not null){info.Text=$"{current.Duration:0.###}秒  {current.Width}×{current.Height}\n{canvas.Zoom*100:0}%  /  {current.Fps:0.###} FPS";tips.SetToolTip(info,$"{current.Width}×{current.Height}  {current.Fps:0.###} fps  {current.Duration:0.###}秒\n表示: 全体表示を100%とした倍率 {canvas.Zoom*100:0}%");}}
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
        bool wasSyncing=syncing;syncing=true;
        try{start.Value=(decimal)rangeStart;position.Value=(decimal)currentTime;end.Value=(decimal)rangeEnd;}
        finally{syncing=wasSyncing;}
        timeline.Duration=current?.Duration??0;timeline.Start=rangeStart;timeline.End=rangeEnd;timeline.Position=currentTime;timeline.Invalidate();UpdateInfo();SyncMasks();SyncEffects();
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
    {if(rawPreview is null||current is null||closing)return;canvas.Frame=FrameComposer.ComposePreview(rawPreview,current,OutputContext(canvas.Crop,previewTime));}
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
    {
        bool wasSyncing=syncing;syncing=true;
        try{var c=canvas.Crop;cropX.Value=Math.Clamp(c.X,(int)cropX.Minimum,(int)cropX.Maximum);cropY.Value=Math.Clamp(c.Y,(int)cropY.Minimum,(int)cropY.Maximum);cropW.Value=Math.Clamp(c.Width,1,16384);cropH.Value=Math.Clamp(c.Height,1,16384);SelectCurrentPreset();}
        finally{syncing=wasSyncing;}
    }
    private void RememberCrop()
    {if(current is null)return;var crop=canvas.Crop;if(historyIndex>=0&&history[historyIndex]==crop)return;if(historyIndex+1<history.Count)history.RemoveRange(historyIndex+1,history.Count-historyIndex-1);history.Add(crop);historyIndex=history.Count-1;}
    private void Undo(){if(historyIndex>0){canvas.Crop=history[--historyIndex];SyncCrop();Redraw();}}
    private void Redo(){if(historyIndex+1<history.Count){canvas.Crop=history[++historyIndex];SyncCrop();Redraw();}}
    private void ApplyPreset()
    {
        if(current is null||sizeLock.Checked||presets.SelectedItem is not ResolutionPreset preset)return;
        int selected=presets.SelectedIndex;
        SetCrop(canvas.Crop with{Width=preset.Width,Height=preset.Height});
        // 動画内へ制限されても、削除対象が別のプリセットへ入れ替わらないよう選択を保持する。
        presets.SelectedIndex=selected;
        if(canvas.Crop.Width!=preset.Width||canvas.Crop.Height!=preset.Height)
            Status($"プリセット「{preset.Name}」を適用: {canvas.Crop.Width}×{canvas.Crop.Height} px（動画内に収めました）");
    }
    private void SavePreset()
    {
        if(current is null)return;
        using var dialog=new BrandedForm{Text=WindowTitle.Create("プリセット登録"),ClientSize=new Size(360,110),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var text=new TextBox{Text=$"{canvas.Crop.Width}×{canvas.Crop.Height}",Location=new Point(12,12),Width=332};var ok=new Button{Text="登録",DialogResult=DialogResult.OK,Location=new Point(180,60)};var no=new Button{Text="キャンセル",DialogResult=DialogResult.Cancel,Location=new Point(265,60)};dialog.Controls.AddRange([text,ok,no]);dialog.AcceptButton=ok;dialog.CancelButton=no;
        if(dialog.ShowDialog(this)!=DialogResult.OK||string.IsNullOrWhiteSpace(text.Text))return;
        settings.Presets.Add(new(text.Text.Trim(),canvas.Crop.Width,canvas.Crop.Height));RefreshPresets();TrySaveApp();
    }
    private void DeletePreset(){if(presets.SelectedItem is ResolutionPreset p){settings.Presets.Remove(p);RefreshPresets();TrySaveApp();}}
    private void RefreshPresets()
    {
        presets.BeginUpdate();
        try
        {
            presets.Items.Clear();presets.Items.Add("カスタム");
            double? ratio=presetRatio.SelectedIndex switch{1=>16d/9,2=>4d/3,3=>1d,_=>null};
            var visible=settings.Presets.Where(p=>ratio is null||Math.Abs(p.Width/(double)p.Height-ratio.Value)<0.01);
            presets.Items.AddRange(visible.Cast<object>().ToArray());SelectCurrentPreset();
        }
        finally{presets.EndUpdate();}
    }
    private void SelectCurrentPreset()
    {
        if(presets.Items.Count==0)return;
        var crop=canvas.Crop;int selected=0;
        if(presets.SelectedItem is ResolutionPreset currentPreset&&currentPreset.Width==crop.Width&&currentPreset.Height==crop.Height)return;
        for(int i=1;i<presets.Items.Count;i++)if(presets.Items[i] is ResolutionPreset p&&p.Width==crop.Width&&p.Height==crop.Height){selected=i;break;}
        presets.SelectedIndex=selected;
    }
    private void SaveVideo()
    {
        if(current is null)return;
        try{SettingsStore.SaveVideo(current,new(){Crop=canvas.Crop,Start=rangeStart,End=rangeEnd,Position=currentTime,AllowOutside=outside.Checked,Masks=MaskSnapshot().ToList(),Clips=clips.Select(c=>c with{}).ToList(),Markers=markers.Select(m=>m with{}).ToList()});TrySaveApp();Status("動画設定を保存しました: "+SettingsStore.VideoPath(current.Path));}catch(Exception ex){ShowError(ex);}
    }
    private void TrySaveApp(){try{store.Save(settings);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Status("設定を保存できません: "+ex.Message);}}
    private void OpenSettings()
    {StopPlaying();using var form=new SettingsForm(settings);if(form.ShowDialog(this)==DialogResult.OK){settings=form.Settings;ApplySettings();TrySaveApp();Redraw();SyncEffects();SyncIdle();}}
    private void ApplySettings()
    {
        bool wasSyncing=syncing;syncing=true;
        try
        {
            speed.Value=(decimal)settings.Speed;range.Checked=settings.RangeOnly;loop.Checked=settings.Loop;pingpong.Checked=settings.PingPong;
            pointer.Checked=settings.Overlay.ShowPointer||settings.Overlay.ShowClicks;keys.Checked=settings.Overlay.ShowKeys;burn.Checked=settings.BurnOverlay;stable.Checked=settings.StableOnly;duplicates.Checked=settings.SkipDuplicates;
            pngCompression.Value=settings.PngCompression;
            canvas.CropColor=settings.CropColor;canvas.CanvasColor=settings.CanvasColor;RefreshPresets();
        }
        finally{syncing=wasSyncing;}
    }
    private void OpenFolder()
    {if(current is not null)Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,Arguments=$"/select,\"{current.Path}\""});}
    private void OpenInputLog()
    {
        if(current is null)return;
        string path=Path.ChangeExtension(current.Path,".tsv");
        if(!File.Exists(path)){Status("対応する入力履歴TSVはありません: "+path);return;}
        try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){ShowError(ex);}
    }
    private void OpenStepEditor()
    {
        if(current is null||media is null){Status("先に動画を選択してください。");return;}
        StopPlaying();
        using var form=new StepEditorForm(media,current,events,settings,rangeStart,rangeEnd,canvas.Crop,currentTime,MaskSnapshot());
        form.ShowDialog(this);
    }
    private async Task<Bitmap> CurrentImageAsync()
    {
        if(current is null||media is null)throw new InvalidOperationException("先に動画を選択してください。");StopPlaying();
        var source=current;var time=FrameTime(currentTime,source);
        // 読込を待つ間の編集が混ざらないよう、合成の指定は先に確定する。
        var context=OutputContext(canvas.Crop,time) with{Options=settings.Overlay with{}};
        using var frame=await media.ReadFrameAsync(source,time,lifetime.Token);
        return FrameComposer.Compose(frame,context);
    }
    private static double FrameTime(double time,MediaInfo info)=>info.FrameTime(time);
    private async Task CopyAsync(){using var image=await CurrentImageAsync();Clipboard.SetImage(image);Status($"{image.Width}×{image.Height} の画像をコピーしました。");}
    private async Task SaveImageAsync()
    {
        if(current is null)return;StopPlaying();
        using var dialog=SaveDialog("PNG画像|*.png",$"{Path.GetFileNameWithoutExtension(current.Path)}_{currentTime:0.000}.png",Path.GetDirectoryName(current.Path));
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(File.Exists(dialog.FileName)){MessageBox.Show(this,"既存ファイルを保護するため、新しい名前を指定してください。",WindowTitle.Create("保存先"));return;}
        using var image=await CurrentImageAsync();ExportService.SavePng(image,dialog.FileName,settings.PngCompression);Status("画像を保存しました: "+dialog.FileName);
    }
    // 既存ファイルは常に保護するため、上書き確認は出さずに選択後に別名を求める。
    private static SaveFileDialog SaveDialog(string filter,string fileName,string? directory)=>new(){Filter=filter,FileName=fileName,InitialDirectory=directory??"",OverwritePrompt=false};
    private void BeginExport(ExportKind kind)
    {
        if(current is null||media is null||exporting||loading)return;StopPlaying();string outputPath;
        if(kind==ExportKind.PngSequence){using var dialog=new FolderBrowserDialog{Description="PNG連番を保存する親フォルダー",UseDescriptionForTitle=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;outputPath=Path.Combine(dialog.SelectedPath,$"{Path.GetFileNameWithoutExtension(current.Path)}_crops_{DateTime.Now:yyyyMMdd_HHmmss_fff}");}
        else{var ext=kind==ExportKind.Mp4?"mp4":"gif";using var dialog=SaveDialog($"{ext.ToUpperInvariant()}|*.{ext}",$"{Path.GetFileNameWithoutExtension(current.Path)}_clip_{DateTime.Now:HHmmss}.{ext}",null);if(dialog.ShowDialog(this)!=DialogResult.OK)return;if(File.Exists(dialog.FileName)){MessageBox.Show(this,"既存ファイルを保護するため、新しい名前を指定してください。",WindowTitle.Create("保存先"));return;}outputPath=dialog.FileName;}
        var snapshot=System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        var request=new ExportRequest(current,canvas.Crop,rangeStart,rangeEnd,outputPath,kind,snapshot){Masks=MaskSnapshot()};
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
    private void ShowHelp()=>MessageBox.Show(this,"動画を選択し、開始・終了とクロップを指定して保存します。\n\nSpace: 再生/停止　←/→: 0.1秒　Ctrl+←/→: 1秒\nHome/End: 区間端　Ctrl+Home/End: 動画端\nI/O: 現在位置を開始/終了に設定（文字・数値入力中を除く）\nM: マーカー追加　, / .: 前後のマーカーへ\nCtrl+C: 画像コピー　Ctrl+S: PNG保存　Ctrl+Z/Y: 戻す/進む\nAlt+矢印: 1px移動　Shift+矢印: 中心固定で伸縮（Ctrl併用10px）\n左ドラッグ: 選択/移動　枠ドラッグ: サイズ変更\nShift: 比率/移動軸を固定　Ctrl: 中心固定　Alt: 10px刻み\nホイール: ズーム　中ドラッグ: パン　中ダブルクリック: リセット\n\n時刻は秒数、mm:ss、hh:mm:ss.fffで入力できます。\nクロップの「比率」はプリセットの絞り込み用です。枠の比率は固定しません。\n動画と同名のTSVがあればマウス・キー操作を表示します。\n動画別設定は上部の「保存」で保存します。Python版.settings.jsonも読み込めます。\nMP4/GIFは音声なし。「動画へ焼込み」は出力欄で変更できます。",WindowTitle.Create("使い方"));

    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(exporting||loading)return base.ProcessCmdKey(ref msg,keyData);
        var key=keyData&Keys.KeyCode;var ctrl=(keyData&Keys.Control)!=0;var alt=(keyData&Keys.Alt)!=0;var shift=(keyData&Keys.Shift)!=0;
        if(ctrl&&key==Keys.O){ChooseVideo();return true;}
        if(ActiveControl is TextBoxBase or NumericUpDown or ComboBox || ContainsFocusedNumeric(this))return base.ProcessCmdKey(ref msg,keyData);
        if(maskEdit.Checked&&key==Keys.Delete&&!ctrl&&!alt&&!shift){DeleteSelectedMask();return true;}
        if(!ctrl&&!alt&&!shift&&key==Keys.M){AddMarker();return true;}if(!ctrl&&!alt&&!shift&&key==Keys.Oemcomma){JumpMarker(-1);return true;}if(!ctrl&&!alt&&!shift&&key==Keys.OemPeriod){JumpMarker(1);return true;}
        if(!ctrl&&!alt&&!shift&&key==Keys.I){SetRange(currentTime,rangeEnd);return true;}
        if(!ctrl&&!alt&&!shift&&key==Keys.O){SetRange(rangeStart,currentTime);return true;}
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
        // Windowsの終了時は取り消さない。取消要求でFFmpegを止め、設定だけ同期保存して閉じる。
        if(e.CloseReason==CloseReason.WindowsShutDown){BeginClosing();CompleteClosing();base.OnFormClosing(e);return;}
        e.Cancel=true;if(closing)return;BeginClosing();
        try{if(exportTask is not null)await exportTask;if(preview is not null)await preview.DisposeAsync();}
        catch(Exception ex){Debug.WriteLine(ex);}
        finally{if(!shutdownComplete){CompleteClosing();Close();}}
    }
    private void BeginClosing(){closing=true;StopPlaying();timer.Stop();lifetime.Cancel();mediaLifetime.Cancel();exportCancel?.Cancel();}
    private void CompleteClosing()
    {
        if(WindowState==FormWindowState.Normal){settings.WindowWidth=Width;settings.WindowHeight=Height;}TrySaveApp();
        rawPreview?.Dispose();rawPreview=null;timer.Dispose();tips.Dispose();shutdownComplete=true;
    }
}
