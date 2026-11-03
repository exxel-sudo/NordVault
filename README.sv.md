# NordVault 0.4.4 — privat förhandsversion för Windows 11 x64

Svenskt GUI för diskar, USB, lösenordskrypterade filer, ett krypterat personligt valv och självuppackande arkiv. BitLocker sköter volymer och GnuPG 2.5.24 sköter filer. Den historiska PGP-koden ligger separat i `legacy/pgp263i` och används inte som kryptomotor.

**Installationsbar testversion. Oberoende säkerhetsrevision, prov på en annan dator och verkliga disk-/återställningstester återstår. Det finns inget underlag för att kalla den buggfri, oknäckbar eller bättre än Symantec.**

## Installera eller ta med på USB

Dubbelklicka **Setup-NordVault-0.4.4.exe** i projektets rotmapp. Välj:

- **Installera på datorn:** standardmappen är `%LOCALAPPDATA%\Programs\NordVault`. Du kan välja annan mapp. En vanlig installation för ditt konto behöver ingen administratör. Skrivbordsgenväg och Startmeny är förvalda, och kan väljas bort. Startmenyn får både NordVault och **Avinstallera NordVault**. Windows Installerade appar får en avinstallationspost.
- **Installera för alla användare:** standardmappen blir Program Files och Windows begär administratör via UAC.
- **Packa upp portabel version:** välj en tom mapp på exempelvis USB. Inga registerposter eller genvägar skapas.

Du kan också packa upp `release/NordVault-Portable-0.4.4.zip` till en tom mapp och starta `NordVault.exe`. Kopiera **hela** mappen, inklusive verktyg och senare skapad `Data`, när du flyttar den. `Starta-NordVault.cmd` i projektet öppnar versionen i `release/NordVault-Portable`.

.NET-runtime och filmotorn medföljer. Windows x64 krävs; detta är ingen Linux-, Mac- eller ARM-version. Programfilerna är vanliga körbara filer. Dina valvfiler, nyckelfiler och deras namn krypteras med huvudlösenordet; programkoden behöver inte vara hemlig för detta skydd. Appen och installern är **inte Authenticode-signerade**. SHA-256-kontrollsummorna identifierar ett bygge men bevisar inte dess säkerhet eller ursprung på egen hand.

Installerat standardvalv: `%LOCALAPPDATA%\NordVault\Data\Vault`, separat från programfilerna. Portabelt standardvalv: `Data\Vault` bredvid appen. Du kan välja annan valvmapp. För att flytta från installerat läge måste även valvmappen kopieras och väljas vid upplåsning på nästa dator.

Avinstallera via Windows **Installerade appar**, Startmenyns **NordVault → Avinstallera NordVault**, eller **Avinstallera NordVault.cmd** i programmappen. Setup visar även **Avinstallera befintlig installation…** när en installation upptäcks. För en portabel mapp skapad med Setup kan dess avinstallerare också köras:

```powershell
.\NordVault.Setup.exe --uninstall 'E:\sökväg\NordVault'
```

Avinstallation tar endast bort kända programfiler med rätt kontrollsummor. Egna filer, ändrade programfiler och `Data` bevaras. ZIP-versionen har ingen installationspost; programfiler kan tas bort manuellt efter att valvet kopierats säkert.

**Uppdatera från 0.3.0, 0.3.1, 0.3.2, 0.4.0, 0.4.1, 0.4.2 eller 0.4.3:** stäng först NordVault. Kör Setup 0.4.4 och välj samma installationsmapp och läge. De verifierade lokala versionerna upptäcks och knappen visar **Uppdatera till 0.4.3**. Kända programfiler byts, valv och egna filer behålls, och genvägar skapas enligt valen. Ändrade programfiler eller egna filer som kolliderar med nya programnamn stoppar uppdateringen. Samma 0.4.3-paket kan reparera installationen och genvägarna; ett okänt äldre bygge behöver sin egen avinstallerare eller en ny mapp. Normal felhantering återställer gamla programfiler. Ett process-/strömavbrott under bytet behöver manuell kontroll; eventuell programbackup ligger i en `NV-*`-arbetsmapp bredvid installationen.

