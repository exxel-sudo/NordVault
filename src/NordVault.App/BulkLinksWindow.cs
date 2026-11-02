using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NordVault.Core;

namespace NordVault.App;

/// <summary>Paste many links at once: one per line, with or without a title, or mixed into text.</summary>
internal sealed class BulkLinksWindow : Window
{
    private readonly TextBox _text=new(){AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.NoWrap,MinHeight=260,MaxHeight=420,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas")};
    private readonly TextBox _note=new();
    private readonly TextBlock _preview=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8),FontWeight=FontWeights.SemiBold};
    private readonly TextBlock _sample=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.DimGray,Margin=new Thickness(0,0,0,10)};
    private readonly CheckBox _strip=new(){IsChecked=true,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,10)};
    private readonly Button _save=new(){Content="Spara länkarna krypterat",IsEnabled=false};
    public List<ImportedBookmark> Links{get;private set;}=[];

    private readonly bool _private;
    public BulkLinksWindow(bool isPrivate=false)
    {
        _private=isPrivate;
        Title="Lägg till många länkar";Width=720;Height=760;MinHeight=520;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _save.Style=(Style)Application.Current.FindResource("Primary");
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Lägg till många länkar på en gång",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10),Text=
            "Klistra in länkarna nedan (Ctrl+V). En per rad fungerar bäst, men de kan också stå i löptext. Skriv gärna en titel på samma rad, till exempel:\n"+
            "  Banken - https://bank.se\n  https://kommun.se/e-tjanster Kommunens e-tjänster\n  exempel.se\n"+
            "Utan titel används webbplatsens namn. Länkar som redan finns i valvet läggs inte till igen."});
        panel.Children.Add(_text);
        panel.Children.Add(_preview);panel.Children.Add(_sample);panel.Children.Add(_strip);
        panel.Children.Add(new TextBlock{Text="Anteckning för alla länkarna (valfritt, till exempel Jobb eller Resor – går att söka på)"});panel.Children.Add(_note);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(_save);panel.Children.Add(buttons);
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _text.TextChanged+=(_,_)=>Preview();
        _save.Click+=(_,_)=>
        {
            var note=new string(_note.Text.Where(c=>!char.IsControl(c)).ToArray()).Trim();if(note.Length>200)note=note[..200];
            Links=LinkParser.Parse(_text.Text,note,_private);
            if(_strip.IsChecked==true)Links=Links.Select(l=>FirefoxImport.HasSecret(l.Url)?FirefoxImport.WithoutSecret(l):l).ToList();
            DialogResult=true;
        };
        Loaded+=(_,_)=>{_text.Focus();if(Clipboard.ContainsText()&&LinkParser.Parse(Clipboard.GetText()).Count>0){_text.Text=Clipboard.GetText();_text.CaretIndex=_text.Text.Length;}};
        Preview();
    }

    private void Preview()
    {
        List<ImportedBookmark> links;
        try{links=LinkParser.Parse(_text.Text);}catch(System.Text.RegularExpressions.RegexMatchTimeoutException){_preview.Text="Texten är för stor att tolka. Dela upp den.";_save.IsEnabled=false;return;}
        _preview.Text=links.Count==0?"Inga länkar hittade ännu.":$"Hittade {links.Count} länkar."+(links.Count>=LinkParser.MaximumLinks?" (högst 5 000 per gång)":"");
        _sample.Text=string.Join("\n",links.Take(6).Select(l=>"• "+l.Title+"  –  "+l.Url))+(links.Count>6?$"\n… och {links.Count-6} till":"");
        var secret=links.Count(l=>FirefoxImport.HasSecret(l.Url));
        _strip.Content=$"⚠ {secret} länkar har hemliga koder i adressen (token=, key=, password= …). Ta bort koderna och spara bara adressen – rekommenderas.";
        _strip.Visibility=secret>0?Visibility.Visible:Visibility.Collapsed;
        _save.IsEnabled=links.Count>0;
    }
}
