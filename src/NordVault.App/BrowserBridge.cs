using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace NordVault.App;

/// <summary>
/// Firefox ↔ NordVault. Firefox starts NordVault.exe as a native-messaging host (stdio). That host process only relays
/// frames to the running NordVault over a named pipe that accepts the current Windows user only, and the app only
/// serves clients that are this same NordVault.exe. Passwords leave the app only after a fill confirmed in its own window.
/// </summary>
internal static class BrowserBridge
{
    public const string HostName="se.nordvault.firefox";
    public const string ExtensionId="nordvault@nordvault.local";
    public const int MaximumFrame=1024*1024;
    public static string PipeName=>"NordVault.Browser."+WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>Native-messaging framing: 32-bit little-endian length, then UTF-8 JSON. Null at end of stream.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream,CancellationToken cancellation)
    {
        var header=new byte[4];
        if(!await ReadExactAsync(stream,header,cancellation,true))return null;
        var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <= 0 or > MaximumFrame)throw new IOException("Ogiltigt meddelande från webbläsaren.");
        var body=new byte[length];
        if(!await ReadExactAsync(stream,body,cancellation,false))throw new EndOfStreamException();
        return body;
    }
    private static async Task<bool> ReadExactAsync(Stream stream,byte[] buffer,CancellationToken cancellation,bool allowEnd)
    {
        var read=0;
        while(read<buffer.Length)
        {
            var count=await stream.ReadAsync(buffer.AsMemory(read),cancellation);
            if(count==0){if(allowEnd&&read==0)return false;throw new EndOfStreamException();}
            read+=count;
        }
        return true;
    }
    public static async Task WriteFrameAsync(Stream stream,ReadOnlyMemory<byte> body,CancellationToken cancellation)
    {
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
        await stream.WriteAsync(header,cancellation);await stream.WriteAsync(body,cancellation);await stream.FlushAsync(cancellation);
    }

    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint processId);

    private static bool IsOwnExecutable(NamedPipeServerStream pipe)
    {
        try
        {
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var id))return false;
            using var process=Process.GetProcessById((int)id);
            return string.Equals(Path.GetFullPath(process.MainModule!.FileName),Path.GetFullPath(Environment.ProcessPath!),StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){return false;}
    }

    /// <summary>Runs in the NordVault app. Requests are answered on the UI thread by <paramref name="handler"/>.</summary>
    public sealed class Server(Func<JsonObject,Task<JsonObject>> handler) : IDisposable
    {
        private readonly CancellationTokenSource _stop=new();
        public void Start()=>_=Task.Run(AcceptLoopAsync);
        private async Task AcceptLoopAsync()
        {
            while(!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try{pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,8,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){return;}// another NordVault already serves Firefox
                try{await pipe.WaitForConnectionAsync(_stop.Token);}
                catch(OperationCanceledException){pipe.Dispose();return;}
                catch(IOException){pipe.Dispose();continue;}
                if(!IsOwnExecutable(pipe)){pipe.Dispose();continue;}
                _=ServeAsync(pipe);
            }
        }
        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            using var writeLock=new SemaphoreSlim(1,1);var running=new List<Task>();
            try
            {
                while(await ReadFrameAsync(pipe,_stop.Token) is {} frame)
                {
                    running.RemoveAll(t=>t.IsCompleted);
                    running.Add(AnswerAsync(pipe,frame,writeLock));
                }
            }
            catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){}
            finally{try{await Task.WhenAll(running);}catch{}pipe.Dispose();}
        }
        private async Task AnswerAsync(NamedPipeServerStream pipe,byte[] frame,SemaphoreSlim writeLock)
        {
            JsonObject response;JsonNode? requestId=null;
            try
            {
                var request=JsonNode.Parse(frame) as JsonObject??throw new JsonException();
                requestId=request["requestId"]?.DeepClone();
                response=await await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>handler(request));
            }
            catch(Exception error)when(error is JsonException or InvalidOperationException or IOException or ArgumentException or FormatException or UnauthorizedAccessException)
            {response=Error(error is JsonException?"Ogiltig begäran.":error.Message);}
            finally{Array.Clear(frame);}
            response["requestId"]=requestId;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(response);
            try{await writeLock.WaitAsync(_stop.Token);try{await WriteFrameAsync(pipe,bytes,_stop.Token);}finally{writeLock.Release();}}
            catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){}
            finally{Array.Clear(bytes);}
        }
        public void Dispose(){_stop.Cancel();}
    }

    public static JsonObject Error(string message,bool locked=false)=>new(){["ok"]=false,["error"]=message,["locked"]=locked};
}

