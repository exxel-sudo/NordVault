using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.AccessControl;
using NordVault.Core;

var root=Directory.GetCurrentDirectory();
using var scratch=new PrivateWorkspace(Path.Combine(root,"artifacts"));
var runtime=new GnuPgRuntime(Path.Combine(root,"tools","gnupg"));
var archive=new ArchivePackage(runtime);
var password="Portable-test-passphrase-åäö".ToCharArray();
var tests=new List<(string,Func<Task>)>();
void Add(string name,Func<Task> test)=>tests.Add((name,test));
void Expect(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
async Task Reject(Func<Task> operation) { try { await operation(); } catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException) { return; } throw new InvalidOperationException("Osäker indata godkändes."); }
string At(string name)=>Path.Combine(scratch.Path,name);
var source=At("hemliga nycklar.asc"); var bytes=Encoding.UTF8.GetBytes("TEST ONLY — not a real private key\n"); await File.WriteAllBytesAsync(source,bytes);
var vaultRoot=At("vault");
Add("Create/import locks vault and authenticates its encrypted catalog",async()=>
{
    using var vault=await VaultSession.CreateAsync(vaultRoot,runtime,password);
    var entry=await vault.ImportAsync(source);
    Expect(vault.Entries.Count==1&&vault.Entries[0].Name==Path.GetFileName(source),"Import saknas.");
    await Reject(async()=> { using var other=await VaultSession.OpenAsync(vaultRoot,runtime,password); });
    await vault.ExportAsync(entry.Id,At("export.asc"));
    Expect((await File.ReadAllBytesAsync(At("export.asc"))).SequenceEqual(bytes),"Nyckelfilen ändrades.");
    Expect(!Directory.EnumerateFiles(vaultRoot,"*",SearchOption.AllDirectories).Any(p=>p.EndsWith(".asc")),"Klartextnyckel lagrades i valvet.");
});
Add("Wrong vault password reveals no metadata",async()=> { await Reject(async()=> { using var vault=await VaultSession.OpenAsync(vaultRoot,runtime,"Wrong-vault-password".AsMemory()); }); });
Add("Copied vault opens in a new location without DPAPI or original drive",async()=>
{
    var copy=At("copied-vault"); Directory.CreateDirectory(copy); Directory.CreateDirectory(Path.Combine(copy,"entries"));
    foreach(var file in Directory.EnumerateFiles(vaultRoot)) File.Copy(file,Path.Combine(copy,Path.GetFileName(file)));
    foreach(var file in Directory.EnumerateFiles(Path.Combine(vaultRoot,"entries"))) File.Copy(file,Path.Combine(copy,"entries",Path.GetFileName(file)));
    Directory.CreateDirectory(Path.Combine(copy,"keys"));foreach(var file in Directory.EnumerateFiles(Path.Combine(vaultRoot,"keys"))) File.Copy(file,Path.Combine(copy,"keys",Path.GetFileName(file)));
    using var vault=await VaultSession.OpenAsync(copy,runtime,password);
    await vault.ExportAsync(vault.Entries[0].Id,At("copied-export.asc"));
    Expect((await File.ReadAllBytesAsync(At("copied-export.asc"))).SequenceEqual(bytes),"Portabel flytt misslyckades.");
});
Add("Substituted encrypted entry is rejected by authenticated catalog",async()=>
{
    using var vault=await VaultSession.OpenAsync(vaultRoot,runtime,password);
    var path=Path.Combine(vaultRoot,"entries",vault.Entries[0].Id+".gpg"); var cipher=await File.ReadAllBytesAsync(path); cipher[^1]^=1; await File.WriteAllBytesAsync(path,cipher);
    await Reject(()=>vault.ExportAsync(vault.Entries[0].Id,At("tampered-key.asc")));
    Expect(!File.Exists(At("tampered-key.asc")),"Manipulerat objekt publicerades.");
});
Add("Create cannot overwrite an existing vault or unrelated directory",async()=>
{
    await Reject(async()=> { using var vault=await VaultSession.CreateAsync(vaultRoot,runtime,password); });
    var folder=At("other"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder,"keep"),"preserve");
    await Reject(async()=> { using var vault=await VaultSession.CreateAsync(folder,runtime,password); });
    Expect(await File.ReadAllTextAsync(Path.Combine(folder,"keep"))=="preserve","Befintlig fil ändrades.");
});
Add("Passwords, links, notes and accounts persist encrypted and need the master password to read",async()=>
{
    var itemRoot=At("item-vault"); string loginId;
    using(var vault=await VaultSession.CreateAsync(itemRoot,runtime,password))
    {
        var login=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"Banken","https://bank.example.se","anna@example.se","",""),"Sajt-lösen-ÅÄÖ-123".AsMemory(),true);loginId=login.Id;
        await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Link,"Nyheter","nyheter.example.se","","",""),ReadOnlyMemory<char>.Empty,false);
        await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Note,"Portkod","","","","Portkod 4711\nrad två"),ReadOnlyMemory<char>.Empty,false);
        await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Account,"Forum","https://forum.example.se","anna","Google-kontot",""),ReadOnlyMemory<char>.Empty,false);
        Expect(vault.Items.Count==4&&vault.Items.Single(i=>i.Id==loginId).HasPassword,"Poster saknas.");
    }
    var raw=Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(itemRoot,"items.gpg")));
    Expect(!raw.Contains("Banken")&&!raw.Contains("Sajt-lösen")&&!raw.Contains("Portkod"),"Poster lagrades i klartext.");
    using(var vault=await VaultSession.OpenAsync(itemRoot,runtime,password))
    {
        Expect(vault.Items.Count==4&&vault.Items.Single(i=>i.Kind==VaultItemKind.Note).Notes=="Portkod 4711\nrad två","Anteckningen återställdes inte.");
        Expect(vault.Items.Single(i=>i.Kind==VaultItemKind.Account).LinkedAccount=="Google-kontot","Kopplat konto saknas.");
        var wrong=false;try{await vault.RevealPasswordAsync(loginId,"fel-lösenord".AsMemory());}catch(UnauthorizedAccessException){wrong=true;}
        Expect(wrong,"Fel huvudlösenord visade lösenordet.");
        var shown=await vault.RevealPasswordAsync(loginId,password);
        try{Expect(new string(shown)=="Sajt-lösen-ÅÄÖ-123","Fel lösenord visades.");}finally{Array.Clear(shown);}
        Expect(vault.FindLogins(new Uri("https://login.bank.example.se/x")).Count==1,"Underdomän matchade inte.");
        Expect(vault.FindLogins(new Uri("http://bank.example.se/")).Count==0,"https-inloggning erbjöds till http-sida.");
        Expect(vault.FindLogins(new Uri("https://bank.example.se.evil.test/")).Count==0,"Främmande domän matchade.");
        await Reject(async()=>{var leaked=vault.ReleaseForConfirmedFill(loginId,new Uri("https://evil.test/"));Array.Clear(leaked);await Task.CompletedTask;});
        Expect(await vault.SaveBrowserLoginAsync(new Uri("https://bank.example.se/login"),"anna@example.se","Sajt-lösen-ÅÄÖ-123".AsMemory())=="unchanged","Oförändrat lösenord sparades igen.");
        Expect(await vault.SaveBrowserLoginAsync(new Uri("https://bank.example.se/login"),"anna@example.se","Nytt-lösen".AsMemory())=="updated","Lösenordet uppdaterades inte.");
        Expect(await vault.SaveBrowserLoginAsync(new Uri("https://shop.example.se/"),"kalle","Shop-lösen".AsMemory())=="created","Ny inloggning skapades inte.");
        await vault.DeleteItemAsync(vault.Items.Single(i=>i.Kind==VaultItemKind.Link).Id);
        Expect(!await vault.VerifyMasterPasswordAsync("x".AsMemory())&&!await vault.VerifyMasterPasswordAsync("y".AsMemory())&&!await vault.VerifyMasterPasswordAsync("z".AsMemory())&&vault.MasterPasswordLockout&&!await vault.VerifyMasterPasswordAsync(password),"Spärr efter tre fel saknas.");
    }
    using(var vault=await VaultSession.OpenAsync(itemRoot,runtime,password))
    {
        Expect(vault.Items.Count==4&&vault.Items.All(i=>i.Kind!=VaultItemKind.Link),"Borttagning eller ny inloggning sparades inte.");
        var updated=await vault.RevealPasswordAsync(loginId,password);try{Expect(new string(updated)=="Nytt-lösen","Uppdaterat lösenord sparades inte.");}finally{Array.Clear(updated);}
    }
    var other=At("item-vault-other");using(var vault=await VaultSession.CreateAsync(other,runtime,password)){}
    File.Copy(Path.Combine(itemRoot,"items.gpg"),Path.Combine(other,"items.gpg"));
    await Reject(async()=>{using var vault=await VaultSession.OpenAsync(other,runtime,password);});
});
Add("Firefox password CSV and bookmarks import once, without duplicates",async()=>
{
    var csv="\uFEFF\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timePasswordChanged\"\r\n"+
        "\"https://bank.example.se\",\"anna\",\"p,\"\"å\"\" ny rad\",,\"https://bank.example.se\",\"{1}\",\"1\",\"1\"\r\n"+
        "\"https://shop.example.se\",\"\",\"shop-lösen\",,,\"{2}\",\"1\",\"1\"\r\n"+
        "\"chrome://FirefoxAccounts\",\"x\",\"y\",,,\"{3}\",\"1\",\"1\"\r\n";
    var csvBytes=Encoding.UTF8.GetBytes(csv);
    Expect(FirefoxImport.Detect(csvBytes)==FirefoxImport.FileKind.Logins,"CSV kändes inte igen.");
    var logins=FirefoxImport.ParseLogins(csvBytes);
    Expect(logins.Count==2&&new string(logins[0].Password)=="p,\"å\" ny rad"&&logins[0].Username=="anna","CSV tolkades fel.");
    var html="<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Bookmarks</TITLE>\n<H1>Bokmärkesmeny</H1>\n<DL><p>\n"+
        "    <DT><H3 ADD_DATE=\"1\">Nyheter &amp; sport</H3>\n    <DL><p>\n        <DT><A HREF=\"https://nyheter.example.se/\" ADD_DATE=\"1\">Nyheter</A>\n    </DL><p>\n"+
        "    <DT><A HREF=\"https://kommun.example.se\">Kommunen</A>\n    <DT><A HREF=\"place:sort=8\">Senast</A>\n    <DT><A HREF=\"javascript:alert(1)\">Skript</A>\n</DL><p>\n";
    Expect(FirefoxImport.Detect(Encoding.UTF8.GetBytes(html))==FirefoxImport.FileKind.BookmarksHtml,"HTML kändes inte igen.");
    var bookmarks=FirefoxImport.ParseBookmarksHtml(html);
    Expect(bookmarks.Count==2&&bookmarks[0].Folder=="Nyheter & sport"&&bookmarks[1].Folder=="","Bokmärken tolkades fel.");
    var json=Encoding.UTF8.GetBytes("{\"title\":\"\",\"typeCode\":2,\"children\":[{\"title\":\"toolbar\",\"typeCode\":2,\"children\":[{\"title\":\"Forum\",\"typeCode\":1,\"uri\":\"https://forum.example.se/\"}]}]}");
    Expect(FirefoxImport.Detect(json)==FirefoxImport.FileKind.BookmarksJson&&FirefoxImport.ParseBookmarksJson(json).Single().Folder=="toolbar","JSON-bokmärken tolkades fel.");
    var importRoot=At("import-vault");
    using(var vault=await VaultSession.CreateAsync(importRoot,runtime,password))
    {
        var first=await vault.ImportItemsAsync(logins,bookmarks);
        Expect(first==new ImportSummary(4,0,0,2)&&vault.Items.Count(i=>i.MustChange)==2,"Första importen: "+first);
        var second=await vault.ImportItemsAsync(logins,bookmarks);
        Expect(second==new ImportSummary(0,0,4,2)&&vault.Items.Count==4,"Dubbletter skapades: "+second);
    }
    using(var vault=await VaultSession.OpenAsync(importRoot,runtime,password))
    {
        var bank=vault.Items.Single(i=>i.Host=="bank.example.se");
        var shown=await vault.RevealPasswordAsync(bank.Id,password);try{Expect(new string(shown)=="p,\"å\" ny rad","Importerat lösenord ändrades.");}finally{Array.Clear(shown);}
        Expect(vault.Items.Single(i=>i.Title=="Nyheter").Notes=="Firefox-mapp: Nyheter & sport","Mapp sparades inte.");
        Expect(bank.MustChange&&bank.ImportedUtc is not null&&bank.ChangePriority.Rank==3,"Läckmarkering saknas efter omstart.");
        var draft=new VaultItemDraft(bank.Id,bank.Kind,bank.Title,bank.Url,bank.Username,"","");
        await vault.SaveItemAsync(draft,"p,\"å\" ny rad".AsMemory(),true);
        Expect(vault.Items.Single(i=>i.Id==bank.Id).MustChange,"Samma läckta lösenord tog bort markeringen.");
        await vault.SaveItemAsync(draft,"Helt-nytt-lösen-42".AsMemory(),true);
        var changed=vault.Items.Single(i=>i.Id==bank.Id);
        Expect(!changed.MustChange&&changed.PasswordChangedUtc is not null,"Nytt lösenord tog inte bort markeringen.");
        Expect(VaultItemRisk.Category("login.live.com").Rank==1||VaultItemRisk.Category("outlook.live.com").Rank==1,"E-post prioriteras inte.");
        Expect(VaultItemRisk.Category("ads.google.com").Rank==5&&VaultItemRisk.Category("accounts.google.com").Rank==2&&VaultItemRisk.Category("www.loopia.se").Rank==4&&VaultItemRisk.Category("claude.ai").Rank==6,"Fel prioritering.");
        Expect(FirefoxImport.HasSecret("https://x.example.se/login?token=abc")&&FirefoxImport.HasSecret("https://x.example.se/a#access_token=1")&&!FirefoxImport.HasSecret("https://x.example.se/search?q=hej"),"Hemliga adresser upptäcks inte.");
        Expect(FirefoxImport.WithoutSecret(new ImportedBookmark("a","https://x.example.se/p?token=abc#k","")).Url=="https://x.example.se/p","Koden togs inte bort.");
    }
    foreach(var login in logins)Array.Clear(login.Password);
});
Add("Password health flags reused and weak passwords without storing them",async()=>
{
    using var vault=await VaultSession.CreateAsync(At("health-vault"),runtime,password);
    var a=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"A","https://a.example.se","anna","",""),"Samma-Starka-Lösen-2026!".AsMemory(),true);
    var b=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"B","https://b.example.se","anna","",""),"Samma-Starka-Lösen-2026!".AsMemory(),true);
    var c=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"C","https://c.example.se","kalle","",""),"sommar123".AsMemory(),true);
    var d=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"D","https://d.example.se","kalle","",""),"k3#Vq9!xTz2@Lm8$".AsMemory(),true);
    VaultItem Get(VaultItem item)=>vault.Items.Single(i=>i.Id==item.Id);
    Expect(Get(a).Issue==PasswordIssue.Reused&&Get(b).Issue==PasswordIssue.Reused&&Get(c).Issue==PasswordIssue.Weak&&Get(d).Issue==PasswordIssue.None,"Fel hälsa: "+string.Join(",",vault.Items.Select(i=>i.Issue)));
    await vault.SaveItemAsync(new VaultItemDraft(b.Id,b.Kind,b.Title,b.Url,b.Username,"",""),"Annat-Starkt-Lösen-7731#".AsMemory(),true);
    Expect(Get(a).Issue==PasswordIssue.None&&Get(b).Issue==PasswordIssue.None,"Återanvändning försvann inte efter byte.");
    var sent=new List<string>();var dSha=Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("k3#Vq9!xTz2@Lm8$")));
    var found=await vault.CheckBreachesAsync((prefix,_)=>{sent.Add(prefix);return Task.FromResult(prefix==dSha[..5]?"0000000000000000000000000000000000A:0\r\n"+dSha[5..]+":12\r\n":"0000000000000000000000000000000000B:0");});
    Expect(found==1&&sent.All(p=>p.Length==5)&&sent.Count==sent.Distinct().Count()&&sent.Count<=4,"Läckkontrollen skickade fel eller hittade fel: "+found+" "+sent.Count);
    Expect(Get(d).Issue==PasswordIssue.Breached&&Get(d).NeedsAttention,"Läckmarkering saknas.");
    Expect(!PwnedPasswords.Contains("ABC:0",("ABC")),"Utfyllnadsrad räknades som träff.");
    await vault.SaveItemAsync(new VaultItemDraft(d.Id,d.Kind,d.Title,d.Url,d.Username,"",""),"Nytt-Lösen-Efter-Läcka-91!".AsMemory(),true);
    Expect(Get(d).Issue==PasswordIssue.None,"Läckmarkering försvann inte efter byte.");
    var raw=Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(At("health-vault"),"items.gpg")));
    Expect(!raw.Contains("Reused")&&!raw.Contains("Issue"),"Hälsa lagrades i klartext.");
});
Add("New vaults use a random vault key behind the master-password capsule",async()=>
{
    var root=At("key-vault");
    using(var vault=await VaultSession.CreateAsync(root,runtime,password)){await vault.ImportAsync(source);}
    Expect(File.Exists(Path.Combine(root,"keys","password.gpg")),"Lösenordskapsel saknas.");
    await Reject(async()=>{using var _=await new FileCryptoService(runtime).DecryptMetadataAsync(Path.Combine(root,"catalog.gpg"),password);});
    using var reopened=await VaultSession.OpenAsync(root,runtime,password);
    await reopened.ExportAsync(reopened.Entries[0].Id,At("key-export.asc"));
    Expect((await File.ReadAllBytesAsync(At("key-export.asc"))).SequenceEqual(bytes),"Fil via valvnyckel ändrades.");
});
Add("A 0.3.x vault gets a capsule, keeps its files and still opens with the old format",async()=>
{
    var root=At("legacy-key-vault");Directory.CreateDirectory(Path.Combine(root,"entries"));
    var crypto=new FileCryptoService(runtime);var catalog=JsonSerializer.SerializeToUtf8Bytes(VaultCatalog.Empty());
    await crypto.EncryptMetadataAsync(catalog,Path.Combine(root,"catalog.gpg"),password);
    var before=await File.ReadAllBytesAsync(Path.Combine(root,"catalog.gpg"));
    using(var vault=await VaultSession.OpenAsync(root,runtime,password))
    {
        Expect(File.Exists(Path.Combine(root,"keys","password.gpg")),"Gammalt valv fick ingen kapsel.");
        Expect(await vault.VerifyMasterPasswordAsync(password),"Huvudlösenordet godtogs inte efter migrering.");
    }
    Expect((await File.ReadAllBytesAsync(Path.Combine(root,"catalog.gpg"))).SequenceEqual(before),"Katalogen skrevs om vid migrering.");
    using var direct=await crypto.DecryptMetadataAsync(Path.Combine(root,"catalog.gpg"),password); // what 0.3.x does
    using var again=await VaultSession.OpenAsync(root,runtime,password);
});
Add("Recovery codes open the vault once, force a new master password and can be replaced",async()=>
{
    var root=At("recovery-vault");List<char[]> codes;
    using(var vault=await VaultSession.CreateAsync(root,runtime,password))
    {
        await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Note,"Kvar","","","","text"),ReadOnlyMemory<char>.Empty,false);
        await Reject(async()=>{try{await vault.CreateRecoveryCodesAsync("fel-lösenord".AsMemory());}catch(UnauthorizedAccessException){throw new IOException("nekad");}});
        codes=await vault.CreateRecoveryCodesAsync(password);
        Expect(codes.Count==5&&codes.All(c=>c.Length==40)&&codes.Select(c=>new string(c)).Distinct().Count()==5&&vault.RecoveryCodeCount==5,"Koder skapades fel.");
    }
    foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
        Expect(!codes.Any(c=>Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(new string(c))),"Kod lagrades i klartext.");
    await Reject(async()=>{using var v=await VaultSession.OpenWithRecoveryCodeAsync(root,runtime,"ABCDE-ABCDE-ABCDE-ABCDE-ABCDE-ABCDE-ABCDE-ABCDE".AsMemory());});
    var typed=VaultKeyring.Format(codes[0]).ToLowerInvariant().Replace("-"," ");var newPassword="Nytt-huvudlösenord-efter-kod-2026".ToCharArray();
    using(var vault=await VaultSession.OpenWithRecoveryCodeAsync(root,runtime,typed.AsMemory()))
    {
        Expect(vault.UsedRecoveryCapsule is not null&&vault.Items.Single().Title=="Kvar","Koden öppnade inte valvet.");
        await vault.ChangeMasterPasswordAsync(null,newPassword);
        Expect(vault.RecoveryCodeCount==4,"Koden förbrukades inte.");
    }
    await Reject(async()=>{using var v=await VaultSession.OpenAsync(root,runtime,password);});
    await Reject(async()=>{using var v=await VaultSession.OpenWithRecoveryCodeAsync(root,runtime,typed.AsMemory());});
    using(var vault=await VaultSession.OpenAsync(root,runtime,newPassword))
    {
        var wrong=false;try{await vault.ChangeMasterPasswordAsync("fel".AsMemory(),password);}catch(UnauthorizedAccessException){wrong=true;}
        Expect(wrong,"Byte utan rätt nuvarande lösenord godtogs.");
        var fresh=await vault.CreateRecoveryCodesAsync(newPassword,3);
        Expect(vault.RecoveryCodeCount==3,"Gamla koder togs inte bort.");
        await Reject(async()=>{using var v=await VaultSession.OpenWithRecoveryCodeAsync(root,runtime,new string(codes[1]).AsMemory());});
        foreach(var code in fresh)Array.Clear(code);
    }
    foreach(var code in codes)Array.Clear(code);Array.Clear(newPassword);
    Expect(!WindowsHello.IsEnabled(root),"Windows Hello påslaget utan begäran.");
});
Add("Pasted text becomes links with titles, without duplicates",async()=>
{
    var pasted="Banken - https://bank.example.se/\r\nhttps://kommun.example.se/e-tjanster Kommunens e-tjänster\nexempel.se\n\nSe https://a.example.se och https://b.example.se/x?q=1.\nhttps://bank.example.se\njavascript:alert(1)\nbara text utan länk";
    var links=LinkParser.Parse(pasted,"Jobb");
    Expect(links.Count==5,"Fel antal länkar: "+links.Count+" "+string.Join(" | ",links.Select(l=>l.Title+"="+l.Url)));
    Expect(links[0].Title=="Banken"&&links[1].Title=="Kommunens e-tjänster"&&links[2].Url=="https://exempel.se/"&&links[2].Title=="exempel.se","Titlar tolkades fel.");
    Expect(links[3].Title=="a.example.se"&&links[4].Url=="https://b.example.se/x?q=1"&&links.All(l=>l.Note=="Jobb"),"Flera länkar på en rad tolkades fel.");
    using var vault=await VaultSession.CreateAsync(At("bulk-vault"),runtime,password);
    var first=await vault.ImportItemsAsync([],links);var second=await vault.ImportItemsAsync([],LinkParser.Parse(pasted));
    Expect(first.Created==5&&second.Created==0&&second.Skipped==5&&vault.Items.All(i=>i.Notes=="Jobb"),"Länkar dubblerades eller anteckningen saknas.");
});
Add("Private links stay in Privat, survive reopening and can move in and out",async()=>
{
    var root=At("private-vault");
    using(var vault=await VaultSession.CreateAsync(root,runtime,password))
    {
        var normal=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Link,"Vanlig","https://vanlig.example.se","","",""),ReadOnlyMemory<char>.Empty,false);
        var hidden=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Link,"Hemlig","https://hemlig.example.se","","","min anteckning",true),ReadOnlyMemory<char>.Empty,false);
        Expect(hidden.Private&&!normal.Private,"Privat-flaggan sattes inte.");
        var bulk=await vault.ImportItemsAsync([],LinkParser.Parse("https://vanlig.example.se\nhttps://ny.example.se","",true));
        Expect(bulk.Created==2&&vault.Items.Count(i=>i.Private)==3,"Massimport till Privat blev fel: "+bulk);
        await vault.SetPrivateAsync([normal.Id],true);
        Expect(vault.Items.Single(i=>i.Id==normal.Id).Private,"Flytt till Privat misslyckades.");
        await vault.SetPrivateAsync([normal.Id],false);
        var login=await vault.SaveItemAsync(new VaultItemDraft(null,VaultItemKind.Login,"L","https://l.example.se","u","",""),"lösen-1234567890".AsMemory(),true);
        await vault.SetPrivateAsync([login.Id,hidden.Id],true);
        Expect(!vault.Items.Single(i=>i.Id==login.Id).Private,"En inloggning blev privat.");
        Expect(await vault.UnlockPrivateAsync(password,IntPtr.Zero,false)&&!await vault.UnlockPrivateAsync("fel".AsMemory(),IntPtr.Zero,false),"Privat kräver inte rätt huvudlösenord.");
    }
    var raw=Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root,"items.gpg")));
    Expect(!raw.Contains("Hemlig")&&!raw.Contains("Private"),"Privata länkar lagrades i klartext.");
    using(var vault=await VaultSession.OpenAsync(root,runtime,password))
    {
        var h=vault.Items.Single(i=>i.Title=="Hemlig");
        Expect(h.Private&&h.Notes=="min anteckning"&&vault.Items.Count(i=>i.Private)==3&&!vault.Items.Single(i=>i.Title=="Vanlig").Private,"Privat sparades inte.");
    }
});
Add("Firefox inbox saves while locked, opens only with the vault and needs approval",async()=>
{
    var root=At("inbox-vault");
    using(var vault=await VaultSession.CreateAsync(root,runtime,password))
    {
        Expect(LoginInbox.ReadPublicKey(root) is null,"Brevlådan fanns före första upplåsningen.");
        Expect(await vault.PrepareInboxAsync() is {Repaired:false},"Brevlådans nyckel skapades inte.");
    }
    var publicKey=LoginInbox.ReadPublicKey(root)!;
    var page=new Uri("https://login.example.se/steg2");
    await LoginInbox.AddAsync(root,runtime,publicKey,page,"anna@example.se","Inbox-test-only-1".AsMemory());
    await Task.Delay(50);
    await LoginInbox.AddAsync(root,runtime,publicKey,page,"anna@example.se","Inbox-test-only-2".AsMemory());
    var files=LoginInbox.Files(root);
    Expect(files.Length==2&&!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(files[0])).Contains("anna"),"Brevlådan sparade fel eller i klartext.");
    await Reject(async()=>{using var _=await new FileCryptoService(runtime).DecryptMetadataAsync(files[0],password);});
    await File.WriteAllBytesAsync(Path.Combine(LoginInbox.Folder(root),new string('a',32)+".gpg"),Encoding.UTF8.GetBytes("not gpg"));
    using(var vault=await VaultSession.OpenAsync(root,runtime,password))
    {
        Expect(await vault.PrepareInboxAsync() is {Repaired:false},"Oförändrad nyckel markerades som utbytt.");
        var inbox=await vault.ReadInboxAsync();
        Expect(inbox.Logins.Count==2&&inbox.Unreadable.Count==1,"Brevlådan lästes fel.");
        Expect(inbox.Logins.All(l=>l.Username=="anna@example.se"&&l.Url=="https://login.example.se"),"Användarnamn eller sida tappades.");
        Expect(await vault.AcceptInboxAsync(inbox.Logins)==2,"Inloggningarna lades inte in.");
        vault.DiscardInbox(inbox.Unreadable);
        foreach(var login in inbox.Logins)login.Dispose();
        var item=vault.Items.Single(i=>i.Kind==VaultItemKind.Login);
        var secret=await vault.RevealPasswordAsync(item.Id,password);
        Expect(item.Username=="anna@example.se"&&new string(secret)=="Inbox-test-only-2","Senaste inloggningen vann inte.");
        Expect(LoginInbox.Files(root).Length==0,"Brevlådan tömdes inte.");
    }
    // Something else replaced the public key: detected and restored on the next unlock.
    var original=await File.ReadAllBytesAsync(LoginInbox.PublicKeyPath(root));
    await File.WriteAllBytesAsync(LoginInbox.PublicKeyPath(root),Encoding.UTF8.GetBytes("replaced key"));
    using(var vault=await VaultSession.OpenAsync(root,runtime,password))
        Expect(await vault.PrepareInboxAsync() is {Repaired:true},"Utbytt nyckel upptäcktes inte.");
    Expect((await File.ReadAllBytesAsync(LoginInbox.PublicKeyPath(root))).SequenceEqual(original),"Nyckeln återställdes inte.");
});
Add("Deleting a vault file removes it from the catalog and disk",async()=>
{
    var deleteRoot=At("delete-vault");
    using var vault=await VaultSession.CreateAsync(deleteRoot,runtime,password);
    var entry=await vault.ImportAsync(source);await vault.DeleteAsync(entry.Id);
    Expect(vault.Entries.Count==0&&!File.Exists(Path.Combine(deleteRoot,"entries",entry.Id+".gpg")),"Filen togs inte bort.");
});
var stub=At("reader-test.exe"); await File.WriteAllBytesAsync(stub,Encoding.ASCII.GetBytes("MZTEST STUB — deliberately inert test data"));
var stubHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(stub))); var package=At("private.exe");
Add("Encrypted multi-file package restores exact bytes and duplicate names",async()=>
{
    var secondFolder=At("second"); Directory.CreateDirectory(secondFolder); var second=Path.Combine(secondFolder,Path.GetFileName(source)); await File.WriteAllTextAsync(second,"a different test file");
    await archive.CreateAsync([source,second],package,password,stub,stubHash);
    var destination=await archive.ExtractAsync(package,scratch.Path,password);
    Expect((await File.ReadAllBytesAsync(Path.Combine(destination,Path.GetFileName(source)))).SequenceEqual(bytes),"Arkivfil ändrades.");
    Expect(Directory.EnumerateFiles(destination).Count()==2,"Dubbla namn tappades.");
});
Add("Folder archive restores the whole tree and rejects unsafe folder names",async()=>
{
    var tree=At("Semester bilder"); Directory.CreateDirectory(Path.Combine(tree,"Dag 1","kväll")); Directory.CreateDirectory(Path.Combine(tree,"tom"));
    await File.WriteAllTextAsync(Path.Combine(tree,"a.txt"),"rot"); await File.WriteAllTextAsync(Path.Combine(tree,"Dag 1","b.txt"),"dag");
    await File.WriteAllBytesAsync(Path.Combine(tree,"Dag 1","kväll","c.bin"),[0,1,2,255]);
    var folderPackage=At("folder.exe");
    await archive.CreateAsync([tree],folderPackage,password,stub,stubHash);
    var destination=await archive.ExtractAsync(folderPackage,scratch.Path,password);
    Expect(await File.ReadAllTextAsync(Path.Combine(destination,"Semester bilder","Dag 1","b.txt"))=="dag","Undermappen återställdes inte.");
    Expect((await File.ReadAllBytesAsync(Path.Combine(destination,"Semester bilder","Dag 1","kväll","c.bin"))).SequenceEqual(new byte[]{0,1,2,255}),"Djup fil ändrades.");
    Expect(Directory.EnumerateFiles(destination,"*",SearchOption.AllDirectories).Count()==3,"Fel antal filer i mapparkivet.");
    Expect(VaultCatalog.IsSafeRelativeName("A/b/c.txt")&&!VaultCatalog.IsSafeRelativeName("A/../c.txt")&&!VaultCatalog.IsSafeRelativeName("/c.txt")&&!VaultCatalog.IsSafeRelativeName("A//c")&&!VaultCatalog.IsSafeRelativeName("A/C:x")&&!VaultCatalog.IsSafeRelativeName(@"A\b"),"Osäkra mappnamn godkändes.");
    Expect(!VaultCatalog.IsSafeName("A/b"),"Valvet tillåter mappar.");
    var empty=At("tom mapp"); Directory.CreateDirectory(empty); await Reject(()=>archive.CreateAsync([empty],At("empty.exe"),password,stub,stubHash));
    Expect(!File.Exists(At("empty.exe")),"Tomt arkiv skapades.");
});
Add("Free-space wipe fills, overwrites in place for every pass and removes its files, also when cancelled",async()=>
{
    Expect(FreeSpaceWiper.Patterns(1).SequenceEqual(new[]{-1})&&FreeSpaceWiper.Patterns(3).Length==3&&FreeSpaceWiper.Patterns(7).Length==7,"Fel passmönster.");
    await Reject(()=>Task.Run(()=>FreeSpaceWiper.Patterns(2)));
    Expect(FreeSpaceWiper.Refuse(new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!)) is not null,"Windows-disken får inte fyllas.");
    var folder=At(FreeSpaceWiper.FolderPrefix+Guid.NewGuid().ToString("N")); var seen=new List<WipeProgress>();
    await FreeSpaceWiper.RunAsync(folder,3,9L*1024*1024+123,long.MaxValue,new SyncProgress<WipeProgress>(seen.Add),CancellationToken.None);
    Expect(!Directory.Exists(folder),"Utfyllnadsfilerna togs inte bort.");
    Expect(seen.Any(p=>p.Pass==3)&&seen[^1].Written==3*(9L*1024*1024+123)&&seen[^1].Total==seen[^1].Written,"Alla pass skrevs inte: "+seen[^1]);
    var cancelled=At(FreeSpaceWiper.FolderPrefix+Guid.NewGuid().ToString("N")); using var stop=new CancellationTokenSource();
    try{await FreeSpaceWiper.RunAsync(cancelled,7,64L*1024*1024,long.MaxValue,new SyncProgress<WipeProgress>(_=>stop.Cancel()),stop.Token);throw new InvalidOperationException("Avbrott ignorerades.");}
    catch(OperationCanceledException){}
    Expect(!Directory.Exists(cancelled),"Avbruten rensning lämnade filer.");
    var leftover=At("leftovers"); Directory.CreateDirectory(Path.Combine(leftover,FreeSpaceWiper.FolderPrefix+Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(Path.Combine(leftover,FreeSpaceWiper.FolderPrefix+"mina-filer"));
    FreeSpaceWiper.RemoveLeftovers(leftover);
    Expect(Directory.EnumerateDirectories(leftover).Select(Path.GetFileName).SequenceEqual([FreeSpaceWiper.FolderPrefix+"mina-filer"]),"Fel mappar städades bort.");
});
Add("Wrong archive password publishes no output directory",async()=>
{
    var before=Directory.EnumerateDirectories(scratch.Path).Order().ToArray();
    await Reject(()=>archive.ExtractAsync(package,scratch.Path,"Incorrect-archive-password".AsMemory()));
    Expect(Directory.EnumerateDirectories(scratch.Path).Order().SequenceEqual(before),"Fel lösenord lämnade resultat.");
});
Add("Modified package transport and truncated footer are rejected",async()=>
{
    var data=await File.ReadAllBytesAsync(package); data[^70]^=1; await File.WriteAllBytesAsync(At("damaged.exe"),data);
    await Reject(()=>archive.ExtractAsync(At("damaged.exe"),scratch.Path,password));
    await File.WriteAllBytesAsync(At("truncated.exe"),data[..^20]); await Reject(()=>archive.ExtractAsync(At("truncated.exe"),scratch.Path,password));
});
Add("Modified reader template cannot be executed or packaged",async()=>
{
    await File.AppendAllTextAsync(stub,"changed");
    await Reject(()=>archive.CreateAsync([source],At("invalid-reader.exe"),password,stub,stubHash));
    Expect(!File.Exists(At("invalid-reader.exe")),"Ändrad läsare paketerades.");
});
Add("ZIP traversal, duplicate names and Unix symlinks are rejected",async()=>
{
    foreach(var malicious in new[]{"../escape","/absolute","C:/drive","entries/CON","entries/name:stream"})
    { await Reject(()=> { SafeZip.Relative(malicious); return Task.CompletedTask; }); }
    using var memory=new MemoryStream(); using(var writer=new ZipArchive(memory,ZipArchiveMode.Create,true)) { writer.CreateEntry("catalog.gpg"); writer.CreateEntry("CATALOG.gpg"); }
    memory.Position=0; using var reader=new ZipArchive(memory,ZipArchiveMode.Read,true); await Reject(()=> { SafeZip.Index(reader); return Task.CompletedTask; });
    using var links=new MemoryStream(); using(var writer=new ZipArchive(links,ZipArchiveMode.Create,true)) { var entry=writer.CreateEntry("link"); entry.ExternalAttributes=unchecked((int)0xA1FF0000); }
    links.Position=0; using var linkReader=new ZipArchive(links,ZipArchiveMode.Read,true); await Reject(()=> { SafeZip.Index(linkReader); return Task.CompletedTask; });
});
Add("Portable encrypted files inherit destination access policy",async()=>
{
    using var vault=await VaultSession.CreateAsync(At("acl-vault"),runtime,password);
    var entry=await vault.ImportAsync(source);
    foreach(var path in new[]{Path.Combine(vault.Root,"catalog.gpg"),Path.Combine(vault.Root,"entries",entry.Id+".gpg")})
    {
        var acl=new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Expect(!acl.AreAccessRulesProtected,"Portabel kryptotext har låst ACL-arv.");
        Expect(acl.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)).Count==0,"Explicit skaparkonto finns kvar på kryptotexten.");
    }
});
var appBytes=Encoding.UTF8.GetBytes("test program; never executable");
var helper=At("setup-fixture.exe");await File.WriteAllTextAsync(helper,"inert installer fixture");
var deploymentManifest=new DeploymentManifest(DeploymentService.ProductId,"test-0.3",[new("NordVault.exe",appBytes.Length,Convert.ToHexString(SHA256.HashData(appBytes))),new("docs/readme.txt",appBytes.Length,Convert.ToHexString(SHA256.HashData(appBytes)))]);
var deployment=new DeploymentService(deploymentManifest);
MemoryStream Payload(byte[]? data=null)
{
    var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var file in deploymentManifest.Files){using var output=zip.CreateEntry(file.Path).Open();output.Write(data??appBytes);}memory.Position=0;return memory;
}
Add("Installer rejects unsafe manifests even when passed directly",async()=>
{
    foreach(var path in new[]{"../outside","Data/key.asc","Data","installation.json","NordVault.Setup.exe","CON"})
        await Reject(()=>{_=new DeploymentService(deploymentManifest with{Files=[new(path,1,new string('A',64))]});return Task.CompletedTask;});
    await Reject(()=>{_=new DeploymentService(deploymentManifest with{Files=[new("ok",1,null!)]});return Task.CompletedTask;});
    await Reject(()=>{_=new DeploymentService(deploymentManifest with{Files=[deploymentManifest.Files[0],deploymentManifest.Files[0]]});return Task.CompletedTask;});
});
Add("Installer validates every file and creates a matching uninstall marker",async()=>
{
    using var payload=Payload();var destination=At("installed");await deployment.InstallAsync(payload,destination,"Portable",helper);
    Expect((await File.ReadAllBytesAsync(Path.Combine(destination,"NordVault.exe"))).SequenceEqual(appBytes),"Installationsbytes skiljer sig.");
    var marker=JsonSerializer.Deserialize<InstallationState>(await File.ReadAllTextAsync(Path.Combine(destination,"installation.json")))!;
    Expect(marker.Mode=="Portable"&&marker.Folder==destination&&marker.Version=="test-0.3","Fel installationsmarkör.");
    var directoryAccess=new DirectoryInfo(destination).GetAccessControl(AccessControlSections.Access);
    Expect(!directoryAccess.AreAccessRulesProtected&&directoryAccess.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)).Count==0,"Installerad mapp är bundet till stagingkontot.");
    var access=new FileInfo(Path.Combine(destination,"NordVault.exe")).GetAccessControl(AccessControlSections.Access);
    Expect(!access.AreAccessRulesProtected&&access.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)).Count==0,"Programfilens ACL är inte ärvd från målmappen.");
});
Add("Uninstaller preserves Data, unknown files and changed program files",async()=>
{
    var destination=At("installed");Directory.CreateDirectory(Path.Combine(destination,"Data","Vault"));
    var data=Path.Combine(destination,"Data","Vault","private.gpg");await File.WriteAllTextAsync(data,"test ciphertext");
    var own=Path.Combine(destination,"my-file.txt");await File.WriteAllTextAsync(own,"user data");
    var changed=Path.Combine(destination,"docs","readme.txt");await File.WriteAllTextAsync(changed,"changed program file");
    var result=deployment.Uninstall(destination,helper);
    Expect(result.PreservedFiles==1&&!File.Exists(Path.Combine(destination,"NordVault.exe")),"Fel kända programfiler hanterades.");
    Expect(await File.ReadAllTextAsync(data)=="test ciphertext"&&await File.ReadAllTextAsync(own)=="user data"&&await File.ReadAllTextAsync(changed)=="changed program file","Egna eller ändrade filer raderades.");
});
Add("All-users install preserves the destination's inherited read access",async()=>
{
    var parent=At("shared-install-parent");Directory.CreateDirectory(parent);
    using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();var users=new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid,null);
    var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
    security.AddAccessRule(new FileSystemAccessRule(identity.User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    new DirectoryInfo(parent).SetAccessControl(security);
    var destination=Path.Combine(parent,"NordVault");using var payload=Payload();await deployment.InstallAsync(payload,destination,"AllUsers",helper);
    var rules=new FileInfo(Path.Combine(destination,"NordVault.exe")).GetAccessControl(AccessControlSections.Access).GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>();
    Expect(rules.Any(r=>r.IsInherited&&r.IdentityReference.Equals(users)&&(r.FileSystemRights&FileSystemRights.ReadAndExecute)==FileSystemRights.ReadAndExecute),"Användarna tappade målmappens läs-/körbehörighet.");
    deployment.Uninstall(destination,helper);
});
Add("Installer never overwrites an existing folder or file",async()=>
{
    var destination=At("already-there");Directory.CreateDirectory(destination);await File.WriteAllTextAsync(Path.Combine(destination,"keep"),"keep");
    using var payload=Payload();await Reject(()=>deployment.InstallAsync(payload,destination,"User",helper));
    Expect(await File.ReadAllTextAsync(Path.Combine(destination,"keep"))=="keep","Befintlig fil ändrades.");
    using var second=Payload();await Reject(()=>deployment.InstallAsync(second,source,"User",helper));
});
Add("Incorrect installer hash leaves no published installation",async()=>
{
    var destination=At("bad-install");using var payload=Payload(Encoding.UTF8.GetBytes(new string('X',appBytes.Length)));
    await Reject(()=>deployment.InstallAsync(payload,destination,"User",helper));Expect(!Directory.Exists(destination),"Skadat paket publicerades.");
});
Add("Cancelled installer leaves no published installation",async()=>
{
    var destination=At("cancel-install");using var payload=Payload();using var cancellation=new CancellationTokenSource();cancellation.Cancel();
    try{await deployment.InstallAsync(payload,destination,"User",helper,cancellation.Token);throw new IOException("Avbruten installation fortsatte.");}catch(OperationCanceledException){}
    Expect(!Directory.Exists(destination),"Avbruten installation publicerades.");
});
Add("Installer refuses Windows folders and drive roots",async()=>
{
    foreach(var path in new[]{Path.GetPathRoot(root)!,Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)})
        await Reject(()=>{DeploymentService.ValidateDestination(path);return Task.CompletedTask;});
});
Add("Windows shortcuts preserve Unicode paths and uninstall arguments",async()=>
{
    var link=At("Avinstallera mitt program.lnk");var arguments="--uninstall \""+At("min mapp åäö")+"\"";
    WindowsShortcut.Create(link,helper,arguments);var actual=WindowsShortcut.Read(link);
    Expect(actual.Target==helper&&actual.Arguments==arguments&&actual.WorkingDirectory==scratch.Path,"Genvägens mål eller argument ändrades.");
    Expect(WindowsShortcut.RemoveIfMatches(link,helper,arguments)&&!File.Exists(link),"Egen genväg togs inte bort.");await Task.CompletedTask;
});
Add("Unrelated existing shortcut is never overwritten or removed",async()=>
{
    var other=At("other-app.exe");await File.WriteAllTextAsync(other,"other test fixture");var link=At("preserve.lnk");WindowsShortcut.Create(link,other);
    await Reject(()=>{WindowsShortcut.Create(link,helper);return Task.CompletedTask;});
    Expect(!WindowsShortcut.RemoveIfMatches(link,helper)&&WindowsShortcut.Read(link).Target==other,"Annan genväg ändrades.");
});
var updatedBytes=Encoding.UTF8.GetBytes("updated test program; never executable");var newLibrary=Encoding.UTF8.GetBytes("new fixture module");
var nextManifest=new DeploymentManifest(DeploymentService.ProductId,"test-0.3.2",[new("NordVault.exe",updatedBytes.Length,Convert.ToHexString(SHA256.HashData(updatedBytes))),new("NordVault.New.dll",newLibrary.Length,Convert.ToHexString(SHA256.HashData(newLibrary)))]);
var upgrade=new DeploymentService(nextManifest);
var helperHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(helper)));
MemoryStream UpdatePayload(bool damaged=false)
{
    var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
    {using(var stream=zip.CreateEntry("NordVault.exe").Open())stream.Write(damaged?Encoding.UTF8.GetBytes(new string('X',updatedBytes.Length)):updatedBytes);using(var stream=zip.CreateEntry("NordVault.New.dll").Open())stream.Write(newLibrary);}
    memory.Position=0;return memory;
}
async Task<string> OldInstall(string name)
{var destination=At(name);using var payload=Payload();await deployment.InstallAsync(payload,destination,"Portable",helper);return destination;}
Add("Version update replaces verified program files and preserves Data and own files",async()=>
{
    var folder=await OldInstall("upgrade-good");Directory.CreateDirectory(Path.Combine(folder,"Data","Vault"));
    await File.WriteAllTextAsync(Path.Combine(folder,"Data","Vault","keep.gpg"),"test encrypted content");await File.WriteAllTextAsync(Path.Combine(folder,"own.txt"),"user file");
    using var payload=UpdatePayload();await upgrade.UpdateAsync(payload,folder,"Portable",helper,deploymentManifest,helperHash);
    Expect((await File.ReadAllBytesAsync(Path.Combine(folder,"NordVault.exe"))).SequenceEqual(updatedBytes),"Programmet uppdaterades inte.");
    Expect(DeploymentService.ReadState(folder).Version=="test-0.3.2"&&File.Exists(Path.Combine(folder,"NordVault.New.dll")),"Ny markör saknas.");
    Expect(await File.ReadAllTextAsync(Path.Combine(folder,"Data","Vault","keep.gpg"))=="test encrypted content"&&await File.ReadAllTextAsync(Path.Combine(folder,"own.txt"))=="user file","Egna filer ändrades.");
    Expect(!File.Exists(Path.Combine(folder,"docs","readme.txt")),"Utgått känt programobjekt lämnades kvar.");
});
Add("Damaged update leaves the old installation and marker intact",async()=>
{
    var folder=await OldInstall("upgrade-damaged");using var payload=UpdatePayload(true);
    await Reject(()=>upgrade.UpdateAsync(payload,folder,"Portable",helper,deploymentManifest,helperHash));
    Expect((await File.ReadAllBytesAsync(Path.Combine(folder,"NordVault.exe"))).SequenceEqual(appBytes)&&DeploymentService.ReadState(folder).Version=="test-0.3","Gammal installation ändrades av skadat paket.");
});
Add("Update refuses modified program files and unknown destination collisions",async()=>
{
    var changed=await OldInstall("upgrade-changed");await File.WriteAllTextAsync(Path.Combine(changed,"NordVault.exe"),"modified file");using var payload=UpdatePayload();
    await Reject(()=>upgrade.UpdateAsync(payload,changed,"Portable",helper,deploymentManifest,helperHash));
    Expect(await File.ReadAllTextAsync(Path.Combine(changed,"NordVault.exe"))=="modified file","Ändrad fil försvann.");
    var collision=await OldInstall("upgrade-collision");await File.WriteAllTextAsync(Path.Combine(collision,"NordVault.New.dll"),"user-created file");using var second=UpdatePayload();
    await Reject(()=>upgrade.UpdateAsync(second,collision,"Portable",helper,deploymentManifest,helperHash));
    Expect(await File.ReadAllTextAsync(Path.Combine(collision,"NordVault.New.dll"))=="user-created file","Egen fil skrevs över.");
});
Add("Locked program file prevents update before any old file is changed",async()=>
{
    var folder=await OldInstall("upgrade-locked");using var locked=new FileStream(Path.Combine(folder,"NordVault.exe"),FileMode.Open,FileAccess.Read,FileShare.Read);using var payload=UpdatePayload();
    await Reject(()=>upgrade.UpdateAsync(payload,folder,"Portable",helper,deploymentManifest,helperHash));
    Expect(DeploymentService.ReadState(folder).Version=="test-0.3"&&File.Exists(Path.Combine(folder,"docs","readme.txt")),"Låst installation ändrades.");
});
Add("Cancelled update and changed installation mode preserve the old version",async()=>
{
    var folder=await OldInstall("upgrade-cancelled");using var cancellation=new CancellationTokenSource();cancellation.Cancel();using var payload=UpdatePayload();
    try{await upgrade.UpdateAsync(payload,folder,"Portable",helper,deploymentManifest,helperHash,cancellation.Token);throw new IOException("Avbruten uppdatering fortsatte.");}catch(OperationCanceledException){}
    using var other=UpdatePayload();await Reject(()=>upgrade.UpdateAsync(other,folder,"AllUsers",helper,deploymentManifest,helperHash));
    Expect(DeploymentService.ReadState(folder).Version=="test-0.3"&&(await File.ReadAllBytesAsync(Path.Combine(folder,"NordVault.exe"))).SequenceEqual(appBytes),"Avbruten uppdatering ändrade källan.");
});
Add("A destination race during update rolls back old files without deleting the new user file",async()=>
{
    var folder=await OldInstall("upgrade-race");using var gate=new AsyncGateStream(UpdatePayload());
    var operation=upgrade.UpdateAsync(gate,folder,"Portable",helper,deploymentManifest,helperHash);
    try{await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));await File.WriteAllTextAsync(Path.Combine(folder,"NordVault.New.dll"),"user file created during update");}
    finally{gate.Continue.TrySetResult();}
    await Reject(()=>operation);
    Expect(DeploymentService.ReadState(folder).Version=="test-0.3"&&(await File.ReadAllBytesAsync(Path.Combine(folder,"NordVault.exe"))).SequenceEqual(appBytes)&&File.Exists(Path.Combine(folder,"docs","readme.txt")),"Originalinstallation återställdes inte efter publiceringsfel.");
    Expect(await File.ReadAllTextAsync(Path.Combine(folder,"NordVault.New.dll"))=="user file created during update","Den samtidiga egna filen raderades.");
});
Add("Concurrent installer cannot modify the same destination",async()=>
{
    var destination=At("parallel-install");using var gate=new AsyncGateStream(Payload());var operation=deployment.InstallAsync(gate,destination,"Portable",helper);
    try{await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));using var second=Payload();await Reject(()=>deployment.InstallAsync(second,destination,"Portable",helper));}
    finally{gate.Continue.TrySetResult();}
    await operation;Expect((await File.ReadAllBytesAsync(Path.Combine(destination,"NordVault.exe"))).SequenceEqual(appBytes),"Samtidig installation ändrade publiceringen.");
});
if(args.Contains("--published"))
{
    var published=Path.Combine(root,"release","NordVault-Portable");var template=Path.Combine(published,"NordVault.Reader.exe");var executable=At("standalone-archive.exe");
    Add("Published full installer payload installs and uninstalls in isolated scratch",async()=>
    {
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        var service=new DeploymentService(manifest);var destination=At("full-install");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await service.InstallAsync(payload,destination,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"));
        Expect(File.Exists(Path.Combine(destination,"NordVault.Reader.exe")),"Läsaren saknas i verkliga installationen.");
        Directory.CreateDirectory(Path.Combine(destination,"Data"));await File.WriteAllTextAsync(Path.Combine(destination,"Data","keep.gpg"),"cipher fixture");
        var result=service.Uninstall(destination,Path.Combine(root,"Setup-NordVault-0.4.4.exe"));
        Expect(result.PreservedFiles==0&&!File.Exists(Path.Combine(destination,"NordVault.exe"))&&File.Exists(Path.Combine(destination,"Data","keep.gpg")),"Full avinstallation ändrade egna data.");
    });
    async Task<(int Exit,JsonDocument Report)> RunReader(string secret,string name)
    {
        var report=At(name+".json");var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true,StandardInputEncoding=new UTF8Encoding(false,true)};
        foreach(var argument in new[]{"--test-package",executable,"--test-output",scratch.Path,"--test-report",report})start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ROOT"]=At("no-dotnet");start.Environment["DOTNET_ROOT_X64"]=At("no-dotnet");start.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
        start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]=At("runtime-cache");
        using var process=Process.Start(start)!;var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(secret);process.StandardInput.Close();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
        await error;await output;
        return(process.ExitCode,JsonDocument.Parse(await File.ReadAllTextAsync(report)));
    }
    Add("Actual self-extracting EXE decrypts using bundled runtime and engine",async()=>
    {
        var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(template)));
        await archive.CreateAsync([source],executable,password,template,hash);
        var result=await RunReader(new string(password),"standalone-success");using var report=result.Report;
        Expect(result.Exit==0&&report.RootElement.GetProperty("Success").GetBoolean(),"Portabel körbar läsare misslyckades: "+report.RootElement.ToString());
        var destination=report.RootElement.GetProperty("Destination").GetString()!;
        Expect((await File.ReadAllBytesAsync(Path.Combine(destination,Path.GetFileName(source)))).SequenceEqual(bytes),"Självuppackad fil skiljer sig.");
    });
    Add("Actual self-extracting EXE rejects wrong password with no result",async()=>
    {
        var before=Directory.EnumerateDirectories(scratch.Path,"NordVault-uppackat-*").Order().ToArray();
        var result=await RunReader("incorrect-test-password","standalone-failure");using var report=result.Report;
        Expect(result.Exit!=0&&!report.RootElement.GetProperty("Success").GetBoolean(),"Fel lösenord godkändes av EXE.");
        Expect(Directory.EnumerateDirectories(scratch.Path,"NordVault-uppackat-*").Order().SequenceEqual(before),"EXE publicerade felaktigt resultat.");
    });
    async Task RunFixture(string executable,string[] arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
        Expect(process.ExitCode==0,"Publicerad programprocess misslyckades: "+Path.GetFileName(executable));
    }
    Add("Published app constructs its main window without a mandatory vault",async()=>
    {
        var report=At("startup-result.json");await RunFixture(Path.Combine(published,"NordVault.exe"),["--test-startup",report]);
        using var data=JsonDocument.Parse(await File.ReadAllTextAsync(report));Expect(data.RootElement.GetProperty("Success").GetBoolean()&&!data.RootElement.GetProperty("VaultCreated").GetBoolean(),"Valv krävdes vid programstart.");
    });
    Add("Published GUI stays usable during delayed/failed disk reads and cancels a read on close",async()=>
    {
        var report=At("refresh-result.json");await RunFixture(Path.Combine(published,"NordVault.exe"),["--test-refresh",report]);
        using var data=JsonDocument.Parse(await File.ReadAllTextAsync(report));Expect(data.RootElement.GetProperty("Success").GetBoolean()&&data.RootElement.GetProperty("Cases").GetArrayLength()==3,"GUI-regression för diskstatus misslyckades.");
    });
    Add("Published Setup EXE verifies real Windows shortcuts and preserves user data",async()=>
    {
        using var installerScratch=new PrivateWorkspace(scratch.Path);await RunFixture(Path.Combine(root,"Setup-NordVault-0.4.4.exe"),["--test-install",installerScratch.Path]);
        using var data=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(installerScratch.Path,"result.json")));
        Expect(data.RootElement.GetProperty("Success").GetBoolean()&&data.RootElement.GetProperty("ShortcutsVerified").GetBoolean()&&data.RootElement.GetProperty("DataPreserved").GetBoolean(),"Setup misslyckades med genvägar eller data.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.3.0.zip")))
    Add("Real 0.3.0 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.3.0.json")));
        var folder=At("legacy-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.3.0.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.3.0.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"Äldre installation eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.3.1.zip")))
    Add("Real 0.3.1 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.3.1.json")));
        var folder=At("legacy031-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.3.1.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.3.1.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.3.1 eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.3.2.zip")))
    Add("Real 0.3.2 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.3.2.json")));
        var folder=At("legacy032-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.3.2.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.3.2.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.3.2 eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.4.0.zip")))
    Add("Real 0.4.0 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.4.0.json")));
        var folder=At("legacy040-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.4.0.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.4.0.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.4.0 eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.4.1.zip")))
    Add("Real 0.4.1 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.4.1.json")));
        var folder=At("legacy041-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.4.1.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.4.1.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.4.1 eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.4.2.zip")))
    Add("Real 0.4.2 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.4.2.json")));
        var folder=At("legacy042-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.4.2.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.4.2.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.4.2 eller valv uppdaterades fel.");
    });
    if(File.Exists(Path.Combine(root,"artifacts","legacy-payload-0.4.3.zip")))
    Add("Real 0.4.3 payload updates to 0.4.4 while retaining vault bytes",async()=>
    {
        var oldManifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"src","NordVault.Setup","Resources","legacy-0.4.3.json")));
        var folder=At("legacy043-update");var oldSetup=Path.Combine(root,"Setup-NordVault-0.4.3.exe");
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","legacy-payload-0.4.3.zip")))await new DeploymentService(oldManifest).InstallAsync(payload,folder,"Portable",oldSetup);
        Directory.CreateDirectory(Path.Combine(folder,"Data"));var preserved=Path.Combine(folder,"Data","keep.gpg");await File.WriteAllBytesAsync(preserved,bytes);
        var manifest=DeploymentService.ReadManifest(await File.ReadAllBytesAsync(Path.Combine(root,"artifacts","setup-manifest.json")));
        using(var payload=File.OpenRead(Path.Combine(root,"artifacts","setup-payload.zip")))await new DeploymentService(manifest).UpdateAsync(payload,folder,"Portable",Path.Combine(root,"Setup-NordVault-0.4.4.exe"),oldManifest,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(oldSetup))));
        Expect(DeploymentService.ReadState(folder).Version=="0.4.4"&&(await File.ReadAllBytesAsync(preserved)).SequenceEqual(bytes),"0.4.3 eller valv uppdaterades fel.");
    });
}
var failed=0;
try { foreach(var(name,test) in tests) { try { await test(); Console.WriteLine("PASS "+name); } catch(Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e.GetType().Name+": "+e.Message); } } }
finally { Array.Clear(password); }
Console.WriteLine($"{tests.Count-failed}/{tests.Count} portable vault/archive tests passed. Only test-created files were used.");
return failed==0?0:1;

sealed class AsyncGateStream(Stream inner):Stream
{
    private int _first;
    public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
    public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
    public override int Read(Span<byte> buffer)=>inner.Read(buffer);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellation=default)
    {if(Interlocked.Exchange(ref _first,1)==0){Entered.TrySetResult();await Continue.Task.WaitAsync(cancellation);}return await inner.ReadAsync(buffer,cancellation);}
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken cancellation)=>ReadAsync(buffer.AsMemory(offset,count),cancellation).AsTask();
    public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);
    public override void Flush()=>inner.Flush();public override void SetLength(long length)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
}

sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
