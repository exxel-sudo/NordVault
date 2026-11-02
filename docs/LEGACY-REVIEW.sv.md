# Granskning av PGP 2.6.3i

Granskad kopia: `legacy/pgp263i`, uppackad från `pgp263is.tar.gz`. Detta är en statisk läsning av centrala kodvägar, inte en fullständig säkerhetsrevision eller ett intyg om paketets autenticitet. Originalfilerna har inte ändrats. Hänvisningarna nedan anger fil och enbaserade radnummer i den uppackade kopian.

## Vad källkoden faktiskt innehåller

`legacy/pgp263i/readme.1st:5–13` beskriver versionen som ett program för meddelandekryptering och digitala signaturer, med datumet 1996-01-18. `readme.1st:19–27` anger att 2.6.3i är en internationellt modifierad variant byggd på MIT PGP 2.6.2 och att den medföljande användarhandboken gäller den äldre basversionen. Handbokens juridiska och tekniska bedömningar måste därför läsas som historiska uppgifter.

Byggfilen `legacy/pgp263i/src/makefile:138–152` länkar ett användarprogram av moduler för filer, nyckelringar, kryptografi, textkodning och komprimering. Denna kodbas innehåller inte Symantec PGP Whole Disk Encryption. I det granskade källträdet finns ingen identifierad Windows-diskfilterdrivrutin, startladdare, förstartsinloggning, TPM-integration eller återupptagningsbar sektorvis volymkonvertering. Sökning efter `DriverEntry`, `IRP_`, `FltRegisterFilter`, `IoCreateDevice`, `bootloader` och `whole disk` gav inga motsvarande komponenter. Negativa sökresultat är stöd för den slutsatsen, inte ensamma ett bevis; de stämmer med byggfilens moduler och de filbaserade gränssnitten.

Det finns heller ingen identifierad funktion som rensar ett helt filsystems lediga utrymme. Funktionen `wipefile()` behandlar en namngiven befintlig fil; den söker inte efter tidigare raderade filer eller oanvända sektorer.

## Konkreta fynd i kryptografin

| Fynd | Kodbelägg | Konsekvens för den nya produkten |
| --- | --- | --- |
| Symmetrisk filkryptering använder IDEA i CFB-läge. | `src/crypto.c:535–605`; `src/idea.h:10–11` anger 16 bytes nyckel och 8 bytes block. | Denna filrutin är ingen modern helvolymmotor och ska inte flyttas över som säkerhetsgrund. |
| Lösenfrasen reduceras direkt till 16 bytes med MD5. Inget salt eller arbetskostnadsparameter finns i funktionen. | `src/passwd.c:46–54`; anropet på `src/passwd.c:103`. | Använd inte den gamla lösenfrashanteringen i en ny implementation. Välj en etablerad motor vars nyckelskydd och återställning kan granskas och testas. |
| Filkryptot jämför två upprepade prefixbytes som kontroll av nyckeln. Funktionen innehåller ingen autentisering av hela kryptotexten. | `src/crypto.c:553–579`, därefter dataflödet på `583–597`. | Två kontrollbytes är inte en äkthetskontroll av filinnehållet. Beskriv varje modern motors verkliga integritetsskydd separat; anta inte att volymkryptering automatiskt är autentiserad. |
| En slumpmässig sessionsnyckel krypterar filen och kapslas med RSA. | `src/crypto.c:2414–2426`, `2580–2581`, `2476–2480`. | Separation mellan datanyckel och nyckelskydd är en användbar arkitekturprincip. Återanvänd principen, inte den gamla RSA-paketkodningen. |
| En särskild debugvariant kan skriva sessionsnyckeln till utdata. | `src/crypto.c:2571–2579`, bakom `MR_DEBUG`. | Hemligheter måste hållas borta från loggar, felrapporter och releasebyggen. |
| Rensning av hemligt minne görs med vanlig `memset`. | `src/usuals.h:35–39`; lösenfrasbuffertar på `src/passwd.c:104–105`. | En modern implementation behöver rensningsprimitiver med dokumenterade garantier och minimerad livslängd för hemligheter. Det kräver granskning av den faktiskt byggda binären; den här läsningen bevisar inte vad en viss äldre kompilator optimerade bort. |

Den synliga MD5-koden gäller både äldre protokoll och lösenfrashantering. Fyndet om lösenfrasen ovan grundar sig på funktionen och dess anrop, inte på ett antagande att varje förekomst av MD5 har samma roll. Ingen ny attack har utförts mot det äldre programmet.