/// <summary>Started by Firefox. Relays frames between stdin/stdout and the running NordVault; holds no vault data.</summary>
internal static class NativeHost
{
    public static async Task<int> RunAsync()
    {
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();
        using var outputLock=new SemaphoreSlim(1,1);NamedPipeClientStream? pipe=null;var pipeLock=new SemaphoreSlim(1,1);
        async Task Reply(ReadOnlyMemory<byte> body){await outputLock.WaitAsync();try{await BrowserBridge.WriteFrameAsync(output,body,default);}finally{outputLock.Release();}}
        async Task PumpAsync(NamedPipeClientStream from)
        {
            try{while(await BrowserBridge.ReadFrameAsync(from,default) is {} frame){try{await Reply(frame);}finally{Array.Clear(frame);}}}
            catch(Exception error)when(error is IOException or ObjectDisposedException){}
            await pipeLock.WaitAsync();try{if(pipe==from){pipe.Dispose();pipe=null;}}finally{pipeLock.Release();}
        }
        while(true)
        {
            byte[]? frame;
            try{frame=await BrowserBridge.ReadFrameAsync(input,default);}catch(IOException){return 1;}
            if(frame is null)return 0;
            try
            {
                string? type=null;JsonNode? requestId=null;
                try{var request=JsonNode.Parse(frame) as JsonObject;type=request?["type"]?.GetValue<string>();requestId=request?["requestId"]?.DeepClone();}catch(Exception error)when(error is JsonException or InvalidOperationException or FormatException){}
                NamedPipeClientStream? target;
                await pipeLock.WaitAsync();
                try
                {
                    if(pipe is null)
                    {
                        // Opening NordVault only for actions the user started: never for a page load.
                        pipe=await ConnectAsync(type is "save" or "fill" or "open");
                        if(pipe is not null){var started=pipe;_=Task.Run(()=>PumpAsync(started));}
                    }
                    target=pipe;
                }
                finally{pipeLock.Release();}
                if(target is null)
                {
                    var error=BrowserBridge.Error("NordVault är inte igång. Starta NordVault och lås upp valvet.",true);error["notRunning"]=true;error["requestId"]=requestId;
                    await Reply(JsonSerializer.SerializeToUtf8Bytes(error));continue;
                }
                try{await BrowserBridge.WriteFrameAsync(target,frame,default);}
                catch(IOException){var error=BrowserBridge.Error("Anslutningen till NordVault bröts. Försök igen.");error["requestId"]=requestId;await Reply(JsonSerializer.SerializeToUtf8Bytes(error));}
            }
            finally{Array.Clear(frame);}
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(bool launch)
    {
        var pipe=await TryConnectAsync(500);
        if(pipe is not null||!launch)return pipe;
        try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,WorkingDirectory=AppContext.BaseDirectory});}// shell start: Firefox's stdio handles are not inherited
        catch(System.ComponentModel.Win32Exception){return null;}
        for(var i=0;i<60;i++){pipe=await TryConnectAsync(1000);if(pipe is not null)return pipe;}
        return null;
    }
    private static async Task<NamedPipeClientStream?> TryConnectAsync(int milliseconds)
    {
        var pipe=new NamedPipeClientStream(".",BrowserBridge.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try{await pipe.ConnectAsync(milliseconds);return pipe;}
        catch(Exception error)when(error is TimeoutException or IOException or UnauthorizedAccessException){pipe.Dispose();await Task.Delay(Math.Min(milliseconds,250));return null;}
    }
}