## Huvudlösenord och mitt valv

**Programmet öppnas direkt. Inget valv krävs för att använda diskar, USB, filkryptering eller arkiv.** Om du vill ha ett personligt valv väljer du **Mitt valv → Skapa eller öppna valv…**. Välj en tom valvmapp, ange och upprepa minst 16 tecken eller använd generatorn med 24 slumpade tecken. Spara lösenordet säkert. Ett förlorat huvudlösenord kan inte återställas.

Valvet är **en mapp med krypterade filer**, inte en virtuell disk med reserverad storlek. Ett tomt valv har bara små metadatafiler. Det växer med de filer du importerar och en liten krypteringsoverhead; högst 64 GiB per fil och 5 000 objekt, även begränsat av ledigt utrymme. Du behöver inte ange någon valvstorlek.

Innehållsförteckningen verifieras när valvet låses upp. **Mitt valv** importerar filer och nyckelfiler och sparar dekrypterade kopior utanför valvet. Originalet behålls vid import. Exporten är avsiktligt klartext. Nyckelfiler lagras som krypterade filer; offentlig nyckelring och nyckelbaserad filkryptering ingår inte ännu. Befintliga valv från 0.3.0 använder samma format.

Filnamn och innehåll krypteras; antal objekt och ungefärliga storlekar kan fortfarande ses. Varje objekt binds med SHA-256 till den autentiserade innehållsförteckningen. Ingen DPAPI-bindning till ursprungsdatorn används. Stäng eller lås appen innan du kopierar hela valvet, inklusive katalog och `entries`. På NTFS kan mappens Windows-behörigheter ändå kräva åtgärd på nästa dator; exFAT saknar dessa behörigheter.

**Lås valvet** stänger sessionen och döljer filnamnen. Diskar och filkryptering är kvar. Valvet låses även efter tio minuters inaktivitet när ingen åtgärd eller dialog pågår. Vill du också skydda programstarten, välj **Kräv valvets lösenord när NordVault startas** i valvdialogen. Då öppnas appen efter valvets lösenord och knappen heter **Lås appen**. Detta är valfritt och avslaget som standard, även för en gammal installation utan denna inställning. Inställningen kan stängas av när valvet öppnas igen. Valvfilerna förblir krypterade oavsett startvalet. Hemligheter finns i minnet under upplåst session. Applås låser inte automatiskt öppna BitLocker-volymer eller tar bort tidigare klartextexporter.

## Lösenord, länkar, anteckningar och kopplade konton

Fliken **Lösenord & länkar** kräver ett upplåst valv. Där sparar du:

- **Inloggning:** webbplats, användarnamn och lösenord. **Generera starkt lösenord** skapar 20 slumpade tecken.
- **Länk:** titel och webbadress, som ett krypterat bokmärke.
- **Anteckning:** fri text, till exempel koder eller instruktioner.
- **Kopplat konto:** en webbplats där du loggar in via ett annat konto, till exempel Google-kontot eller BankID, med valfritt användarnamn och lösenord.

Allt lagras krypterat i `items.gpg` i valvmappen med samma huvudlösenord och samma GnuPG-format som valvets innehållsförteckning. Föregående version sparas som `items.previous.gpg`. Filen är bunden till valvets id och kan inte flyttas till ett annat valv. Äldre NordVault-versioner ignorerar filen; dina filer i valvet påverkas inte.

**Lösenord visas aldrig i listan.** Att visa eller kopiera ett sparat lösenord kräver att du anger huvudlösenordet igen, även när valvet redan är upplåst. Efter tre fel låses valvet. Lösenordsrutan stängs efter 30 sekunder och döljs vid skärmdelning och skärmdumpar där Windows stöder det. Ett kopierat lösenord rensas från urklipp efter 30 sekunder och markeras så att det inte hamnar i Windows urklippshistorik eller molnurklipp. Medan valvet är öppet hålls varje lösenord krypterat i minnet med en slumpad sessionsnyckel och dekrypteras bara precis när det behövs.

### Privat

