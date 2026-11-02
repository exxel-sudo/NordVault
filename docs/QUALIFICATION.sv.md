# Verifiering av NordVault 0.3.2

## Genomfört

- Originalarkivet är uppackat separat och granskat med kodhänvisningar.
- Lokal .NET-byggning med varningar som fel. Appkoden använder inga externa NuGet-bibliotek; fristående distribution innehåller Microsofts .NET/Windows Desktop-runtime 10.0.11 för win-x64.
- 23 tester av volym- och lösenordspolicy samt verklig skrivning, flush och tillbakaläsning av en **testskapad** återställningsfil. Testerna kontrollerar att befintliga filer aldrig skrivs över.
- 17 isolerade backendscenarier med simulerade Windows-kommandon: fel eller bytt volym-ID, utebliven bekräftelse, overifierad backup, SSD-filförstöring, farliga sökvägar, full kryptering, bevarad recovery efter motorfel, återanvändning av recovery, TPM + PIN och bevarat starttest, okända/felaktiga WMI-statusar, låst volym, upplåsning och Windows flaggor för logisk rensning.
- 25 filintegrationstester med den verkliga lokala GnuPG-motorn, enbart testskapade filer: Unicode-lösenord, exakt binär roundtrip och flera OCB-chunkar, tom fil, olika kryptotext vid upprepning, fel lösenord, manipulation, avklippt autentisering, bevarad original-/resultatfil, låst källa, avbrott under kryptering och under klartextskrivning, samtidig skapning av resultat, extern GnuPG-läsning, paketens S2K-inställningar, avvisad äldre CFB/MDC och AES-128, flera strömmar, osäkra sökvägar, ogiltiga lösenord, ändrad motor, injicerad DLL och generatorns alfabet/längd. Metadata krypteras direkt från stdin och återställs autentiserat i minnet utan klartextmetadatafil.
- Reproducerbar lokal uppackning av GnuPG från signerat paket: ytterpaketets publicerade SHA-256 och signatur, inre paketets SHA-256 och komponenternas fasta manifest kontrolleras. Installationsprogrammet körs inte. Testad även mot redan installerad motor.
- Visuell PNG-kontroll av GUI med simulerade diskar. Demo utför inga diskåtgärder.
- SDelete hämtat från Microsoft och dess Microsoft-signatur verifierad vid installation. Verktyget har inte körts under utvecklingen.
- Läskontroll på utvecklingsdatorn: Windows 11 Enterprise, sex lokala NTFS-volymer och sex NVMe SSD-enheter. Den konservativa matchningen mot lagrings-ID hittar exakt en fysisk enhet per disk. Oprivilegierad GUI-process listar volymer men lämnar krypteringsstatus okänd tills användaren väljer administratörsstart.

Testerna visar att de kontrollerade kodvägarna följer dessa krav. De visar inte att programmet är buggfritt eller fungerar på varje verklig disk.

## Tillkommet i 0.3.0

- 22 valv-/arkiv-/installationstester i det fulla distributionsbygget: skapa, importera, exportera och låsa valvsession; fel huvudlösenord; kopierat valv på annan sökväg; manipulerat objekt; skydd av befintliga mappar; krypterat flerfilsarkiv och dubbla namn; fel arkivlösenord; ändrat paket och avklippt footer; ändrad läsare; ZIP-traversal, dubbletter och symlänkar; ärvda NTFS-behörigheter för portabel kryptotext; ogiltiga installationsmanifest; kontrollerad installation; bevarande av egna och ändrade filer vid avinstallation; ärvd läs-/köråtkomst för alla användare; hashfel, avbrott och skyddade installationsmål.
- Bland dessa finns installation och avinstallation av **hela det publicerade payloadpaketet** i en testskapad mapp. Inget register, ingen startmenygenväg eller riktig användarinstallation ändras av testet.
- Den **verkliga publicerade självuppackande EXE-filen** körs med medföljande .NET-runtime och GnuPG, tom alternativ DOTNET_ROOT, separat runtimecache och PATH utan externa GnuPG-program. Korrekt lösenord ger exakt återställd testfil; fel lösenord ger fel och ingen ny resultatmapp. Detta är ett lokalt körprov, inte prov på en andra fysisk dator.
- Totalt 87 olika automatiska scenarier i distributionsbygget: 23 policy-/recovery, 17 backend, 25 fil och 22 valv-/arkiv-/installation. De första 19 valvscenarierna körs även före paketering.
- Lokal varm hastighetsmätning på 64 MiB slumpad data: 58,2 MiB/s kryptering, 48,4 MiB/s dekryptering, verifierad hash. Intel i7-7820X/16 trådar, Windows 11 Enterprise 26200. Se [PERFORMANCE.sv.md](PERFORMANCE.sv.md) för omfattning och begränsningar.
- Installations- och valvgränssnitt förhandsrenderas, liksom fil- och valvflikarna. Dessa bilder provar layout, inte varje klickflöde.