internal static class FirefoxRegistration
{
    private const string Key=@"Software\Mozilla\NativeMessagingHosts\"+BrowserBridge.HostName;
    public static string ExtensionFolder=>Path.Combine(AppContext.BaseDirectory,"browser","firefox");
    private static string ManifestPath=>Path.Combine(AppPaths.DataRoot,"Firefox",BrowserBridge.HostName+".json");
    public enum State{NotRegistered,Ok,Changed}
    public static bool IsRegistered=>Check()==State.Ok;
    /// <summary>
    /// Firefox starts whatever program the manifest names. Ok only if our HKCU key points at our own manifest, which names this
    /// NordVault.exe and allows exactly our extension, and no machine-wide key for the same name exists.
    /// </summary>
    public static State Check()
    {
        try
        {
            using var machine=Registry.LocalMachine.OpenSubKey(Key);
            using var key=Registry.CurrentUser.OpenSubKey(Key);
            if(key is null)return machine is null?State.NotRegistered:State.Changed;
            if(machine is not null||key.GetValue("") is not string path||!string.Equals(Path.GetFullPath(path),Path.GetFullPath(ManifestPath),StringComparison.OrdinalIgnoreCase)||!File.Exists(path)||new FileInfo(path).Length>8192)return State.Changed;
            NordVault.Core.FileCryptoPaths.AssertNoReparsePoints(path);
            var manifest=JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
            var allowed=manifest?["allowed_extensions"] as JsonArray;
            var ok=manifest is not null&&manifest.Count==5&&manifest["name"]?.GetValue<string>()==BrowserBridge.HostName&&manifest["type"]?.GetValue<string>()=="stdio"&&
                string.Equals(manifest["path"]?.GetValue<string>(),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)&&
                allowed is {Count:1}&&allowed[0]?.GetValue<string>()==BrowserBridge.ExtensionId;
            return ok?State.Ok:State.Changed;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Security.SecurityException or ArgumentException){return State.Changed;}
    }
    public static void Register()
    {
        var folder=Path.GetDirectoryName(ManifestPath)!;Directory.CreateDirectory(folder);NordVault.Core.FileCryptoPaths.AssertNoReparsePoints(folder);
        var manifest=new JsonObject{["name"]=BrowserBridge.HostName,["description"]="NordVault lösenordsvalv",["path"]=Environment.ProcessPath,["type"]="stdio",["allowed_extensions"]=new JsonArray(BrowserBridge.ExtensionId)};
        File.WriteAllText(ManifestPath,manifest.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        using var key=Registry.CurrentUser.CreateSubKey(Key);key.SetValue("",ManifestPath);
    }
    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Key,false);
        if(File.Exists(ManifestPath))File.Delete(ManifestPath);
    }
}

/// <summary>A program that reaches the pipe cannot page through the vault: few fills and searches per minute.</summary>
internal sealed class BrowserRateLimit
{
    private readonly Dictionary<string,Queue<DateTimeOffset>> _recent=new(StringComparer.Ordinal);
    private static int PerMinute(string type)=>type switch{"fill"=>6,"save"=>10,"links"=>60,_=>60};
    public bool Allow(string type)
    {
        var now=DateTimeOffset.UtcNow;
        if(!_recent.TryGetValue(type,out var queue))_recent[type]=queue=new Queue<DateTimeOffset>();
        while(queue.Count>0&&now-queue.Peek()>TimeSpan.FromMinutes(1))queue.Dequeue();
        if(queue.Count>=PerMinute(type))return false;
        queue.Enqueue(now);return true;
    }
}