Knappen **🔒 Privat…** öppnar en egen avdelning för privata länkar med anteckningar. Den kräver huvudlösenordet eller Windows Hello varje gång, även när valvet är upplåst. Efter tre fel låses valvet.

- **+ Länk** och **+ Många länkar** (klistra in många på en gång) finns här, inte i huvudvyn. **Hämta från vanliga listan…** flyttar valda befintliga länkar hit, och **Flytta till vanliga listan** flyttar tillbaka.
- Privata länkar syns aldrig i huvudlistan eller i Firefox-knappens sökning. De ligger i samma krypterade `items.gpg`.
- **Öppna i privat fönster** (eller dubbelklick) öppnar länken i Firefox privat fönster, Chrome inkognito eller Edge InPrivate, enligt ditt val. Valet sparas. Ett privat fönster sparar ingen historik i webbläsaren, men nätverket och webbplatsen ser fortfarande besöket.
- Fönstret döljs vid skärmdumpar och skärmdelning och stängs när valvet låses.

### Importera lösenord och bokmärken från Firefox

1. **Lösenord:** skriv `about:logins` i Firefox → menyn ••• → **Exportera lösenord…** → spara CSV-filen. Gör det helst till en BitLocker-krypterad disk eller ett USB-minne som du formaterar efteråt: filen innehåller alla lösenord i klartext.
2. **Bokmärken:** Ctrl+Skift+O → **Importera och säkerhetskopiera** → **Exportera bokmärken till HTML…** (JSON-säkerhetskopia fungerar också).
3. NordVault: **Lösenord & länkar → Importera från Firefox…** → välj filerna → **Importera krypterat**.

Allt sparas i ett enda krypterat steg. Samma webbplats och användarnamn uppdateras, och befintliga länkar dubbleras inte. Bokmärkesmappen sparas i länkens anteckning så att du kan söka på den. CSV-filen raderas direkt efteråt (förvalt), inte via papperskorgen. På SSD och USB-minnen kan rester ligga kvar trots radering, och överskrivning hjälper inte där.

**Säker import:** Alla importerade inloggningar markeras **Att byta**, eftersom allt som låg i Firefox ska räknas som möjligen läckt. **Visa listan Att byta** sorterar dem viktigast först: e-post, Microsoft/Apple/Google-konton, bank/betalning, domän/DNS/hosting, Google Ads, Anthropic/Claude och övrigt. Markeringen försvinner först när du sparar ett **nytt, annat** lösenord för posten, i NordVault eller när Firefox-tillägget sparar ett ändrat lösenord. Bokmärken vars adress innehåller hemliga koder (till exempel `token=`, `key=`, `password=`, `session`, `sig=`, `code=` eller återställningslänkar) visas med varning, och koderna tas bort ur adressen som förval. Efter importen visar NordVault hur du tar bort lösenorden ur Firefox och stänger av Firefox fråga om att spara inloggningar.

## Firefox: spara och fylla i lösenord

Tillägget gör att Firefox frågar **Spara lösenordet i NordVault?** när du loggar in på en webbplats, och att du kan fylla i sparade inloggningar. Du behöver då inte Firefox egen lösenordshanterare eller Firefox-konto.

1. Öppna **Lösenord & länkar → Koppla Firefox…** och klicka **Koppla**. NordVault registreras för Firefox för ditt Windows-konto (`HKCU\Software\Mozilla\NativeMessagingHosts\se.nordvault.firefox`). Ingen administratör krävs.
2. Öppna `about:debugging#/runtime/this-firefox` i Firefox och välj **Läs in tillfälligt tillägg…**.
3. Välj `manifest.json` i mappen `browser\firefox` i NordVault-mappen (**Visa tilläggsmappen** öppnar den).

Ett tillfälligt tillägg försvinner när Firefox startas om och läses då in igen på samma sätt. För permanent installation i vanliga Firefox måste tillägget signeras av Mozilla; det kan göras gratis som olistat tillägg på addons.mozilla.org. Stäng gärna av Firefox egen fråga under **Inställningar → Integritet och säkerhet → Fråga om att spara inloggningar**.

