# Matematisk designkontroll av NordVault 0.3.0

Detta är en intern analys av konstruktionen och dess antaganden. Ingen extern matematiker har deltagit, och detta är varken en oberoende säkerhetsrevision eller ett bevis för att programmet är oknäckbart. NordVault implementerar inga egna chiffer: BitLocker används för volymer och GnuPG 2.5.24 för filer.

## Lösenordets styrka

Generatorns alfabet har exakt 64 olika ASCII-tecken. Varje tecken väljs separat med `.NET RandomNumberGenerator.GetInt32(64)`. För 24 tecken är antalet möjliga lösenord `64^24 = 2^144`. Under antagandet om oberoende, likformiga och hemliga val är entropin alltså 144 bitar. En uttömmande klassisk sökning behöver i genomsnitt ungefär `2^143` försök om lösenordet kommer från denna fördelning och angriparen saknar andra ledtrådar. Detta räknar alternativ; det är ingen tidsuppskattning eller garanti om datorns slumptalsgenerator.

Ett eget lösenord med 24 tecken kan vara mycket svagare. Längdkravet 16 tecken bevisar ingen minsta entropi; förutsägbara fraser och återanvända lösenord kan gissas. Att AES använder en 256-bitars nyckel ger inte ett 144-bitars slumpat lösenord 256 bitars entropi. Generatorns hemlighet får inte publiceras, återanvändas eller sparas tillsammans med den krypterade filen.

## Härledning och filkryptering

NordVault begär AES-256, OCB, saltad och itererad S2K typ 3, SHA-512 och S2K-räknevärdet 65 011 712. Räknevärdet avser bytebearbetning enligt S2K-formatet, **inte** 65 miljoner fullständiga lösenordshashningar. GnuPG anger dessa alternativ i [OpenPGP Options](https://gnupg.org/documentation/manuals/gnupg/OpenPGP-Options.html). NordVault skriver ingen egen implementation av dem. S2K är inte en minneskrävande Argon2-härledning; ett starkt slumpat lösenord är därför en central del av denna versions design.

GnuPG hanterar slump, nycklar, salt, noncer och OCB-paket. NordVault ska inte välja eller återanvända noncer själv. [OCB:s specifikation och säkerhetsdiskussion](https://datatracker.ietf.org/doc/html/rfc7253#section-5) bygger bland annat på att noncer inte återanvänds med samma nyckel. En matematisk analys av OCB är inte ett bevis för att GnuPG, Windows eller appens kod saknar fel. Testet att upprepade krypteringar ger olika resultat upptäcker vissa regressionsfel men bevisar inte slumptalskvalitet.

Autentisering kontrollerar det krypterade innehållet och ger mer än enbart hemlighållande. NordVault kräver motorns lyckade avslut, strukturerad AES-256/OCB-status och lyckad autentisering, samt avvisar flera krypteringsströmmar i en fil. Hela innehållet kontrolleras innan första klartextfilen skapas. Därefter görs en andra genomgång medan källfilen hålls öppen utan delad skriv- eller raderingsåtkomst. Manipulations- och avklippningstester är konkreta funktionsprov, inte en formell verifiering av alla möjliga filformat eller angrepp.

## Valv och självuppackande arkiv

Valvets och arkivets innehållsförteckning är autentiserat krypterad med samma GnuPG-inställningar. Varje post binder ett slumpat objekt-ID till filnamn, storlek och SHA-256 av objektets kryptotext. Ett annat giltigt krypterat objekt under samma lösenord ska därför ändå avvisas om det inte matchar den autentiserade posten. Både GnuPG-autentisering och hashbindning måste kontrolleras; en vanlig hash bredvid ett oskyddat namn vore inte samma konstruktion.

Arkivets ytterfooter innehåller SHA-256 för ZIP-payloaden. Den upptäcker transportskador men är **inte autentisering**: en angripare kan byta payload och räkna om hashvärdet. Hemlighet och autentisering ligger i den krypterade katalogen och objekten. Läsarens EXE-kod är offentlig och kan i sin tur bytas av en angripare; mottagaren behöver en betrodd leverans av körbar kod. SHA-manifest i appen upptäcker ändrade beroenden bara under antagandet att appen själv är betrodd.

Katalogbackup och låsning ger ingen fullständig rollback-detektion. Någon som kan ersätta **hela** ett valv med en äldre giltig kopia kan återföra det till äldre innehåll. Den här versionen har ingen extern betrodd räknare eller revisionslogg. Katalogen döljer filnamn men antalet objekt och storleksmönster syns fortfarande.

Varje objekt har separat salt och krypteringsmaterial som GnuPG skapar. Samma huvudlösenord används inom ett valv; det gör ett starkt huvudlösenord centralt och innebär att ett känt huvudlösenord öppnar samtliga objekt. Slumpade objekt-ID:n är namnseparering, inte krypteringsnycklar.

## Diskkryptering är ett annat skydd

BitLocker begär XTS-AES-256 för hela valda volymen. XTS är ett lämpligt läge för sektorkryptering men är inte filmodulens autentiserade OCB-läge. Diskfunktionen ger inget generellt löfte att upptäcka manipulerat innehåll. TPM + PIN, återställningsnyckel, Windows startkedja och fysisk enhet är separata delar av systemets säkerhet. Filgeneratorns 144-bitarsberäkning säger ingenting om styrkan hos en användarvald PIN eller återställningshantering.

## Varför inget löfte om ”oknäckbar”

Angriparen kan angripa lösenordet, det upplåsta systemet, RAM, säkerhetskopior, beroenden eller användarens arbetsflöde. Ett starkt chiffer löser inte dessa problem. Nycklar och klartext förekommer tillfälligt i processminne; NTFS-åtkomstregler skyddar inte mot administratör. Dekryptering till en okrypterad disk kan lämna klartext i lagringsceller även efter vanlig radering.

Den granskade konstruktionen ger ett motiverat val av etablerade motorer, en beräknad lösenordsrymd under uttryckliga antaganden och testade felvägar. Den ger inget bevis för buggfrihet, överlägsenhet mot Symantec, framtida kryptografiska genombrott eller skydd mot alla kvantdatorangrepp. Nästa kvalificeringssteg är en verkligt oberoende granskning och tester enligt [QUALIFICATION.sv.md](QUALIFICATION.sv.md).
