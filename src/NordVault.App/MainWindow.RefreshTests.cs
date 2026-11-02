using System.Windows;
using NordVault.Core;

namespace NordVault.App;

public partial class MainWindow
{
    // Isolated regression entry point: no window is shown and no disk operation is invoked.
    internal static async Task<object> RunDiskRefreshTestsAsync()
    {
        static void Check(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        var cases=new List<string>();
        var pending=new TaskCompletionSource<DiscoveryResult>();
        var calls=0;
        var window=new MainWindow(false,readDiskStatus:_=>{calls++;return pending.Task;});
        try
        {
            window.ActivityText.Text="Tidigare filåtgärd klar.";
            var refresh=window.RefreshAsync(true);
            Check(!refresh.IsCompleted&&!window.AnyBusy,"Statuskontrollen blockerar programmet.");
            Check(window.AdminButton.IsEnabled&&window._fileTools.CanChooseFile&&window._vaultTools.CanOpenVault,"Admin, filer eller valv är spärrade under avläsningen.");
            Check(window.BusyProgress.Visibility==Visibility.Collapsed&&window.DiskReadText.Text.Contains("bakgrunden"),"Bakgrundskontrollen visar en missvisande arbetsindikator.");
            await window.RefreshAsync(true);
            Check(calls==1,"Överlappande statuskontroller startades.");
            pending.SetResult(new DiscoveryResult{Volumes=[new VolumeInfo{MountPoint="Z:",VolumeId="test-only-volume"}]});
            await refresh;
            Check(window.RefreshButton.IsEnabled&&window.ActivityText.Text=="Tidigare filåtgärd klar.","Statuskontrollen ändrade en annan åtgärds meddelande.");
            Check(window.DiskReadText.Text.Contains("uppdaterad")&&window._timer.Interval==TimeSpan.FromSeconds(60),"Slutförd avläsning saknar status eller viloperiod.");
            cases.Add("Delayed refresh keeps files, vault and admin available and prevents overlapping reads");
        }
        finally{pending.TrySetResult(new DiscoveryResult());window.Close();}
        var failed=new MainWindow(false,readDiskStatus:_=>Task.FromException<DiscoveryResult>(new InvalidOperationException("Test-only unavailable status")));
        try
        {
            await failed.RefreshAsync(true);
            Check(failed.RefreshButton.IsEnabled&&failed.AdminButton.IsEnabled&&failed._fileTools.CanChooseFile&&failed._vaultTools.CanOpenVault,"Fel vid avläsning spärrar programmet.");
            Check(!failed.EncryptButton.IsEnabled&&!failed._hasDiskStatus&&failed.BusyProgress.Visibility==Visibility.Collapsed&&failed.DiskReadText.Text=="Test-only unavailable status","Fel vid avläsning lämnar giltig diskstatus eller en arbetsindikator.");
            cases.Add("Failed refresh allows retry and keeps disk mutations disabled");
        }
        finally{failed.Close();}
        var observedCancellation=false;
        var closing=new MainWindow(false,readDiskStatus:async token=>
        {try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){observedCancellation=true;throw;}return new DiscoveryResult();});
        var closingRead=closing.RefreshAsync(true);
        closing.Close();
        await closingRead;
        Check(observedCancellation&&!closing._timer.IsEnabled&&!closing._refreshing,"Stängning lämnar en statuskontroll eller timer igång.");
        cases.Add("Closing cancels only the pending read-only status check");
        return new{Success=true,Cases=cases.ToArray()};
    }
}