**Spara:** När du loggar in visas en ruta nere till höger på sidan: **Spara**, **Inte nu** eller **Aldrig för denna webbplats**. Rutan stängs med **✕** och försvinner efter 20 sekunder (som Inte nu), så att den aldrig skymmer till exempel Logga ut. Samma webbplats och användarnamn uppdateras i stället för att dubbleras. Inloggningar i två steg (först användarnamn, sedan lösenord på nästa sida) sparas med båda delarna: tillägget minns användarnamnet från första steget i högst 5 minuter, bara för samma flik och webbplats och bara i minnet.

**Spara när valvet är låst (brevlådan):** Du behöver inte låsa upp valvet för att spara. Inloggningen krypteras direkt av GnuPG till valvets offentliga brevlådenyckel (RSA-3072, AES-256/OCB) och läggs i mappen `inbox` i valvet. Bara valvet kan öppna den. Nästa gång du låser upp visar NordVault de nya inloggningarna och frågar **Ja** (lägg in), **Nej** (ta bort) eller **Avbryt** (fråga nästa gång). Brevlådan skapas första gången du låser upp valvet med 0.4.4; dessförinnan ber NordVault om huvudlösenordet som tidigare.

**Fylla i:** På en inloggningssida med sparad inloggning visas **Fyll i**, och samma lista finns under NordVault-ikonen i verktygsfältet. NordVault visar då en egen ruta där du bekräftar varje ifyllning (Nej är förvalt). Lösenordet skickas bara till huvudsidan med exakt samma ursprung. En https-inloggning erbjuds aldrig till en http-sida; underdomäner till den sparade webbplatsen godtas.

**Skydd i tillägget:**

- Bara krypterade https-sidor används. Okrypterat http godtas endast för den egna datorn (localhost).
- Tilläggets knapp i verktygsfältet har en sökruta för dina länkar och bokmärken. Den visar högst 15 träffar per sökning, bara när valvet är upplåst, och öppnar vald länk i en ny flik. Bokmärkena kan därmed ligga enbart i valvet.
- NordVault begränsar hur ofta Firefox får fråga: högst 6 ifyllningar och 60 sökningar per minut.
- Vid varje start kontrolleras att Firefox-kopplingen i registret pekar på just den här NordVault.exe och bara tillåter NordVault-tillägget. Har något annat program ändrat den visas en varning med val att återställa eller ta bort kopplingen.
- Är Windows Hello påslaget bekräftas varje ifyllning med Windows Hello i stället för en Ja/Nej-ruta.

Om NordVault inte är igång startas programmet när du väljer Spara eller Fyll i. Att bara öppna en sida startar aldrig NordVault. Firefox når bara ett NordVault som körs utan administratörsläge; stäng administratörsfönstret efter diskarbete.

## Valvnyckel, återställningskoder och Windows Hello

Från 0.4.0 krypteras valvets innehåll med en **valvnyckel**. Nyckeln ligger i kapslar i mappen `keys` i valvet, en kapsel per sätt att låsa upp:

- `password.gpg` – huvudlösenordet.
- `recovery-*.gpg` – en per återställningskod.
- `hello-*.json` – Windows Hello, en per dator och Windows-konto.

Kapslarna för huvudlösenord och koder använder samma GnuPG-format (AES-256/OCB, iterad SHA-512) som resten av valvet.

- **Nya valv** får en slumpad valvnyckel på 256 bitar. Sådana valv kan inte öppnas av NordVault 0.3.x.
- **Valv från 0.3.x** behåller sitt huvudlösenord som valvnyckel. Vid första upplåsningen läggs bara en kapsel till; inga filer krypteras om, och äldre versioner kan fortfarande öppna valvet med det ursprungliga lösenordet.
- **Byt huvudlösenord** (Mitt valv → Säkerhet) kräver det nuvarande lösenordet. Valvets filer krypteras inte om. En kopia av valvmappen som någon redan har kan alltså fortfarande öppnas med det gamla lösenordet. Byt därför också lösenord på valv som du misstänker har kopierats, och flytta innehållet till ett nytt valv om du behöver full säkerhet.