Utöver tabellen nedan behövs manuella prov av Windows Installerade appar/startmeny, UAC och temporär installations-/avinstallationshjälpare; flytt till annan dator/Windows-konto, NTFS/exFAT och USB; upplåsning/låsning, DPI/tangentbord; strömavbrott under kataloguppdatering och arkivuppackning. Automatisk återställning från `catalog.previous.gpg` ingår inte.

## Ändrat och provat i 0.3.1

Programstart kräver inte längre ett valv. Det öppnas från fliken Mitt valv. Startlösenord är ett uttryckligt val i valvdialogen. Installeraren har ny layout, programikon, skrivbords- och Startmenyval, avinstallationsgenväg, avinstallation i programmappen, samt uppdatering/reparation av den verifierade 0.3.0-versionen och aktuellt 0.3.1-paket.

Distributionsbygget omfattar nu **99 olika scenarier**: 23 policy/recovery, 17 backend, 25 fil och 34 valv/arkiv/installation. 28 av de sistnämnda körs före paketering. Nya prov använder verkliga Windows `.lnk`-filer med Unicode och avinstallationsargument, skyddar andra genvägar, uppdaterar versionsmarkör och programfiler, bevarar Data och egna filer, avvisar ändrade/låsta filer och kolliderande egna filer, hanterar avbrott och låser samtidiga installationer. Ett kontrollerat publiceringsfel återställer den gamla installationen och bevarar en samtidigt skapad egen fil.

Den publicerade appen konstruerar huvudfönstret utan valv och utan att skapa en katalogfil. Den publicerade Setup-EXE:n installerar sitt inbyggda paket, skapar och läser tillbaka riktiga Windows-genvägar och avinstallerar med bevarad testdata i en egen testmapp. Det verkliga bevarade 0.3.0-paketet uppdateras till 0.3.1 i en isolerad mapp med bytejämförelse av bevarad valvdata. Testerna skriver inte till användarens skrivbord, Startmeny eller avinstallationsregister.

Global registerintegration, en annan Windows-användare, UAC, temporär avinstallationshjälpare och hårt ström-/processavbrott återstår att prova manuellt. Uppdateringens programbackup är för felhantering, inte en säkerhetskopia av användarens filer. Automatisk reparation av valvkatalogen ingår fortfarande inte.

## Diskavläsning och användbarhet i 0.3.2

Avläsning av diskstatus är separat från ändrande diskoperationer och spärrar inte adminstart, filkryptering eller valv. Diskknappar väntar på fullständig status och backend verifierar fortfarande identiteten på nytt inför varje ändring. Överlappande avläsningar hindras, nästa automatiska kontroll räknas från avslutad kontroll, och aktuellt besked/tid visas separat. Intervallet är 20 sekunder för admin och 60 sekunder utan admin. Stängning avbryter enbart den skrivskyddade statusprocessen, aldrig en påbörjad ändringsoperation.

Storage-listorna för partitioner, diskar och fysisk medietyp hämtas en gång per Discover-begäran. Snapshots återanvänds inte vid ändringsbegäran. Utan admin hoppas privilegierade BitLocker/TPM-frågor över och säkerhetsstatus behålls som Unknown.

