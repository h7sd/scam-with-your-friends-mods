using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using VoltstroStudios.UnityWebBrowser.Input;

namespace ScamWYF.TikTokPocket
{
    public sealed class PocketBrowserHost : MonoBehaviour
    {
        internal KolkataOsController Controller;
        internal object App;
        private VisualElement viewport;
        private Image image;
        private Label status;
        private WebBrowserInputHandler input;
        private readonly List<InputAction> actions=new List<InputAction>();
        private WorkerBridge bridge;
        private Texture2D texture;
        private bool focused,pointerInside,inputStarted,remember;
        private bool? sentVisible,sentFocus;
        private int width=480,height=854;
        private Vector2 cursor;
        private float resizeAt;
        private bool disposed,restartBlocked;
        private int dragButton=-1;
        private static readonly MethodInfo ignoreKey=AccessTools.Method(typeof(FocusController),"IgnoreEvent",new[]{typeof(EventBase)});

        internal void Initialize(KolkataOsController controller,object app,WebBrowserInputHandler original,VisualElement content)
        {
            Controller=controller;App=app;remember=Plugin.Current.RememberSession.Value;
            input=UnityEngine.Object.Instantiate(original);
            for(Type type=original.GetType();type!=null;type=type.BaseType)
                foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    if(field.FieldType==typeof(InputAction))
                    {
                        var value=field.GetValue(original) as InputAction;var owned=value==null?null:value.Clone();
                        field.SetValue(input,owned);if(owned!=null) actions.Add(owned);
                    }
            content.Clear();
            var root=new VisualElement();root.AddToClassList("kolkata-browser");root.AddToClassList("kolkata-application-typography");
            root.style.flexGrow=1;root.style.minHeight=0;root.style.overflow=Overflow.Hidden;
            var controls=new VisualElement();controls.AddToClassList("tiktok-pocket-controls");controls.style.flexDirection=FlexDirection.Row;
            controls.Add(new Button(()=>Send(new JObject{{"type","back"}})){text="Back"});
            controls.Add(new Button(Refresh){text="Refresh"});root.Add(controls);
            viewport=new VisualElement {name="tiktok-pocket-viewport",focusable=true};
            viewport.style.flexGrow=1;viewport.style.minHeight=0;viewport.style.overflow=Overflow.Hidden;
            image=new Image {name="tiktok-pocket-image",scaleMode=ScaleMode.StretchToFill,pickingMode=PickingMode.Ignore};
            image.style.width=Length.Percent(100);image.style.height=Length.Percent(100);viewport.Add(image);root.Add(viewport);
            status=new Label("TikTok Pocket is ready to open.");status.style.whiteSpace=WhiteSpace.Normal;root.Add(status);content.Add(root);
            viewport.RegisterCallback<PointerEnterEvent>(evt=>pointerInside=true);
            viewport.RegisterCallback<PointerLeaveEvent>(evt=>{pointerInside=false;Send(new JObject{{"type","leave"}});});
            viewport.RegisterCallback<PointerMoveEvent>(evt=>{cursor=evt.localPosition;Pointer("move",dragButton,evt.localPosition);});
            viewport.RegisterCallback<PointerDownEvent>(evt=>
            {
                if(!BrowserHooks.InputAllowed(this) || bridge==null || !bridge.Ready) return;
                focused=true;viewport.Focus();viewport.CapturePointer(evt.pointerId);StartInput();
                Send(new JObject{{"type","focus"},{"active",true}});sentFocus=true;
                dragButton=evt.button;cursor=evt.localPosition;Pointer("down",evt.button,evt.localPosition);evt.StopPropagation();
            });
            viewport.RegisterCallback<PointerUpEvent>(evt=>
            {
                cursor=evt.localPosition;Pointer("up",evt.button,evt.localPosition);dragButton=-1;
                if(viewport.HasPointerCapture(evt.pointerId)) viewport.ReleasePointer(evt.pointerId);evt.StopPropagation();
            });
            viewport.RegisterCallback<KeyDownEvent>(IgnoreViewportKey);
            viewport.RegisterCallback<KeyUpEvent>(IgnoreViewportKey);
            viewport.RegisterCallback<FocusOutEvent>(evt=>focused=false);
            viewport.RegisterCallback<GeometryChangedEvent>(evt=>resizeAt=Time.unscaledTime+0.15f);
        }
        private void Update()
        {
            if(disposed || Plugin.Current==null) return;
            bool visible=BrowserHooks.Visible(this);
            if(bridge==null && visible && !restartBlocked) StartBrowser();
            if(bridge==null) return;
            string message;
            while((message=bridge.TakeStatus())!=null) Report(message);
            var source=bridge;var frame=source.TakeFrame();
            if(frame!=null)
            {
                try
                {
                    if(texture==null || texture.width!=frame.Width || texture.height!=frame.Height)
                    {
                        if(texture!=null) UnityEngine.Object.Destroy(texture);
                        texture=new Texture2D(frame.Width,frame.Height,TextureFormat.BGRA32,false);image.image=texture;
                    }
                    texture.LoadRawTextureData(frame.Pixels);texture.Apply(false,false);
                }
                catch {Report("start_failed");StopBrowser();restartBlocked=true;return;}
                finally {source.Release(frame);}
            }
            if(!bridge.Ready) {StopInput();return;}
            if(sentVisible!=visible) {Send(new JObject{{"type","show"},{"visible",visible}});sentVisible=visible;}
            bool active=visible && (focused || pointerInside) && BrowserHooks.InputAllowed(this);
            if(sentFocus!=active) {Send(new JObject{{"type","focus"},{"active",active}});sentFocus=active;}
            if(resizeAt!=0 && Time.unscaledTime>=resizeAt)
            {
                resizeAt=0;
                int desiredWidth=Mathf.Clamp(Mathf.RoundToInt(viewport.resolvedStyle.width),240,1080);
                int desiredHeight=Mathf.Clamp(Mathf.RoundToInt(viewport.resolvedStyle.height),320,1920);
                if(desiredWidth!=width || desiredHeight!=height)
                {width=desiredWidth;height=desiredHeight;Send(new JObject{{"type","resize"},{"width",width},{"height",height}});}
            }
            if(!active) {StopInput();return;}
            StartInput();
            try
            {
                int modifiers=Modifiers();
                var down=input.GetDownKeys();var up=input.GetUpKeys();
                if(focused)
                {
                    foreach(var key in down) Send(new JObject{{"type","keyDown"},{"vk",(int)key},{"modifiers",modifiers}});
                    foreach(var key in up) Send(new JObject{{"type","keyUp"},{"vk",(int)key},{"modifiers",modifiers}});
                }
                string text=input.GetFrameInputBuffer();
                if(focused && !string.IsNullOrEmpty(text))
                {
                    var printable=new System.Text.StringBuilder();foreach(char character in text) if(character>=' ') printable.Append(character);
                    text=printable.ToString();
                    for(int start=0;start<text.Length;)
                    {
                        int count=Math.Min(256,text.Length-start);
                        if(count>1 && start+count<text.Length && text[start+count-1]>='\ud800' && text[start+count-1]<='\udbff') count--;
                        Send(new JObject{{"type","text"},{"text",text.Substring(start,count)}});start+=count;
                    }
                }
                if(pointerInside)
                {
                    float wheel=input.GetScroll();if(Mathf.Abs(wheel)>0.0001f)
                    {
                        var point=Point(cursor);
                        Send(new JObject{{"type","wheel"},{"x",point.x},{"y",point.y},{"delta",Mathf.Clamp(Mathf.RoundToInt(wheel*height),-1200,1200)},{"modifiers",modifiers}});
                    }
                }
            }
            catch {Report("input_unavailable");StopInput();}
        }
        private void IgnoreViewportKey(EventBase evt)
        {
            if(!focused || !BrowserHooks.InputAllowed(this)) return;
            evt.StopImmediatePropagation();
            if(ignoreKey!=null && viewport.focusController!=null) ignoreKey.Invoke(viewport.focusController,new object[]{evt});
        }
        private void StartBrowser()
        {
            bridge=new WorkerBridge(Plugin.Current.WorkerPath,remember);bridge.Start(width,height);
            status.text="Starting TikTok's video browser...";
        }
        private void Refresh()
        {
            if(bridge!=null && bridge.Ready) Send(new JObject{{"type","refresh"}});
            else {restartBlocked=false;StopBrowser();if(BrowserHooks.Visible(this)) StartBrowser();}
        }
        private void Pointer(string kind,int button,Vector3 local)
        {
            if(!BrowserHooks.InputAllowed(this) || bridge==null || !bridge.Ready) return;
            var point=Point(local);
            var message=new JObject{{"type",kind},{"x",point.x},{"y",point.y},{"modifiers",Modifiers()}};
            if(button>=0) message["button"]=button==0?"left":button==1?"right":"middle";
            Send(message);
        }
        private Vector2Int Point(Vector2 local)
        {
            float w=viewport.resolvedStyle.width,h=viewport.resolvedStyle.height;
            return new Vector2Int(Mathf.Clamp(Mathf.FloorToInt(local.x/Mathf.Max(1,w)*width),0,width-1),
                Mathf.Clamp(Mathf.FloorToInt(local.y/Mathf.Max(1,h)*height),0,height-1));
        }
        private static int Modifiers()
        {
            var keyboard=Keyboard.current;if(keyboard==null) return 0;
            return (keyboard.altKey.isPressed?1:0)|(keyboard.ctrlKey.isPressed?2:0)|(keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed?4:0)|(keyboard.shiftKey.isPressed?8:0);
        }
        private void StartInput() {if(inputStarted) return;input.OnStart();inputStarted=true;}
        private void StopInput() {if(!inputStarted) return;input.OnStop();inputStarted=false;}
        private void Send(JObject message) {if(bridge!=null) bridge.Send(message);}
        internal void Probe()
        {
            if(bridge!=null && bridge.Ready) {Send(new JObject{{"type","probe"}});Plugin.Current.SetStatus("Checking video compatibility. No URLs or account data are collected.");}
            else Plugin.Current.SetStatus("Open TikTok Pocket and wait for the page to load before checking video compatibility.");
        }
        private void Report(string code)
        {
            string message;
            switch(code)
            {
                case "ready":message="TikTok is open. Sign-in is optional and stays in its browser profile on this PC.";break;
                case "runtime_missing":message="Microsoft Edge WebView2 Runtime is missing. Install Microsoft Edge WebView2 Runtime, then try Refresh.";break;
                case "helper_invalid":message="TikTok's browser files are missing or incomplete. Reinstall TikTok Pocket.";break;
                case "profile_error":message="TikTok's local browser profile could not be opened. Close other TikTok Pocket sessions and try Refresh.";break;
                case "navigation_failed":message="TikTok could not load. Check your connection and try Refresh.";break;
                case "video_unavailable":message="This video is unavailable. Try another video or Refresh.";break;
                case "input_unavailable":message="TikTok input stopped. Use Refresh to restart its browser.";break;
                case "disconnected":case "start_failed":message="TikTok's browser could not start or disconnected. Use Refresh to try again.";break;
                default:
                    if(!code.StartsWith("Video compatibility: ",StringComparison.Ordinal)) return;
                    message=code;Plugin.Current.ReportCompatibility(code);break;
            }
            status.text=message;Plugin.Current.SetStatus(message);
            if(code=="ready") status.style.display=DisplayStyle.None;else status.style.display=DisplayStyle.Flex;
        }
        internal void ResetProfile()
        {
            if(Plugin.Current==null || remember==Plugin.Current.RememberSession.Value) return;
            remember=Plugin.Current.RememberSession.Value;restartBlocked=false;StopBrowser();
        }
        private void StopBrowser()
        {
            StopInput();focused=false;dragButton=-1;sentVisible=null;sentFocus=null;
            if(bridge!=null) {bridge.Dispose();bridge=null;}
            if(texture!=null) UnityEngine.Object.Destroy(texture);texture=null;if(image!=null) image.image=null;
        }
        internal void DisposeBrowser()
        {
            if(disposed) return;disposed=true;StopBrowser();
            foreach(var action in actions) action.Dispose();actions.Clear();
            if(input!=null) UnityEngine.Object.Destroy(input);input=null;
        }
        private void OnDestroy() {DisposeBrowser();}
    }
}