**Återställningskoder:** Mitt valv → Säkerhet → **Återställningskoder…** skapar 5 koder på 40 tecken (8 grupper om 5, cirka 200 bitar slump). NordVault erbjuder detta direkt när ett nytt valv skapas. Koderna visas en enda gång, kan skrivas ut och sparas aldrig i klartext. Förvara dem på papper utanför datorn, inte i fil, mejl, moln eller i valvet. Har du glömt huvudlösenordet väljer du **Glömt huvudlösenordet? Använd återställningskod…** vid upplåsning, anger en kod och väljer ett nytt huvudlösenord. Koden är sedan förbrukad. Nya koder ersätter alla gamla. Sms- eller app-koder (TOTP) används inte för upplåsning: hemligheten skulle då ligga på samma dator och ge inget extra skydd.

**Windows Hello:** Mitt valv → Säkerhet → **Windows Hello…** låter dig låsa upp valvet och godkänna visa, kopiera och fyll i med fingeravtryck, ansikte eller Windows-PIN. Det kräver huvudlösenordet en gång när du slår på det.

- Windows skapar en nyckel som inte kan exporteras och som hör till ditt Windows-konto på den här datorn (i säkerhetschipet TPM där det finns). Windows visar alltid sin egen ruta, så ett annat program kan inte godkänna åt dig.
- Efter omstart av datorn krävs huvudlösenordet första gången.
- Byte av huvudlösenord stänger av Windows Hello.
- Huvudlösenordet fungerar alltid och behövs på en annan dator.
- Windows Hello fungerar normalt inte via fjärrskrivbord.

## Låsning, lösenordshälsa och läckkontroll

**Låsning:** Valvet låses direkt när Windows låses (Win+L), när du byter användare eller loggar ut, när fjärrsessionen kopplas från, när datorn går i vila och när skärmsläckaren startar. Fönster som visar eller tar emot lösenord stängs. Pågår en åtgärd låses valvet när den är klar. Autolås efter 2, 5 eller 10 minuter utan aktivitet väljs under Mitt valv.

**Lösenordshälsa:** Listan **Att byta** visar även lösenord som används på flera poster och lösenord som är korta, har för få teckentyper eller innehåller vanliga ord eller användarnamnet. Allt räknas ut på datorn. Återanvändning jämförs med HMAC under en slumpad sessionsnyckel, och resultatet sparas inte.

**Läckkontroll:** Knappen **Läckkontroll…** kontrollerar sparade lösenord mot Have I Been Pwned (api.pwnedpasswords.com), bara när du klickar. För varje lösenord skickas endast de 5 första tecknen av dess SHA-1-kontrollsumma. Svaret är utfyllt och jämförs på datorn. Tjänsten ser din IP-adress men aldrig lösenord, användarnamn eller webbplatser. Läckta lösenord hamnar först i Att byta tills de byts.

## Filkryptering och självuppackande arkiv

I **Filkryptering** väljer du en fil, anger och upprepar minst 16 tecken och sparar en ny `.gpg`-fil. För dekryptering väljer du `.gpg`, dess lösenord och en ny resultatfil. Original och befintliga resultat skrivs inte över. `.gpg` kräver NordVault eller GnuPG som stöder det använda AES-256/OCB-formatet; alla OpenPGP-program gör inte det.

**Skapa självuppackande arkiv…** använder lösenordet i filfliken, låter dig välja flera filer och sparar en ny `.exe`. Den innehåller läsare, .NET-runtime och GnuPG. På annan **Windows x64-dator** kan arkivet öppnas med lösenord utan NordVault-installation. Filer packas upp i en ny undermapp under vald plats och körs aldrig automatiskt. Dubbla filnamn får särskilda namn. Innehållsförteckning och filer krypteras; läsarens körbara kod är offentlig. Läsaren tar cirka 65 MiB även för ett litet arkiv. Windows eller organisationspolicy kan blockera osignerade EXE-filer. Kör betrodda arkiv och skicka lösenordet separat.

