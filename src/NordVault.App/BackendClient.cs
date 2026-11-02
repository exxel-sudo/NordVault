using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NordVault.Core;

namespace NordVault.App;

public sealed class BackendClient
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public async Task<T> ExecuteAsync<T>(BackendRequest request, CancellationToken cancellation = default)
    {
        if (request.Action is "Discover" or "ValidateBackup") cancellation.ThrowIfCancellationRequested();
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "BitLockerBackend.ps1");
        if (!File.Exists(script)) throw new InvalidOperationException("Programmets BitLocker-modul saknas. Bygg om NordVault.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows-modulen kunde inte startas.");
        // Cancel a read synchronously, before the WPF dispatcher can shut down.
        // Mutating requests deliberately have no cancellation/kill registration.
        using var cancelRead = request.Action is "Discover" or "ValidateBackup"
            ? cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } })
            : default;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        // Secret material goes through a private stdin pipe, never process arguments or a disk request file.
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        if (request.Action is "Discover" or "ValidateBackup")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                await outputTask;
                await errorTask;
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                throw new InvalidOperationException("Windows svarade inte på statuskontrollen. Försök uppdatera igen.");
            }
        }
        else
        {
            // Do not kill an active disk/file operation because of a generic timeout.
            await process.WaitForExitAsync();
        }
        var output = await outputTask;
        _ = await errorTask; // Do not show raw PowerShell diagnostics, which may contain secret objects.
        BackendEnvelope<T>? envelope;
        try { envelope = JsonSerializer.Deserialize<BackendEnvelope<T>>(output, Options); }
        catch (JsonException) { throw new InvalidOperationException("Windows-modulen gav ett oväntat svar. Ingen färdig åtgärd kan bekräftas; uppdatera diskstatus."); }
        if (envelope is null || !envelope.Ok || process.ExitCode != 0)
            throw new InvalidOperationException(envelope?.Message ?? "Windows kunde inte bekräfta åtgärden. Uppdatera diskstatus.");
        return envelope.Data ?? throw new InvalidOperationException("Windows-modulen gav ett tomt svar.");
    }
}
