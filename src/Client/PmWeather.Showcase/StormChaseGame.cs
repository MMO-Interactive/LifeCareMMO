using System.Diagnostics;
using System.Runtime.Versioning;
using Engine.GUI;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Shared.Contracts.Rendering;

internal static partial class Showcase
{
    internal static string? OptionalGameValue(string[] args,string name)=>OptionalValue(args,name);

    [SupportedOSPlatform("windows")]
    private static int RunStormChaseGame(RendererOptions options,NativeSurface surface,MutableRenderAssetCatalog catalog,
        CaptureLogger logger,RenderGpuDebugValidation validation,bool particles,string qualityName,string output,string[] args)
    {
        using var localServer=new LocalChaseServer();using var frontend=new StormChaseFrontend(localServer);
        using var hud=new StormChaseHud(surface.WindowHandle);var pauseMenu=new ChasePauseMenu();
        using var cancellation=new CancellationTokenSource();var photoGate=new object();
        string lastQualityPreference=frontend.Preferences.WeatherQuality;
        if(OptionalValue(args,"--weather-quality") is null&&OptionalValue(args,"--weather-scale") is null)
        {var configured=WeatherRenderSettings.Read(["--weather-quality",lastQualityPreference]);options=GameQualityOptions(options,configured);qualityName=configured.Name;}
        StormChasePlaySession? play=null;Task<StormChasePlaySession>? loading=null;
        var host=new DedicatedRenderBackendHost("Storm Chase game DX12",maximumQueuedFrames:1);
        StormChaseFramePacing.EnableHighResolutionTimer();
        using var profiler=StormChaseLiveProfiler.FromEnvironment();
        surface.EnableFlyCameraControls();surface.ChaseControlsEnabled=true;surface.MenuControlsEnabled=true;
        surface.GameControlsEnabled=false;surface.GuiInputEnabled=true;
        var audioTask=StormChaseAudio.CreateAsync(logger,frontend.Preferences.Muted);
        StormChaseAudio? audio=null;
        try
        {
            var renderSurface=new DemoRenderSurface(surface.WindowHandle,surface.Width,surface.Height);
            Engine.Rendering.Backend.IRenderBackend CreateBackend()
            {
                var backend=new Dx12RenderBackend(options,renderSurface,catalog,logger);
                backend.FrameCpuTimingSink=profiler?.Prefixed("Render.");
                return new DemoRenderBackend(backend,renderSurface,
                    ()=>validation.CollectErrors(backend,"game initialization"),
                    ()=>validation.CollectErrors(backend,"game shutdown",waitForCompletion:validation.Enabled),
                    backend=>{lock(photoGate)play?.Photos.BeforeFrame(backend);},
                    (backend,frame)=>{lock(photoGate)play?.Photos.AfterFrame(backend,frame);});
            }
            DemoThreadPump.Wait(Task.Run(()=>host.Reconfigure(CreateBackend)),surface);
            var clock=Stopwatch.StartNew();double previous=0,nextStatus=1,lastStatus=0;long number=0,completed=0;
            double fps=0;bool paused=false,firstPresented=false;bool? fullscreenPreference=null;
            Console.WriteLine("Native Storm Chase: account login → main menu → single player / host / authenticated multiplayer.");
            while(!surface.IsCloseRequested)
            {
                surface.GameControlsEnabled=play is not null;
                surface.PumpMessages();if(surface.IsCloseRequested)break;host.ThrowIfFaulted();
                double seconds=clock.Elapsed.TotalSeconds;float delta=(float)Math.Clamp(seconds-previous,0,.25);previous=seconds;
                profiler?.BeginUiFrame();
                if(audio is null&&audioTask.IsCompleted)audio=audioTask.GetAwaiter().GetResult();
                var keyboard=new List<GuiKeyboardEvent>();while(surface.TryGetGuiKeyboardEvent(out var key))keyboard.Add(key);
                while(surface.TryGetStormKey(out int key))
                {
                    if(play is null)continue;
                    if(key==0x1B)
                    {
                        if(play.Lots?.Cancel()==true){}
                        else if(hud.MapOpen)hud.MapOpen=false;
                        else if(hud.PhoneOpen)hud.PhoneOpen=false;
                        else if(hud.ForecastOpen)hud.ForecastOpen=false;
                        else if(play.ShopOpen||play.DispatchOpen||play.HousingOpen){play.ShopOpen=false;play.DispatchOpen=false;play.HousingOpen=false;}
                        else paused=!paused;
                    }
                    else if(HudKey(key)){}
                    else if(key==0x5A){hud.ForecastOpen=!hud.ForecastOpen;surface.ReleaseMouseLook();}
                    else if(key==0x51)hud.HandleWeatherGuiAction("radar/toggle");
                    else if(key==0x4D)audio?.ToggleMuted();
                    else if(!paused)play.HandleKey(key);
                }
                if(surface.IsMinimized||surface.Width<=0||surface.Height<=0){play?.SuspendInput();surface.WaitForMessages(16);continue;}
                RenderFrame frame;
                if(play is null)
                {
                    surface.GuiInputEnabled=true;
                    var ui=frontend.Update(surface.Width,surface.Height,surface.ReadGuiInput(),keyboard,seconds);
                    frame=CreateFrontendBackground(number++,surface.Width,surface.Height,(float)seconds) with{Ui=ui};
                    if(frontend.ExitRequested){surface.RequestClose();continue;}
                    if(frontend.Preferences.Fullscreen!=fullscreenPreference)
                    {
                        fullscreenPreference=frontend.Preferences.Fullscreen;
                        if(surface.IsFullscreen!=fullscreenPreference.Value)surface.ToggleFullscreen();
                    }
                    audio?.SetMuted(frontend.Preferences.Muted);
                    if(frontend.Preferences.WeatherQuality!=lastQualityPreference)
                    {
                        lastQualityPreference=frontend.Preferences.WeatherQuality;
                        var configured=WeatherRenderSettings.Read(["--weather-quality",lastQualityPreference]);
                        options=GameQualityOptions(options,configured);qualityName=configured.Name;
                        DemoThreadPump.Wait(Task.Run(()=>host.Reconfigure(CreateBackend)),surface);
                        Console.WriteLine("Native game weather quality changed to "+qualityName+".");
                    }
                    if(loading is null&&frontend.Selection is { } selection)
                    {
                        string name=selection.Character?.Name??frontend.Preferences.Username;
                        frontend.SetJoiningStatus(selection.Offline?"Loading your single-player chase…":"Loading world and avatars before requesting native admission…");
                        loading=StormChasePlaySession.LoadAsync(catalog,logger,selection,name,output,options.FrameExportPath!,args,cancellation.Token);
                    }
                    if(loading is { IsCompleted:true })
                    {
                        try
                        {
                            var admitted=loading.GetAwaiter().GetResult();lock(photoGate)play=admitted;
                            paused=false;hud.RegionalMap=false;hud.ForecastOpen=false;hud.MapOpen=false;hud.PhoneOpen=false;
                            hud.AttachWorld(play.Session.World);hud.MapLots=play.Lots is { } mapLots?mapLots.MapLots:null;
                            audio?.ConfigureWarningSites(play.Session.World.Settlements.Select(site=>site.Position with{Y=site.Position.Y+15}));
                            surface.GuiInputEnabled=false;
                            Console.WriteLine(play.Online?"Native multiplayer admitted: complete reliable debris baseline and server environment.":"Authenticated single-player session loaded.");
                        }
                        catch(Exception error){frontend.JoinFailed(error.Message);Console.WriteLine("Game join failed: "+error.Message);}
                        loading=null;
                    }
                }
                else
                {
                    foreach(var key in keyboard)
                    {
                        if(key.Key==0x1B)
                        {if(play.Lots?.Cancel()==true){}else if(hud.MapOpen)hud.MapOpen=false;else if(hud.PhoneOpen)hud.PhoneOpen=false;else if(hud.ForecastOpen)hud.ForecastOpen=false;else if(play.ShopOpen||play.DispatchOpen||play.HousingOpen){play.ShopOpen=false;play.DispatchOpen=false;play.HousingOpen=false;}else paused=!paused;}
                        else if(HudKey(key.Key)){}
                        else if(key.Key==0x5A){hud.ForecastOpen=!hud.ForecastOpen;surface.ReleaseMouseLook();}
                        else if(key.Key==0x51)hud.HandleWeatherGuiAction("radar/toggle");
                        else if(!paused&&key.Key is 0x26 or 0x28 or 0x0D or 0x55 or 0x4F or 0x48 or 0x59)play.HandleKey(key.Key);
                    }
                    surface.GuiInputEnabled=paused||play.ShopOpen||play.DispatchOpen||play.HousingOpen||hud.ForecastOpen||hud.MapOpen||hud.PhoneOpen||play.Lots?.Pie is not null;
                    hud.WorldOverlay=play.Lots is { } lotLayer?lotLayer.Overlay:null;
                    hud.Hotbar=play.Lots?.Hotbar();hud.LotStatus=play.Lots?.StatusLine;hud.LotMessage=play.Lots?.Status;hud.PlayerName=play.PlayerName;
                    if(profiler is not null){profiler.Mark("InputAndKeys");play.Session.FrameTimingSink??=profiler.Prefixed("Game.");}
                    try{frame=play.Step(surface,number++,delta,paused,particles,hud.ForecastOpen);}
                    catch(Exception error)
                    {Leave(error.Message);continue;}
                    profiler?.Mark("PlayStep");
                    var input=surface.ReadGuiInput();hud.Housing=play.Session.HousingState;hud.HousingOpen=play.HousingOpen;hud.HousingCashCents=play.HousingCashCents;
                    hud.Update(surface,play.Telemetry(fps,host.LastGpuTiming?.TotalMilliseconds??0,audio?.Muted??true),frame,input);
                    profiler?.Mark("Hud");
                    if(paused)
                    {
                        var ui=pauseMenu.Update(surface.Width,surface.Height,input,keyboard,play.PlayerName,play.Online);
                        frame=frame with{Ui=ui};
                        switch(pauseMenu.Action)
                        {
                            case "resume":paused=false;break;
                            case "dispatch":paused=false;play.DispatchOpen=true;break;
                            case "home":paused=false;play.HousingOpen=true;break;
                            case "leave":Leave("Returned to the main menu.");continue;
                            case "quit":surface.RequestClose();continue;
                        }
                    }
                    else
                    {
                        frame=frame with{Ui=hud.Ui};
                        if(hud.LastFrame.Interaction.ActivatedElementId is { } action)
                        {if(hud.HandleWeatherGuiAction(action)){}
                        else if(hud.HandleHudAction(action,out var request))
                        {
                            switch(request)
                            {
                                case "jobs":play.DispatchOpen=true;play.ShopOpen=false;play.HousingOpen=false;break;
                                case "home":play.HousingOpen=true;play.ShopOpen=false;play.DispatchOpen=false;break;
                                case "shop":if(!play.ShopOpen)play.HandleKey(0x55);break;
                                case "build":play.HandleKey(0x59);break;
                                case "menu":paused=true;break;
                                case "forecast":hud.ForecastOpen=true;hud.MapOpen=false;break;
                            }
                        }
                        else if(action.StartsWith("lot/",StringComparison.Ordinal))play.Lots?.HandleGuiAction(action);
                        else if(action.StartsWith("housing/",StringComparison.Ordinal))HousingAction(action);else play.HandleGuiAction(action);}
                    }
                    audio?.Update(frame,(float)seconds,null,play.Session.Effects.LastStrike);
                }
                profiler?.Mark("AudioAndMenus");
                host.SubmitLatest(frame,catalog.AcquireFrameAssetLease(frame));
                profiler?.Mark("Submit");
                var progress=host.GetProgressSnapshot();
                if(!firstPresented&&progress.CompletedFrameCount>0)
                {firstPresented=true;Console.WriteLine("Storm Chase game first native frame presented.");}
                if(seconds>=nextStatus)
                {
                    fps=(progress.CompletedFrameCount-completed)/Math.Max(.001,seconds-lastStatus);
                    completed=progress.CompletedFrameCount;lastStatus=seconds;nextStatus=seconds+1;
                    string mode=play is null?frontend.Screen.ToString():play.Online?"MULTIPLAYER":"SINGLE PLAYER";
                    surface.SetTitle($"Storm Chase · {mode} · {qualityName} · {fps:0.0} FPS · GPU {host.LastGpuTiming?.TotalMilliseconds??0:0.0} ms");
                }
                StormChaseFramePacing.WaitForRenderer(host,surface);
                if(profiler is not null)
                {
                    profiler.Mark("WaitForRenderer");
                    profiler.EndUiFrame((clock.Elapsed.TotalSeconds-seconds)*1000,host.GetProgressSnapshot().CompletedFrameCount,
                        host.LastGpuTiming?.TotalMilliseconds??0,play is null?frontend.Screen.ToString():$"{(play.Online?"online":"single")}, items {frame.Items.Count}");
                    if(profiler.ShouldExit)surface.RequestClose();
                }
            }
            return 0;

            bool HudKey(int key)
            {
                switch(key)
                {
                    case 0x4E:hud.MapOpen=!hud.MapOpen;hud.PhoneOpen=false;hud.MapCenter=null;surface.ReleaseMouseLook();return true;
                    case 0x50:hud.PhoneOpen=!hud.PhoneOpen;hud.PhoneApp=null;hud.MapOpen=false;surface.ReleaseMouseLook();return true;
                    case 0xDB:hud.HandleHudAction("map/mini-in",out _);return true;
                    case 0xDD:hud.HandleHudAction("map/mini-out",out _);return true;
                    default:return false;
                }
            }
            void Leave(string reason)
            {
                lock(photoGate){var leaving=play;play=null;leaving?.Dispose();}
                paused=false;hud.ForecastOpen=false;surface.GuiInputEnabled=true;frontend.ReturnToMenu(reason);
            }
            void HousingAction(string action)
            {
                if(play is null)return;
                var pieces=action.Split('/');if(pieces.Length<2)return;
                int.TryParse(pieces.ElementAtOrDefault(2),out int target);
                switch(pieces[1])
                {
                    case "close":play.HousingOpen=false;break;
                    case "page":hud.HousingPage=Math.Clamp(target,0,2);break;
                    case "inventory":hud.HousingInventoryPage=Math.Clamp(hud.HousingInventoryPage+(pieces[2]=="next"?1:-1),0,3);break;
                    case "select":hud.HousingSelectedFurniture=target;break;
                    case "slot":hud.HousingSlot=(hud.HousingSlot+(pieces[2]=="next"?1:11))%12;break;
                    case "rotate":hud.HousingPlacementYaw=MathF.IEEERemainder(hud.HousingPlacementYaw+MathF.PI*.5f,MathF.Tau);break;
                    case "buy-home":play.HousingAction(StormChase.Protocol.GameplayActionKind.BuyHouse,target);break;
                    case "buy-furniture":play.HousingAction(StormChase.Protocol.GameplayActionKind.BuyFurniture,target);break;
                    case "place":play.HousingAction(StormChase.Protocol.GameplayActionKind.PlaceFurniture,hud.HousingSelectedFurniture,hud.HousingSlot,hud.HousingPlacementYaw);break;
                    case "locate" or "visit":play.HousingAction(StormChase.Protocol.GameplayActionKind.VisitHome,target);break;
                    case "visits":play.HousingAction(StormChase.Protocol.GameplayActionKind.SetHomeVisits,target);break;
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            // Native render resources are disposed while the original HWND is still alive.
            DemoThreadPump.Wait(Task.Run(host.Dispose),surface);
            if(loading is not null)
            {try{DemoThreadPump.Wait(loading,surface);loading.GetAwaiter().GetResult().Dispose();}catch(Exception){}}
            lock(photoGate){play?.Dispose();play=null;}
            // The authority must flush its final career/furniture poses while the
            // owned accounts service is still alive. Frontend owns that service.
            DemoThreadPump.Wait(localServer.StopAsync(),surface);
            if(audio is null){DemoThreadPump.Wait(audioTask,surface);audio=audioTask.GetAwaiter().GetResult();}
            if(audio is not null)DemoThreadPump.Wait(audio.DisposeAsync().AsTask(),surface);
        }
    }
    private static RendererOptions GameQualityOptions(RendererOptions source,WeatherRenderSettings quality)=>new()
    {
        DefaultViewport=source.DefaultViewport,EnableDebugOverlays=source.EnableDebugOverlays,AllowSoftwareAdapter=source.AllowSoftwareAdapter,
        EnableTearingPresentation=source.EnableTearingPresentation,EnableGpuTimings=source.EnableGpuTimings,EnableHdrPipeline=source.EnableHdrPipeline,
        FrameExportPath=source.FrameExportPath,ShadowMapResolution=source.ShadowMapResolution,ShadowCascadeCount=source.ShadowCascadeCount,
        ShadowDistance=source.ShadowDistance,RenderScale=source.RenderScale,PostProcessQuality=source.PostProcessQuality,
        SynchronizeFrameResources=source.SynchronizeFrameResources,WeatherRenderScale=quality.Scale,WeatherQuality=quality.Quality
    };
}

/// <summary>Pause navigation uses the same native DX12 GUI mesh and keyboard/pointer input as account pages.</summary>
internal sealed class ChasePauseMenu
{
    private readonly GuiDx12Composer composer=new();private readonly GuiLayoutEngine layout;
    private GuiFrame previous=GuiFrame.Empty;private int selected;private long revision;
    public string? Action{get;private set;}
    private static readonly string[] ids=["resume","dispatch","home","leave","quit"];
    public ChasePauseMenu(){layout=new(composer);}
    public RenderUiFrame Update(int width,int height,GuiInputState input,IReadOnlyList<GuiKeyboardEvent> keyboard,string name,bool online)
    {
        Action=null;float scale=Math.Min(1,Math.Min(width/760f,height/560f));
        if(scale<1){width=(int)Math.Ceiling(width/scale);height=(int)Math.Ceiling(height/scale);input=new(input.Pointer with{X=input.Pointer.X/scale,Y=input.Pointer.Y/scale});}
        foreach(var key in keyboard)
        {if(key.Key is 0x09 or 0x28)selected=(selected+1)%ids.Length;else if(key.Key==0x26)selected=(selected+ids.Length-1)%ids.Length;else if(key.Key==0x0D)Action=ids[selected];}
        // Same card style as the account pages and the chase HUD (StormChaseMenuKit).
        float k=ChaseMenuKit.ScaleFor(width,height),W=width/k,H=height/k;const float cardWidth=440,pad=30,inner=cardWidth-pad*2;
        var content=new List<GuiElement>();var kit=new ChaseMenuKit(k,content);float y=pad;
        kit.Text("Paused",pad,y,inner,22,StormChaseHud.Theme.Ink,bold:true);y+=40;
        kit.Text(name+(online?"   ·   server weather keeps running":"   ·   single player"),pad,y,inner,10,StormChaseHud.Theme.Muted);y+=38;
        string[] labels=["RESUME CHASE","FIELD DISPATCH & COMMUNITY","HOME, FURNISHINGS & VISITS","LEAVE TO MAIN MENU","QUIT GAME"];
        for(int i=0;i<ids.Length;i++)
        {
            if(i==3)y+=8;
            var kind=i==0?ChaseMenuKit.Kind.Primary:i>=3?ChaseMenuKit.Kind.Ghost:ChaseMenuKit.Kind.Secondary;
            float tall=i>=3?42:50;
            kit.Button(ids[i],labels[i],pad,y,inner,kind,tall,selected:i==selected,align:GuiTextAlignment.Left);
            y+=tall+10;
        }
        y+=pad-10;
        float left=(W-cardWidth)/2,top=Math.Max(16,(H-y)/2);
        var root=new List<GuiElement>(ChaseMenuKit.Backdrop(width,height,false)){ChaseMenuKit.Fill(0,0,width,height,new(2,8,14,90))};
        root.Add(new GuiOverlayPanel{Translation=new(left*k,top*k),Width=GuiLength.Pixels(cardWidth*k),Height=GuiLength.Pixels(y*k),
            BackgroundColor=new(9,16,23,240),BorderColor=StormChaseHud.Theme.GlassEdge,BorderThickness=Math.Max(1,k),CornerRadius=16*k,Items=content});
        new ChaseMenuKit(k,root).Text("UP / DOWN SELECT   ·   ENTER CONFIRM   ·   ESC RESUME",0,top+y+16,W,8.5f,StormChaseHud.Theme.Faint,align:GuiTextAlignment.Center);
        var document=new GuiDocument(new GuiOverlayPanel{Width=GuiLength.Pixels(width),Height=GuiLength.Pixels(height),Items=root});
        previous=layout.BuildFrame(document,new(width,height),input,previous.Interaction);Action??=previous.Interaction.ActivatedElementId;return composer.Build(previous,++revision);
    }
}