Fil/valv/arkiv kräver ingen administratör. Gränsen är 64 GiB per fil och 5 000 objekt per valv/arkiv. Vanliga lokala filer och USB stöds; nätverksvägar, symlänkar och moln-reparsepunkter avvisas. Mappar kan packas i självuppackande arkiv (via högerklick) och återskapas med undermappar; tomma mappar, extra NTFS-dataströmmar och fullständig metadata paketeras inte. Valv-/arkivnamn har högst 200 tecken; fullständig sökväg högst 240 tecken. Utrymme behövs för krypterade arbetskopior och uppackning.

**Högerklick i Utforskaren:** högerklicka på en mapp eller fil → **NordVault → Skapa självuppackande arkiv…**. I Windows 11 ligger NordVault under **Visa fler alternativ** (eller Shift+högerklick). Ange arkivets lösenord två gånger (minst 16 tecken). Arkivet sparas bredvid som `Namn - krypterat.exe` (går att ändra). Inget valv behöver vara upplåst; arkivet har sitt eget lösenord. Originalen ligger kvar. Menyn läggs till för din Windows-användare när NordVault installeras eller startas och tas bort vid avinstallation.

Dekryptering verifierar först hela filen, sedan skriver en andra genomgång klartext. Normal avbrytning städar tillfälliga filer, men process-/strömavbrott kan lämna klartext i resultatmappen eller uppackningens `NV-*`-arbetsmapp. Spara klartext på krypterad volym. .NET kan lämna offentliga runtimefiler i `%TEMP%\.net`; dessa innehåller inte valvets filer eller lösenord. Fil/valv/arkiv har ingen lösenordsåterställning.

## Diskar och USB med lösenord

1. Klicka **Administratör** för diskhantering. Windows UAC startar ny process; valvet behöver låsas upp igen.
2. Välj en volym och **Förbered kryptering…**. Data/USB använder minst 14 tecken eller generatorn. Systemdisk använder TPM och PIN med 6–20 siffror.
3. Bekräfta enhetsbokstaven. Spara återställningsnyckeln på annan fysisk enhet. Programmet skriver, läser tillbaka och kontrollerar placeringen.
4. Bekräfta backupen och starta helvolymskryptering. Systemdiskens Windows-starttest behålls; starta om själv om Windows begär det.

Windows 11 Pro, Enterprise eller Education med fungerande BitLocker krävs för diskfunktionerna. Policy kan begränsa dem. Systemdisk behöver redo TPM; data/USB behöver inte TPM. Lösenord eller recovery kan öppna låsta datavolymer. Låsning gör ingen tvångsavmontering. Avstängt nyckelskydd kan återaktiveras.

Hela valda volymen krypteras med XTS-AES-256, inklusive ledigt utrymme. Andra partitioner, EFI/startpartitioner och SSD-reservceller ingår inte som vanliga datavolymer. NTFS, FAT16/FAT32 och exFAT stöds för USB. Stabil volymidentitet kontrolleras inför åtgärder. Koppla inte ur eller byt enhet under arbetet. Diskstatus läses i bakgrunden utan att spärra filkryptering, valv eller adminstart. Nästa kontroll startar 20 sekunder efter avslutad kontroll som admin, eller 60 sekunder utan admin. Utan admin visas BitLocker-status som okänd. Windows-kryptering fortsätter när GUI stängs.

## Rensning

**Rensning** kan begära och avbryta Windows rensning av ledigt utrymme på helt krypterad volym med aktivt skydd. Befintliga filer behålls.

Filförstöring använder medföljande Microsoft SDelete efter licensgodkännande och uttrycklig bekräftelse. En exakt vanlig fil på identifierad HDD med NTFS krävs. Kataloger, jokertecken, hårda länkar och reparsepunkter avvisas. Verktyget har inte körts på användarens filer under utvecklingen.

**Rensa ledigt utrymme utan BitLocker:** högerklicka på en USB-disk eller annan disk i Utforskaren → **NordVault → Rensa ledigt utrymme…**. Välj **1 pass** (slumpdata, rekommenderas för USB och SSD), **3 pass** (00, FF, slump) eller **7 pass** (DoD 5220.22-M ECE, bara för gamla snurrande hårddiskar). NordVault fyller det lediga utrymmet med utfyllnadsfiler i mappen `NordVault-rensning-…`, skriver över dem för varje pass och tar sedan bort dem. Befintliga filer öppnas aldrig. Disken är full under tiden; ta inte ut den. Avbryt tar bort utfyllnadsfilerna. Blir körningen avbruten av strömavbrott eller utdragen USB tas en kvarlämnad `NordVault-rensning-…`-mapp bort vid nästa rensning. Windows-disken (C:) fylls aldrig; använd fliken Rensning för den. Ingen administratör krävs. Datorn hålls vaken medan rensningen pågår.

