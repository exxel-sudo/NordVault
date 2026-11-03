# Säkerhetsmodell och begränsningar

NordVault är en privat förhandsversion av ett Windows-gränssnitt till BitLocker och GnuPG, med separat filradering via Microsoft SDelete. Programmet är inte en ombyggd Symantec-produkt och använder inte kryptografin från PGP 2.6.3i. Dokumentet beskriver avsedd användning, synliga skydd och kvarvarande risker. Det är inte ett säkerhetsintyg.

## Vad skyddet avser

Målet är konfidentialitet för data som ligger på en låst krypterad volym när en dator, disk eller USB-enhet tappas bort eller stjäls. Systemdisken använder Windows egna start- och BitLocker-flöden med TPM och PIN; data- och USB-volymer använder lösenord och återställningsmaterial. NordVault begär XTS-AES-256 och kryptering av hela volymen, i stället för enbart använt utrymme. Dessa alternativ finns i [Microsofts dokumentation av Enable-BitLocker](https://learn.microsoft.com/en-us/powershell/module/bitlocker/enable-bitlocker?view=windowsserver2025-ps).

En upplåst volym går att läsa av program som användaren har behörighet att köra. Windows filbehörigheter och ett fungerande operativsystem är fortfarande en del av skyddet. En låst Windows-skärm är inte samma sak som en låst datavolym eller en avstängd dator. Förlust av en dator i viloläge har andra risker än förlust av en avstängd dator; Microsoft beskriver exempelvis att okrypterad data kan finnas kvar i RAM under sömnläge. [BitLocker FAQ](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/faq).

Den här versionen ger inget skyddslöfte mot en illvillig lokal administratör, en redan komprometterad Windows-installation, tangentbordsavlyssning, läsning av upplåsta data, minnesdumpning eller manipulerad firmware. Administratorn kan påverka program, script, motor och skyddskonfiguration. NordVault är beroende av Windows, BitLocker, TPM, firmware och de verktyg som faktiskt körs. Varken färgen i gränssnittet eller vald algoritm är ett bevis på att hela den kedjan är säker.

## Volym är inte samma sak som fysisk disk

En fysisk disk kan innehålla flera volymer. NordVault arbetar med valda BitLocker-volymer. Systemets startpartition och andra okrypterade volymer blir inte skyddade bara för att C: krypteras. Kryptering av hela volymen avser de logiska sektorer som Windows kan adressera där; det är inte ett intyg om sanering av SSD-reservceller, tidigare omallokerade sektorer eller enhetens interna firmwaredata.

XTS-kryptering ska inte beskrivas som ett allmänt integritetsskydd för filinnehåll. Produkten lovar inte att upptäcka varje ändring av lagrad kryptotext och ersätter inte säkerhetskopior eller filsignaturer.

## Lösenordskryptering av enskilda filer

Filmodulen använder den separat levererade GnuPG 2.5.24-motorn. Ett fast Gpg4win 5.1.1-paket verifieras mot tillverkarens SHA-256 och ett bestämt Authenticode-certifikat före uppackning. Före varje operation kontrollerar appen SHA-256 för alla 13 körbara komponenter och DLL:er mot ett inbyggt manifest; okända filer eller undermappar i `bin` avvisas. Detta upptäcker ändrade motorfiler men skyddar inte mot en angripare som kan ändra själva NordVault eller byta filer mellan kontroll och körning. Ursprung och licenser medföljer filmotorn.

GnuPG begärs använda AES-256 med OCB, S2K typ 3, SHA-512 och räknevärdet 65 011 712, utan komprimering. Appen kräver motorns strukturerade AES-256/OCB-status, rätt avslutsstatus och slutförd operation. Äldre CFB/MDC, AES-128 och flera krypteringsströmmar godkänns inte av denna version. Det begränsar kompatibilitet; alla OpenPGP-program stöder inte GnuPG:s AEAD-variant. Matematiska antaganden och S2K:s begränsningar beskrivs i [CRYPTO-REVIEW.sv.md](CRYPTO-REVIEW.sv.md).

En unik privat agentmapp skapas för varje körning, med åtkomst för aktuell Windows-användare. Användarens vanliga GnuPG-nyckelring eller alternativ läses inte; automatisk nyckelhämtning är avstängd. Lösenordet skickas endast till stdin, inte i processargument. Lösenordscachning stängs av och den privata agenten stoppas vid städning. Explicita byte- och teckenbuffertar nollställs när de inte längre behövs. GUI, GnuPG, .NET och operativsystemet kan ändå ha andra minneskopior; visning och kopiering av lösenord är användarens val och ger inga löften om minnessäkerhet.

Källfilen hålls öppen med en delningspolicy som blockerar skrivning och radering. Vanliga lokala sökvägar kontrolleras, återkopplingspunkter avvisas och öppnade filhandtags verkliga sökvägar kontrolleras. Resultatet skapas exklusivt som en unik del-fil och publiceras med flytt utan överskrivning. Originalet behålls. Endast filens vanliga innehåll krypteras; extra NTFS-dataströmmar, behörigheter och fullständiga metadata kopieras inte. Det finns ingen filåterställningsnyckel.

Dekryptering verifierar först hela låsta källfilen med klartext riktad till en null-sink. Först efter lyckad verifiering börjar en andra genomgång som skriver klartext till del-filen. Detta undviker lagrad klartext från fel lösenord eller manipulerat krypterat innehåll. Ett strömavbrott, processkrasch eller misslyckad borttagning under den andra genomgången kan lämna klartextdelar. Normal avbrytning raderar del-filen; misslyckad städning visas uttryckligen. Vanlig filradering sanerar inte SSD-celler. NTFS-resultat har användarens privata ACL; FAT/exFAT saknar den kontrollen. Dekrypterad data bör sparas på en krypterad volym.

Appen begränsar faktisk filutdata till 64 GiB, eftersom det paketerade 32-bitars GnuPG-programmets `--max-output`-parser inte kan representera gränsen. Filnamn, ungefärlig filstorlek, tillgång till klartextoriginalet och separata kopior skyddas inte genom att skapa en krypterad kopia. Nätverksfiler och moln-reparsepunkter stöds inte.

## Krypterat valv och applås i 0.3.0

Från 0.3.1 öppnas huvudfönstret direkt. Valvet är valfritt och dess autentiserat krypterade katalog öppnas först efter lösenord. Användaren kan uttryckligen välja att kräva valvlösenord vid programstart; den preferensen är ingen kryptografisk åtkomstkontroll för programkoden. Valvdata är skyddade av krypteringen även om appen öppnas utan valv. Huvudlösenordet lagras som en rensbar teckenbuffert under sessionen. Det skrivs inte i inställningar och är inte bundet med DPAPI till datorn. Metadata krypteras direkt från minne via stdin och dekrypteras till en begränsad, rensbar minnesbuffert. Tolkat JSON och GUI-namn kan ändå lämna hanterade minneskopior.

Valvfiler får slumpade ID-namn. Katalogen binder namn och storlek till SHA-256 av varje krypterat objekt; export verifierar denna bindning innan dekryptering. En exklusiv sessionsfil hindrar två normala skrivande sessioner samtidigt. Ny katalog skrivs krypterat och byter sedan plats med aktuell katalog; föregående krypterad katalog behålls som `catalog.previous.gpg`. Ett strömavbrott kan ge ofullständig uppdatering eller föräldralöst krypterat objekt. Automatisk reparation och verifierad återställning efter strömavbrott ingår inte ännu.

Innehållsfilerna för portabelt bruk ärver målmappens ACL på NTFS i stället för att få en explicit bindning till skaparkontots SID. Mappens egna behörigheter kan ändå hindra en annan dator/användare. Endast krypterade objekt får denna behandling; klartextexporter skyddas fortfarande av privata NTFS-behörigheter där det är möjligt. exFAT saknar dessa ACL-regler.

Manuell låsning och tio minuters inaktivitet stänger sessionen när ingen åtgärd eller dialog pågår. Detta är ett applås, inte en BitLocker-låsning eller garanti om att alla RAM-kopior försvinner. Demo och förhandsrendering kan öppnas utan valv men visar enbart simulerade data och utför inga dataåtgärder. Programkoden är inte krypterad; krypteringen skyddar innehållet även om koden är tillgänglig. Ingen dold återställningsväg för huvudlösenord finns.

## Lösenord, länkar och Firefox i 0.4.0

Poster lagras i `items.gpg`, krypterad med huvudlösenordet via samma GnuPG-anrop (AES-256/OCB, autentiserat) som innehållsförteckningen. Filen innehåller valvets id och avvisas i ett annat valv. Den binds inte till katalogens hash, så någon med skrivåtkomst till valvmappen kan byta tillbaka till en äldre giltig `items.gpg` från samma valv (återspelning). Det avslöjar inget lösenord men kan återställa gamla poster.

I en upplåst session är varje lösenord AES-GCM-förseglat i minnet med en slumpad sessionsnyckel. Visa/kopiera kräver huvudlösenordet igen, jämfört i konstant tid; tre fel låser valvet. Detta skyddar mot någon som sätter sig vid en upplåst dator och mot oavsiktlig visning. **Det skyddar inte mot skadlig kod som körs som din Windows-användare** medan valvet är upplåst: sådan kod kan läsa processminne, tangenttryckningar, urklipp eller ifyllda webbformulär. Huvudlösenordet finns i processens minne under upplåst session för att nya poster ska kunna sparas.

Firefox-tillägget pratar bara med native-messaging-värden `se.nordvault.firefox`, som är NordVault.exe startad av Firefox. Värden vidarebefordrar endast till den körande NordVault över en namngiven pipe som bara ditt Windows-konto kan öppna, och appen accepterar bara klienter vars körbara fil är samma NordVault.exe. Webbplatsens ursprung tas från Firefox, inte från sidan. Ett sparat lösenord lämnar appen endast efter att du bekräftat ifyllningen i NordVaults eget fönster, och bara för en sida som matchar postens webbplats. Ifyllda lösenord finns därefter i webbsidans formulär, där sidans eget skript kan läsa dem; fyll därför bara i på sidor du litar på. Tillägget är osignerat i denna förhandsversion.

## Brevlådan i 0.4.4: spara från Firefox när valvet är låst

Vid första upplåsningen låter NordVault GnuPG skapa ett nyckelpar (ed25519 för certifiering, RSA-3072 för kryptering; GnuPG 2.5.24 kan inte exportera och importera ECC-krypteringsnycklar utan att de blir stubbar). Den hemliga nyckeln, tillsammans med en kopia av den offentliga, ligger i `inbox-key.gpg`, krypterad med valvnyckeln. Den offentliga nyckeln ligger okrypterad i `inbox.pub`.

När valvet är låst krypteras en sparad inloggning (webbplats, användarnamn, lösenord, tid) till `inbox.pub` med autentiserad AES-256/OCB och läggs som `inbox\<slump>.gpg`. Ingen lösenfras behövs och inget kan läsas tillbaka utan valvet. Vid upplåsning jämförs `inbox.pub` med kopian i valvet; har den bytts ut återställs den och en varning visas, eftersom något med skrivåtkomst till mappen då har försökt få framtida inloggningar krypterade till sin egen nyckel. Appen använder i första hand den nyckel som kontrollerades vid senaste upplåsningen i samma körning.

Brevlådan är inte signerad: den som kan skriva i valvmappen kan lägga dit påhittade inloggningar. Därför läggs inget in utan att du godkänner listan. Filer som inte går att verifiera tas bort. Högst 200 inloggningar och 16 KiB per post.

## Valvnyckel och kapslar i 0.4.0

Katalog, `items.gpg` och valvfiler krypteras med valvnyckeln som GnuPG-lösenfras. Nya valv får 64 slumpade hexadecimala tecken (256 bitar). Valv från 0.3.x behåller huvudlösenordet som valvnyckel för bakåtkompatibilitet; deras styrka mot den som har en kopia av valvmappen är därför fortfarande huvudlösenordets.

Kapslar:

- **Huvudlösenord och återställningskoder:** GnuPG-krypterade JSON-dokument med valvets id och nyckel. Var och en ger angriparen ett separat mål för gissningar. Huvudlösenordet har minst 16 tecken; koderna har cirka 200 bitar slump.
- **Windows Hello:** en slumpad AES-256-GCM-nyckel krypterar valvnyckeln. AES-nyckeln är RSA-krypterad (PKCS#1 v1.5) med en Microsoft Passport-nyckel som inte kan exporteras, hör till Windows-kontot och kräver Hello-gest vid varje avkryptering.

Att Hello efter omstart kräver huvudlösenordet först är en bekvämlighetsregel som lagras i appens Data-mapp; den är inget kryptografiskt skydd. Byte av huvudlösenord skriver bara om lösenordskapseln. Gamla kopior av mappen kan därför fortfarande öppnas med det gamla lösenordet; byt valvnyckel genom att flytta innehållet till ett nytt valv. Hello-implementationen har inte kunnat provas via fjärrskrivbord under utvecklingen.

## Låsning, hälsa och läckkontroll

Valvet låses vid Windows-lås, utloggning, frånkopplad session, viloläge och skärmsläckare (SystemEvents och SPI_GETSCREENSAVERRUNNING). Lösenordshälsa jämför HMAC-SHA-256 under en sessionsnyckel och lagras inte. Läckkontrollen skickar 5 hexadecimala tecken av SHA-1 per lösenord till api.pwnedpasswords.com med `Add-Padding`, endast efter uttryckligt klick. SHA-1-värdena hålls bara i minnet under kontrollen.

Bryggan mot Firefox godtar endast https (och http för localhost), begränsar fill till 6 och query/länksök till 60 per minut, och returnerar högst 15 länkar per sökning. Registerposten för native messaging kontrolleras vid varje start: rätt manifestfil, rätt exe, exakt ett tillåtet tillägg och ingen HKLM-post.

## Självuppackande arkiv och installation

Arkivet innehåller en publicerad Windows x64-läsare med egen runtime och GnuPG samt en ZIP-envelop med enbart krypterad katalog och krypterade objekt. Vid skapande kontrolleras läsarens hash mot ett manifest inbyggt i appen. Vid läsning kontrolleras footergränser och transporthash, strikt ZIP-innehåll, autentiserad katalog, säkra filnamn och bundna objekthashar. Footerhashen ensam bevisar inte autenticitet; den kan räknas om av en angripare.

Uppackning håller krypterade arbetskopior i privat temporärmapp och verifierade klartextfiler i en privat `NV-*`-mapp under vald resultatplats. Först när alla filer godkänts flyttas de till en ny resultatmapp. Inga befintliga resultat skrivs över och ingen uppackad fil körs. Normal städning tar bort arbetsmappar; avbrott eller nekad borttagning kan lämna klartext. Välj därför en krypterad resultatvolym. Runtimefiler kan bli kvar i .NET:s offentliga `%TEMP%\.net`-cache.

Installern verifierar filstorlekar och SHA-256 mot sitt inbyggda manifest innan programmet publiceras i en tom undermapp. Detta är integritetskontroll inom en betrodd installerare, inte signering eller bevis för att installeraren är betrodd. Den egna appen, läsaren och Setup är osignerade. AllUsers eller Program Files kräver UAC; normalt användar-/portabelt läge är oprivilegierat. Diskhantering behöver separat administratörsstart.

Avinstalleraren tar endast bort kända programfiler som fortfarande matchar manifestet och bevarar egna/ändrade filer och valvet. Den använder en temporär kopia av sig själv när den startats i målprogrammet. Windows-register ändras bara för den installation vars plats stämmer. Windows Shell Link-gränssnitt används för genvägar; mål och argument läses tillbaka och en annan befintlig genväg skrivs inte över. Riktiga genvägar provas i isolerade mappar. Global installation, UAC, registerintegration och temporär avinstallationshjälpare återstår att kvalificera manuellt.

Uppdatering i 0.3.2 använder inbyggda föregående filmanifest och kända Setup-hashvärden för de verifierade 0.3.0- och 0.3.1-byggena. En gammal markör ensam ger ingen rätt att radera godtyckliga filer. Programfiler kontrolleras innan byte; egna kollisioner och ändrade programfiler stoppar åtgärden. Kända gamla programfiler flyttas till privat backup medan nya filer publiceras. Normala fel återställer gamla filer och bevarar okända filer. En namngiven semaphore för installationsmålet hindrar samtidiga operationer i samma Windows-session. Process-/strömavbrott kan lämna en ofullständig programinstallation och en `NV-*`-backup som behöver manuell kontroll. Data-mappar och valv flyttas inte av uppdateringen.

Den som kan ändra installeraren eller läsarens körbara kod kan också försöka fånga inmatat lösenord. Distribuera därför körbar kod genom en betrodd kanal. Varken ett lösenord på arkivet eller en hashfil från samma okända avsändare löser detta förtroendeproblem. Ingen extern säkerhetsrevision eller kodsigneringscertifikat har lagts till.

## Rensning och filförstöring

Windows har ett särskilt BitLocker-anrop för att rensa ledigt utrymme på en krypterad volym. Ett lyckat startanrop innebär att en operation har begärts; aktuell status måste läsas separat innan gränssnittet får ange att den slutförts. [WMI Encrypt](https://learn.microsoft.com/en-us/windows/win32/secprov/encrypt-win32-encryptablevolume), [GetConversionStatus](https://learn.microsoft.com/en-us/windows/win32/secprov/getconversionstatus-win32-encryptablevolume).

Filförstöringen har en snävare policy: endast en uttryckligen vald vanlig fil på en identifierad HDD med NTFS. SSD, USB-flash och okänd medietyp avvisas för denna funktion. Det är en avsiktlig produktgräns, inte ett påstående att varje medietyp kan identifieras korrekt bakom alla USB-bryggor eller lagringskontroller. Symlänkar, junctions, andra reparsepunkter och andra osäkra mål ska avvisas av policykontrollerna.

SDelete är ett separat Microsoft-verktyg med egna licensvillkor. Dess lyckade returvärde är en uppgift från verktyget, inte en oberoende fysisk efterkontroll. Microsoft anger dessutom att SDelete inte rensar gamla filnamn i ledigt utrymme. [SDelete-dokumentation](https://learn.microsoft.com/en-us/sysinternals/downloads/sdelete).

Rensning av en fil eller en volyms lediga utrymme tar inte bort andra exemplar i säkerhetskopior, snapshots, molnsynkronisering eller tidigare exporter. Kryptera ett nytt medium innan känslig data skrivs dit. Sanering inför försäljning eller kassering är en separat process som ska väljas efter medietyp och enhetens dokumenterade stöd. NordVault implementerar ingen firmwarebaserad NVMe/ATA-sanering eller generell kryptografisk radering av ett helt medium och gör inget påstående om NIST-certifiering. [NIST SP 800-88 Rev. 2](https://csrc.nist.gov/pubs/sp/800/88/r2/final).

## Återställning och säkerhetskopior

Återställningslösenordet kan ge åtkomst till volymen utan den vanliga PIN-koden eller lösenfrasen. Förvara det som en hemlighet på en annan fysisk enhet, offline och på en plats som inte försvinner tillsammans med den skyddade datorn eller disken. NordVault läser tillbaka den exporterade filen före start; det kontrollerar exporten men är inte ett fullständigt återställningsprov.

En säkerhetskopia av återställningsnyckeln är inte en säkerhetskopia av innehållet. Behåll även en separat kopia av viktiga filer. Prova faktisk återställning med testdata på en reservvolym eller i en isolerad VM innan viktiga data flyttas till produkten. Om alla giltiga upplåsnings- och återställningsvägar förloras kan NordVault inte kringgå BitLocker. [Microsofts beskrivning av återställningsmaterial](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/faq#key-management).

Windows- eller organisationspolicy kan ha egna återställningskopior i exempelvis ett konto eller en katalogtjänst. Att NordVault sparar en lokal fil betyder inte att sådana kopior saknas.

## Hantering av hemligheter och diagnostik

Gränssnittets lösenord och PIN-koder går via en omdirigerad standard input-pipe till en separat PowerShell-process. De läggs inte i processargument eller i en tillfällig begärandefil av `BackendClient`. Rå PowerShell-felutdata visas inte direkt. NordVault har ingen implementerad telemetri eller automatisk överföring av lösenord, nycklar eller filinnehåll. Detta är en beskrivning av appen, inte en garanti om Windows, externa verktyg, kontopolicy eller datorns övriga program.

Den aktuella implementationen använder vanliga .NET-strängar för `BackendRequest.Password`, `BackendRequest.Pin`, JSON och `RecoveryMaterial.RecoveryPassword`. Hemligheterna finns därför tillfälligt som klartext i appens och PowerShell-processens hanterade minne och i pipens buffertar. Garbage collection innebär inte garanterad omedelbar nollställning, och senare konvertering till `SecureString` tar inte bort tidigare strängkopior. Den exporterade återställningsfilen är dessutom avsiktligt läsbar för återställning. Programmet gör inget löfte om skydd mot minnesdumpar, swap, kraschdumpar eller en administratör som undersöker de här processerna.

Aktivitetsbesked ska innehålla operation och resultat, aldrig lösenord eller återställningsnycklar. Fel efter en påbörjad operation måste leda till uppdaterad diskstatus; ett felmeddelande bevisar inte att ingen förändring hann ske. BitLocker kan fortsätta arbeta efter att gränssnittet stängts.

## Verifieringsnivå

Den här förhandsversionen saknar oberoende säkerhetsgranskning och genomförda integrationstester på riktiga krypterade diskar. Automatiska policy- och kodtester kan kontrollera exempelvis avvisade mål, volymidentitet och anropsformat, men styrker inte att en TPM-upplåsning, volymkonvertering, strömavbrottsåterhämtning eller fysisk radering fungerar på en viss dator.

Före användning för viktiga data behövs dokumenterade prov av systemstart, återställning, omstart under kryptering, olika Windows-policyer, HDD/SSD/USB, fel och avbrott samt externa granskningar av privilegierade kodvägar. Det finns ingen utfästelse om buggfrihet, likvärdighet med Symantec PGP Whole Disk Encryption eller överlägsenhet jämfört med kommersiella produkter. Den historiska kodgranskningen och testkraven finns i [LEGACY-REVIEW.sv.md](LEGACY-REVIEW.sv.md).