Bygget kör **104 testfall**: 23 policy/recovery, 20 backend, 25 fil och 36 valv/arkiv/installation/GUI. Nya backendfall provar två volymer med en lagringsinventering, uteblivna privilegierade frågor, tvetydig diskidentitet och misslyckad lagringsinventering. Den körbara appens GUI-prov fördröjer en statuskontroll, kontrollerar tillgängliga knappar, hindrar samtidiga läsningar, provar fel och stängning med avbrytning. De bevarade verkliga 0.3.0- och 0.3.1-paketen uppdateras med oförändrade valvbytes i testmappar.

## Krävs före användning med viktiga data

| Prov | Godkännandekrav |
| --- | --- |
| Tom reserv-USB, NTFS och exFAT | Full XTS-AES-256, lösenordsupplåsning efter urkoppling, jämförelse av testfilers hash efter kryptering och återställning. |
| Felaktigt lösenord och giltig recovery | Felaktigt lösenord nekas; sparad nyckel öppnar rätt testvolym. |
| Systemdisk i isolerad VM/reservdator | TPM + PIN, Windows starttest, normal omstart och recovery efter ändrade startförhållanden. |
| Kryptering och strömavbrott | Windows återhämtar konverteringen, testfilernas hash stämmer och GUI visar aktuellt tillstånd. |
| HDD och SSD via USB-brygga | Enhetens identitet och medietyp stämmer; okänd medietyp blockeras för filförstöring. |
| Recovery på annan fysisk enhet | Samma disk/annan partition nekas; rätt separat enhet godkänns; nyckelfilen kan användas efter ny start. |
| Länkar, snapshots, låsta filer och full disk | Osäkra mål avvisas, fel visas utan framgångsbesked och inga oavsiktliga filer berörs. |
| Rensning av ledigt utrymme | Start, status och avbrott provas på reservvolym; fysiska saneringslöften ges inte för SSD. |
| Oberoende säkerhetsgranskning | Privilegier, processgränser, nyckelhantering och fil-/volymidentifiering granskas av extern kompetens. |
| Filkryptering: process-/strömavbrott och full disk | Ingen befintlig fil skrivs över; kvarvarande del-filer identifieras och fel rapporteras korrekt. Klartextrester hanteras på krypterad volym. |
| Filkryptering: stora filer och andra filsystem | 64 GiB-gräns, FAT32:s filstorleksgräns, exFAT och frånkoppling provas på reservmedia med hashjämförelse. |
| Tillgänglighet och vardaglig GUI-användning | Tangentbord, skärmläsare, olika DPI, verkliga dialogflöden och långvariga körningar provas. PNG-kontroll ensam styrker inte detta. |

## Kända begränsningar

GUI och PowerShell anropar vissa Windows-funktioner med enhetsbokstav och SDelete med filnamn. Identiteten kontrolleras precis före anrop, men Windows kan fortfarande ändra enhetsmappning eller en annan process kan byta en fil mellan kontroll och verktygets öppning. Detta är ingen fullständigt handtagsbunden implementation. Koppla inte ur eller byt enheter under operationer, och använd inte programmet i en komprometterad session.

Recovery-exporten verifierar en lyckad skrivning, inte att en backup alltid finns kvar. Filen kan senare raderas eller mediet gå sönder. Användarens uttryckliga bekräftelse och separat offlineförvaring är del av återställningsflödet.

Programmet är inte Authenticode-signerat. Windows kan visa en varning för ett lokalt eget bygge. Krypteringsmotorn och SDelete har separata leverantörers egenskaper och licenser. Den privata förhandsversionen har ingen jämförande mätning mot Symantec PGP Whole Disk Encryption.

GnuPG för Windows har även en längdgräns för agentsökvägar. Extremt långa användar-/temporärsökvägar kan förhindra start. Avbrott under normal körning är testade; hårda processkrascher, nätbortfall och fysiskt strömavbrott är ännu inte provade. Filtesterna bevisar inte AES/OCB-implementationen och ersätter inte leverantörens eller en extern granskares analyser.