## Konkreta fynd i filraderingen

`legacy/pgp263i/src/fileio.c:220–278` skriver över filens logiska innehåll i en omgång. Mönstret genereras deterministiskt från filstorleken, se `fileio.c:188–208` och `227–244`; det är inte en kryptografisk slumpkälla. För överskrivning är verifierad skrivning och mediets verkliga beteende viktigare än att mönstret ser slumpmässigt ut.

Det allvarligaste synliga felhanteringsfyndet är följande kedja:

1. `fileio.c:266–275` ignorerar uttryckligen returvärdena från `fwrite()`.
2. `wipefile()` på `fileio.c:285–294` ignorerar resultatet av `fclose()` och returnerar framgång så snart filen kunde öppnas.
3. Kommandovägen på `legacy/pgp263i/src/pgp.c:967–974` raderar sedan filnamnet och skriver att filen har förstörts när `wipefile()` returnerar noll.

Ett skrivfel efter öppningen kan alltså ge ett framgångsbesked och borttagning trots att hela filen inte skrevs över. Detta är ett härlett fel från den lästa kodvägen; det har inte verifierats genom att köra historiska binärer.

Koden kontrollerar inte heller returvärdena från `fseek()` och `ftell()` på `fileio.c:227–233`. Längden lagras i `unsigned long`; på vanliga Windows C-ABI:er är den 32 bitar. En mekanisk portning är därför inte en lösning för dagens mycket stora filer och volymer.

Författarna dokumenterade redan då begränsningar: `fileio.c:174–184` beskriver kopior i swap och filsystem som skriver uppdateringar på en annan plats. Den nya produkten måste därför skilja mellan:

- **Logisk överskrivning av en vald fil:** vad operativsystemets filgränssnitt faktiskt skrev och verifierade.
- **Rensning av ledigt utrymme på en volym:** en annan operation med egen kapacitetsreserv, avbrottshantering och verifiering.
- **Sanering av ett helt lagringsmedium:** en åtgärd som måste använda rätt stöd från lagringsenheten och som är avsiktligt destruktiv.

På SSD kan programmet inte härleda fysisk sanering enbart från att samma filoffset skrevs över. Snapshots, hårda länkar, alternativa dataströmmar, filsystemsmetadata, synkroniserade kopior och säkerhetskopior kan dessutom innehålla andra exemplar. Den här granskningen visar inget i den äldre filrutinen som hanterar dessa fall. En modern användarvy får därför aldrig likställa ett lyckat filanrop med bevis på att alla fysiska kopior är borta.

## Licens och namn

Detta avsnitt återger uppgifter i det bevarade paketet och är inte en bedömning av dagens lagstiftning, varumärkesägare eller patentstatus.

`legacy/pgp263i/doc/pgpdoc2.txt:2250–2258` uppmuntrar granskning, kompilering och experiment och anger att författaren inte begränsar ändring av källkoden för egen användning. Samma text begränsar spridning av modifierade versioner under namnet PGP. På `2274–2284` beskrivs även märkning av modifierat program och material som det producerar. På `2292–2315` säger handboken uttryckligen att den aktuella freewareversionen inte släpps under GPL och diskuterar dåtidens RSAREF-villkor.

`legacy/pgp263i/src/makefile:99–108` visar två alternativa RSA-bakändar: MPILIB/`rsaglue1.o` och RSAREF/`rsaglue2.o`. Det är därför fel att behandla alla delar och alla byggvarianter som ett enda modernt open source-paket. Källfilerna har också egna upphovsrättsnotiser, till exempel `src/crypto.c:4–19`, medan `src/md5.c:4–9` anger andra villkor för den filen.

Den versionsspecifika `legacy/pgp263i/readme.1st:300–310` diskuterar dessutom hur borttagning av en kompatibilitetsbegränsning förhåller sig till basversionens MIT-villkor. `readme.1st:313–318` anger dåtidens begränsning till icke-kommersiell användning och en IDEA-licens för kommersiell användning. Detta är historiska licensuppgifter i distributionen; de är inte verifierade aktuella rättsliga slutsatser för 2026.

Praktiskt beslut: behåll paketet separat som historiskt studiematerial, bevara dess dokumentation och notices, och kopiera inte dess implementation till den nya säkerhetsmotorn. Ge den nya produkten ett eget namn och dokumentera de komponenter som den faktiskt använder. Att ett paket kan hämtas på nätet innebär inte i sig att det har fri licens.