SSD/USB-flash har reservceller och omallokering: överskrivning garanterar inte borttagning av alla fysiska kopior. Filförstöring är därför spärrad på SSD och okänd medietyp. Logisk rensning av ledigt utrymme finns med denna begränsning. Firmware-sanitize, snapshots, molnkopior och säkerhetskopior ingår inte.

## Hastighet, tester och säkerhet

Den 5 oktober 2026 mättes en 64 MiB slumpad testfil på denna dator: Intel Core i7-7820X, 8 kärnor/16 trådar, cirka 64 GiB RAM, Windows 11 Enterprise build 26200 och lokal E:-lagring. En varm körning gav **58,2 MiB/s kryptering** och **48,4 MiB/s dekryptering**, med exakt hashjämförelse. Liten metadata tog cirka **0,59 sekunder** att kryptera. Motorstart, integritetskontroll, lösenordshärledning, flush och två dekrypteringsgenomgångar ingår. Detta är inget rådisk-, USB- eller BitLocker-test och garanterar ingen hastighet på annan dator.

Appen byggs för Windows 11 x64 med WPF/.NET 10, strömmad filbehandling och avbrytbara filoperationer. Processor, lagring, filstorlek och antal filer påverkar hastigheten. Lösenordshärledning och extra autentiseringskontroll kostar tid avsiktligt. Ingen jämförande Symantec-mätning eller fullständig Windows-optimeringscertifiering har gjorts.

Se `docs/QUALIFICATION.sv.md`, `docs/SECURITY.sv.md` och `docs/CRYPTO-REVIEW.sv.md` för tester, säkerhetsmodell och intern matematisk analys. Ingen extern matematiker eller oberoende granskare har utfärdat säkerhetsintyg. Starka algoritmer ersätter inte ett säkert lösenord, betrodd dator och provad återställning.

## Bygga från källkod

.NET SDK 10 och Windows krävs. Inga externa NuGet-bibliotek används i appkoden. Distributionsbygget återställer Microsofts runtimepaket via `NuGet.Portable.Config`. `scripts/Install-GnuPG.ps1` verifierar ett fast Gpg4win-paket och packar ut valda komponenter utan att köra installeraren. Licenser och ursprung finns i `tools/gnupg`.

```powershell
pwsh -NoProfile -File .\scripts\Build-Distribution.ps1
dotnet run --project .\tests\NordVault.Benchmark --configuration Release
```

Bygget kör policytester, simulerad backend, verkliga GnuPG-filtester, valv-/arkiv-/installationstester och den publicerade självuppackande läsaren med testdata. Setup i rotmappen, portabel ZIP och kontrollsummor skapas. Inga riktiga diskar krypteras eller rensas. `scripts/Build.ps1` skapar utvecklingsbygget i `dist`, som behöver separat .NET Desktop Runtime.

Förhandsvisning utan diskåtgärder: `release\NordVault-Portable\NordVault.exe --demo`. Källor och historiska studier finns i `docs/SOURCES.sv.md` och `docs/LEGACY-REVIEW.sv.md`. NordVault är en egen app utan anslutning till PGP Corporation, Symantec eller Broadcom.

## Licens

Copyright (C) 2026 exxel-sudo. NordVault är fri programvara under **GNU General Public License v3.0** (se `LICENSE`): du får använda, granska, ändra och sprida den, men ändrade versioner som sprids måste också ha öppen källkod under GPL-3.0. Programmet levereras utan garantier. Medföljande GnuPG har egen GPL-licens. Microsoft SDelete omfattas inte av denna licens och laddas ner separat med `scripts/Install-SDelete.ps1`.