## Designbeslut för en modern implementation

1. Använd en etablerad volymmotor för transparent diskkryptering. Lägg egen kod i en tydlig kontroll- och presentationsdel; skriv inte en egen startladdare eller diskdrivrutin med denna gamla filkod som grund.
2. Skilj kartläggning, planering, godkännande och utförande. Ett åtgärdsförslag måste identifiera aktuell volym/enhet och kontrolleras igen direkt före körning.
3. Behandla varje skriv-, flush- och stängningsfel som misslyckande. Radera inte filnamnet efter en ofullständig överskrivning och rapportera alltid vad som hann ändras.
4. Begränsa privilegier och åtkomst. Programmet ska normalt kunna visa status utan administratörsrättigheter; privilegierade åtgärder ska vara små och explicita.
5. Återställning måste ingå från början. Bevis på att en återställningsväg fungerar är ett produktkrav, inte en checkruta efter att kryptering startat.
6. Visa endast styrkta säkerhetsegenskaper. Krypteringsmotor, skyddsläge, medietyp, testnivå och kvarvarande begränsningar ska vara begripliga. Begrepp som ”buggfri” och ”bättre än Symantec” behöver ersättas av jämförbara, verifierade kriterier.

## Meningsfulla säkerhetstester

Tester som kan utföras utan att ändra användarens riktiga data ska komma först. En lista nedan är ett testkrav, inte ett påstående att testet redan har utförts.

| Test | Förväntat resultat |
| --- | --- |
| Standardstart och statusvisning. | Ingen filradering, ingen volymkonvertering och ingen förändring av krypteringsskydd utan en uttrycklig åtgärd. |
| Underprocessanrop med enhetsnamn och filvägar som innehåller citattecken, Unicode, mellanslag och shellmetatecken. | Endast tillåtna argument når motorn; inget sekundärt kommando exekveras. |
| Volymidentitet ändras mellan planering och utförande, exempelvis genom att byta ett USB-medium som har samma enhetsbokstav. | Åtgärden avbryts; enhetsbokstaven ensam räcker inte som identitet. |
| Malformerad status, otillgänglig motor, saknade rättigheter eller okänd medietyp. | Status blir okänd eller fel, aldrig falskt ”skyddad” eller ”säkert raderad”. |
| Återställningsmaterial som saknas, är ofullständigt eller inte kan läsas tillbaka. | Start av den skyddade åtgärden blockeras enligt produktens policy; hemligheter skrivs inte till loggar. |
| Injicerad kort skrivning, fullt filsystem och flushfel i filöverskrivning. | Misslyckande rapporteras och filnamnet lämnas kvar; inga historiska PGP-framgångsbesked återkommer. |
| Hård länk, symlink/junction, reparsepunkt, alternativ dataström, sparsefil och komprimerad fil. | Varje understött fall behandlas korrekt; övriga avvisas med en konkret begränsning och utan att annat mål träffas. |
| HDD- respektive SSD-policy med okänd eller motsägande lagringsinformation. | SSD får inget påstående om garanterad fysisk filsanering. Okänt medium behandlas konservativt. |
| Rensning av ledigt utrymme på en liten tillfällig testvolym. | Reservutrymme bevaras, avbrott fungerar och städning tar endast bort programmets egna temporära filer. |
| Kryptotext/fildata ändras, kapas eller flyttas i isolerad testmiljö. | Resultatet följer den valda motorns dokumenterade skydd; användargränssnittet lovar ingen integritetsegenskap som saknas. |
| VM omstartas eller strömförlust simuleras under volymkonvertering. | Återhämtning, återupptagning och dekryptering verifieras utan förlorade testdata. Kräver separata VM-/diskbilder. |
| Förstarts- och återställningsflöde på VM med olika firmware och TPM-inställningar. | Normal upplåsning och återställning fungerar, och oväntad konfiguration leder till tydligt fel. |
| Loggar och diagnostik granskas efter lyckade och misslyckade körningar. | Inga lösenord, sessionsnycklar eller återställningsnycklar finns där. |

Fysisk SSD-sanering och HDD-återläsning efter överskrivning behöver dedikerade testmedier, oberoende återläsning och dokumenterad metod. Enhetstester av gränssnittet är användbara men kan inte ersätta dessa prov eller en extern säkerhetsgranskning av hela produkten.
